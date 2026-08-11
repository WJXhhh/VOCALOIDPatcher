using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using VOCALOIDPatcher.McpBridge;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

namespace VOCALOIDPatcher.McpServer;

public sealed record PublicInstance(
    string InstanceId,
    int ProcessId,
    string EditorVersion,
    string? WindowTitle,
    string? ProjectName,
    DateTimeOffset RegisteredAtUtc);

public sealed record McpBridgeResult(bool Ok, JsonElement? Result = null, BridgeError? Error = null)
{
    public static McpBridgeResult Failure(string code, string message, bool retryable = false, object? details = null)
        => new(false, Error: new BridgeError(
            code,
            message,
            retryable,
            details == null ? null : JsonSerializer.SerializeToElement(details, BridgeProtocol.JsonOptions)));
}

public sealed class BridgeGateway
{
    private sealed record SessionIdentity(string Id);

    private readonly CompanionOptions _options;
    private readonly BridgePipeClient _client = new();
    private readonly ConditionalWeakTable<McpServerInstance, SessionIdentity> _identities = new();

    public BridgeGateway(CompanionOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<PublicInstance> ListInstances()
        => InstanceRegistry.ReadLive()
            .Select(item => new PublicInstance(
                item.InstanceId,
                item.ProcessId,
                item.EditorVersion,
                item.WindowTitle,
                item.ProjectName,
                item.RegisteredAtUtc))
            .ToArray();

    public async Task<McpBridgeResult> InvokeAsync(
        McpServerInstance server,
        string method,
        string? requestedInstanceId,
        object? arguments,
        CancellationToken cancellationToken)
    {
        InstanceRegistration? instance;
        try
        {
            instance = ResolveInstance(requestedInstanceId);
        }
        catch (BridgeSelectionException exception)
        {
            return McpBridgeResult.Failure(exception.Code, exception.Message, details: exception.Details);
        }

        var info = server.ClientInfo;
        string clientName = info?.Name ?? "unknown-mcp-client";
        string clientId = _options.Transport == "stdio"
            ? StableStdioClientId(clientName, info?.Version)
            : _identities.GetValue(server, _ => new SessionIdentity(Guid.NewGuid().ToString("N"))).Id;
        var bridgeClient = new BridgeClientInfo(
            clientId,
            clientName,
            info?.Version,
            _options.Transport);

        try
        {
            BridgeResponse response = await _client.InvokeAsync(
                instance,
                method,
                arguments,
                bridgeClient,
                cancellationToken).ConfigureAwait(false);
            return new McpBridgeResult(response.Ok, response.Result, response.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return McpBridgeResult.Failure("cancelled", "The MCP request was cancelled.");
        }
        catch (TimeoutException exception)
        {
            return McpBridgeResult.Failure("v6_unavailable", exception.Message, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return McpBridgeResult.Failure("v6_unavailable", "Could not communicate with the selected VOCALOID instance.", true);
        }
    }

    private static string StableStdioClientId(string name, string? version)
    {
        byte[] identity = Encoding.UTF8.GetBytes($"{name}\0{version ?? string.Empty}\0stdio");
        return Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
    }

    public async Task<string> ReadResourceAsync(
        McpServerInstance server,
        string instanceId,
        string kind,
        CancellationToken cancellationToken)
    {
        McpBridgeResult result = await InvokeAsync(
            server,
            kind,
            instanceId,
            new { },
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, BridgeProtocol.JsonOptions);
    }

    private InstanceRegistration ResolveInstance(string? requestedInstanceId)
    {
        IReadOnlyList<InstanceRegistration> instances = InstanceRegistry.ReadLive();
        string? id = requestedInstanceId ?? _options.DefaultInstanceId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            InstanceRegistration? match = instances.FirstOrDefault(item =>
                string.Equals(item.InstanceId, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ProcessId.ToString(), id, StringComparison.Ordinal));
            if (match == null)
                throw new BridgeSelectionException("instance_not_found", "The requested VOCALOID instance is not running.", ListInstances());
            return match;
        }

        return instances.Count switch
        {
            0 => throw new BridgeSelectionException("v6_unavailable", "No MCP-enabled VOCALOID instance is running.", Array.Empty<PublicInstance>()),
            1 => instances[0],
            _ => throw new BridgeSelectionException("instance_ambiguous", "Multiple VOCALOID instances are running; supply instance_id.", ListInstances()),
        };
    }

    private sealed class BridgeSelectionException : Exception
    {
        public string Code { get; }
        public object Details { get; }

        public BridgeSelectionException(string code, string message, object details) : base(message)
        {
            Code = code;
            Details = details;
        }
    }
}
