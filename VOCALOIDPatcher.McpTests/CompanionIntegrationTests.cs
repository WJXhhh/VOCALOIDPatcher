using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class CompanionIntegrationTests
{
    [Fact]
    public async Task StdioInitializesListsToolsAndCallsFakeBridge()
    {
        await using var fake = new FakeV6Bridge();
        await fake.StartAsync();
        string serverAssembly = FindServerAssembly();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = new[] { serverAssembly, "--transport", "stdio", "--instance", fake.InstanceId },
            Name = "v6patch-stdio-test",
        });

        await using McpClient client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        Assert.Equal(17, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "v6_get_state");
        Assert.Contains(tools, tool => tool.Name == "v6_g2pa_candidates");
        Assert.Contains(tools, tool => tool.Name == "v6_g2pa_apply");
        string editSchema = tools.Single(tool => tool.Name == "v6_edit_notes").JsonSchema.GetRawText();
        Assert.Contains("project_id", editSchema);
        Assert.Contains("expected_revision", editSchema);
        Assert.Contains("client_request_id", editSchema);
        var result = await client.CallToolAsync("v6_get_state", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Contains("fake-6.13", result.StructuredContent.Value.GetRawText());
    }

    [Fact]
    public async Task StreamableHttpAuthenticatesAndCallsFakeBridge()
    {
        await using var fake = new FakeV6Bridge();
        await fake.StartAsync();
        int port = ReservePort();
        const string token = "integration-test-token-with-at-least-32-characters";
        using Process companion = StartHttpCompanion(FindServerAssembly(), fake.InstanceId, port, token);
        try
        {
            await WaitForPortAsync(port, companion);
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                Name = "v6patch-http-test",
            });
            await using McpClient client = await McpClient.CreateAsync(transport);

            var result = await client.CallToolAsync("v6_get_state", new Dictionary<string, object?>());
            Assert.NotEqual(true, result.IsError);
            Assert.Contains("fake-6.13", result.StructuredContent!.Value.GetRawText());
        }
        finally
        {
            if (!companion.HasExited)
                companion.Kill(true);
        }
    }

    [Fact]
    public async Task StreamableHttpRejectsInvalidBearerToken()
    {
        await using var fake = new FakeV6Bridge();
        await fake.StartAsync();
        int port = ReservePort();
        const string token = "integration-test-token-with-at-least-32-characters";
        using Process companion = StartHttpCompanion(FindServerAssembly(), fake.InstanceId, port, token);
        try
        {
            await WaitForPortAsync(port, companion);
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer wrong" },
            });

            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await McpClient.CreateAsync(transport));
        }
        finally
        {
            if (!companion.HasExited)
                companion.Kill(true);
        }
    }

    private static Process StartHttpCompanion(string assembly, string instanceId, int port, string token)
        => Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                assembly,
                "--transport", "http",
                "--instance", instanceId,
                "--port", port.ToString(),
                "--token", token,
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("Could not start MCP Companion.");

    private static string FindServerAssembly()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "VOCALOIDPatcher.McpServer.dll");
        if (File.Exists(local))
            return local;
        throw new FileNotFoundException("The MCP Companion test assembly was not copied to the test output.", local);
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
                throw new InvalidOperationException("MCP Companion exited before opening HTTP: " + await process.StandardError.ReadToEndAsync());
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, timeout.Token);
            }
        }
        throw new TimeoutException("MCP Companion did not open its HTTP port.");
    }

    private sealed class FakeV6Bridge : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _server;

        public string InstanceId { get; } = Guid.NewGuid().ToString("N");
        private string PipeName => "VOCALOIDPatcher.Mcp.Test." + InstanceId;
        private string Token { get; } = Guid.NewGuid().ToString("N");

        public Task StartAsync()
        {
            using Process process = Process.GetCurrentProcess();
            InstanceRegistry.Write(new InstanceRegistration(
                BridgeProtocol.Version,
                InstanceId,
                process.Id,
                process.StartTime.ToUniversalTime().Ticks,
                PipeName,
                Token,
                "fake-6.13",
                "Fake VOCALOID",
                "Fake Project",
                DateTimeOffset.UtcNow));
            _server = RunAsync();
            return Task.CompletedTask;
        }

        private async Task RunAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_cancellation.Token);
                    BridgeRequest request = await PipeMessageFraming.ReadAsync<BridgeRequest>(pipe, _cancellation.Token);
                    object result = request.Method switch
                    {
                        "v6_get_state" => new
                        {
                            editor_version = "fake-6.13",
                            project = new ProjectContext(InstanceId, "fake-project", 1),
                        },
                        _ => new { accepted = true },
                    };
                    await PipeMessageFraming.WriteAsync(pipe, BridgeResponse.Success(request.RequestId, result), _cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            InstanceRegistry.Remove(InstanceId);
            if (_server != null)
            {
                try { await _server; }
                catch (OperationCanceledException) { }
            }
            _cancellation.Dispose();
        }
    }
}
