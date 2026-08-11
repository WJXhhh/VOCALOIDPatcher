using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;

namespace VOCALOIDPatcher.Mcp;

public static class McpBridgeService
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _cancellation;
    private static Task? _serverTask;
    private static Task? _maintenanceTask;
    private static Process? _httpCompanion;
    private static string? _instanceId;
    private static string? _pipeName;
    private static string? _handshakeToken;
    private static InstanceRegistration? _registration;

    public static bool IsRunning => _cancellation is { IsCancellationRequested: false };
    public static string? InstanceId => _instanceId;

    public static void Install()
    {
        if (Patcher.VstPluginMode)
            return;
        try
        {
            Settings.McpSettingsChanged += UpdateFromSettings;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            UpdateFromSettings();
        }
        catch (Exception exception)
        {
            Utils.Debug.Print($"MCP bridge installation failed safely: {exception.Message}");
            Stop();
        }
    }

    public static void UpdateFromSettings()
    {
        try
        {
            if (!Settings.McpEnabled || Patcher.VstPluginMode)
            {
                Stop();
                return;
            }

            Start();
            UpdateHttpCompanion();
        }
        catch (Exception exception)
        {
            Utils.Debug.Print($"MCP bridge settings update failed safely: {exception.Message}");
            Stop();
        }
    }

    public static object GetStatus() => new
    {
        enabled = Settings.McpEnabled,
        running = IsRunning,
        instance_id = _instanceId,
        http_enabled = Settings.McpHttpEnabled,
        http_port = Settings.McpHttpPort,
        http_running = _httpCompanion is { HasExited: false },
        access = McpAccessController.GetStatus(),
    };

    public static void Start()
    {
        lock (Gate)
        {
            if (IsRunning)
                return;

            using Process process = Process.GetCurrentProcess();
            _instanceId = Guid.NewGuid().ToString("N");
            _pipeName = $"VOCALOIDPatcher.Mcp.{process.Id}.{_instanceId}";
            _handshakeToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _registration = BuildRegistration(process);
            InstanceRegistry.Write(_registration);

            _cancellation = new CancellationTokenSource();
            _serverTask = RunServerAsync(_cancellation.Token);
            _maintenanceTask = RunMaintenanceAsync(_cancellation.Token);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            try
            {
                _cancellation?.Cancel();
                if (_httpCompanion is { HasExited: false })
                    _httpCompanion.Kill(true);
            }
            catch
            {
            }

            if (_instanceId != null)
                InstanceRegistry.Remove(_instanceId);
            _httpCompanion?.Dispose();
            _httpCompanion = null;
            _cancellation?.Dispose();
            _cancellation = null;
            _serverTask = null;
            _maintenanceTask = null;
            _registration = null;
            _instanceId = null;
            _pipeName = null;
            _handshakeToken = null;
            McpAccessController.RevokeAll();
        }
    }

    public static void RefreshRegistration()
    {
        lock (Gate)
        {
            if (!IsRunning || _registration == null)
                return;
            try
            {
                using Process process = Process.GetCurrentProcess();
                _registration = BuildRegistration(process);
                InstanceRegistry.Write(_registration);
            }
            catch
            {
            }
        }
    }

    public static void RestartHttpCompanion()
    {
        lock (Gate)
        {
            try
            {
                if (_httpCompanion is { HasExited: false })
                    _httpCompanion.Kill(true);
            }
            catch
            {
            }
            _httpCompanion?.Dispose();
            _httpCompanion = null;
        }
        UpdateHttpCompanion();
    }

    private static async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName!,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    64 * 1024,
                    64 * 1024);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Utils.Debug.Print($"MCP pipe accept failed: {exception.Message}");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                RefreshRegistration();
                UpdateHttpCompanion();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Maintenance is best-effort and must never affect the editor.
            }
        }
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            BridgeRequest? request = null;
            BridgeResponse response;
            try
            {
                request = await PipeMessageFraming.ReadAsync<BridgeRequest>(pipe, cancellationToken).ConfigureAwait(false);
                if (request.ProtocolVersion != BridgeProtocol.Version)
                    response = BridgeResponse.Failure(request.RequestId, "unsupported", "Unsupported bridge protocol version.");
                else if (!CryptographicOperations.FixedTimeEquals(
                             System.Text.Encoding.UTF8.GetBytes(request.HandshakeToken),
                             System.Text.Encoding.UTF8.GetBytes(_handshakeToken ?? string.Empty)))
                    response = BridgeResponse.Failure(request.RequestId, "permission_denied", "Invalid bridge handshake.");
                else
                    response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                response = BridgeResponse.Failure(request?.RequestId ?? string.Empty, "internal_error", exception.Message);
            }

            try
            {
                await PipeMessageFraming.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task<BridgeResponse> DispatchAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return BridgeResponse.Failure(request.RequestId, "v6_unavailable", "VOCALOID UI dispatcher is unavailable.", true);

        BridgeResponse response = await dispatcher.InvokeAsync(() =>
            VocaloidMcpFacade.Handle(request, cancellationToken));
        RefreshRegistration();
        return response;
    }

    private static InstanceRegistration BuildRegistration(Process process)
    {
        string? title = null;
        string? project = null;
        try
        {
            title = Application.Current?.MainWindow?.Title;
            project = App.Shared?.Document?.FileName;
        }
        catch
        {
        }

        return new InstanceRegistration(
            BridgeProtocol.Version,
            _instanceId!,
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            _pipeName!,
            _handshakeToken!,
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
            title,
            project,
            DateTimeOffset.UtcNow);
    }

    private static void UpdateHttpCompanion()
    {
        lock (Gate)
        {
            if (!Settings.McpHttpEnabled || !IsRunning)
            {
                try
                {
                    if (_httpCompanion is { HasExited: false })
                        _httpCompanion.Kill(true);
                }
                catch
                {
                }
                _httpCompanion?.Dispose();
                _httpCompanion = null;
                return;
            }

            if (_httpCompanion is { HasExited: false })
                return;
            _httpCompanion?.Dispose();
            _httpCompanion = null;

            try
            {
                if (Mutex.TryOpenExisting($"Local\\VOCALOIDPatcher.Mcp.Http.{Settings.McpHttpPort}", out Mutex? existing))
                {
                    existing.Dispose();
                    return;
                }
            }
            catch
            {
                // If the mutex cannot be inspected, let the companion enforce single-instance startup.
            }

            string path = Path.Combine(Patcher.DataDir, "mcp", "VOCALOIDPatcher.McpServer.exe");
            if (!File.Exists(path))
                return;
            try
            {
                _httpCompanion = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = $"--transport http --port {Settings.McpHttpPort}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception exception)
            {
                Utils.Debug.Print($"MCP HTTP companion failed to start: {exception.Message}");
            }
        }
    }
}
