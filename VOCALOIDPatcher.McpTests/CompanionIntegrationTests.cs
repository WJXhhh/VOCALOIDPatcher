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

        Assert.Equal(26, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "v6_get_state");
        Assert.Contains(tools, tool => tool.Name == "v6_g2pa_candidates");
        Assert.Contains(tools, tool => tool.Name == "v6_g2pa_apply");
        Assert.Contains(tools, tool => tool.Name == "v6_apply_operations");
        Assert.Contains(tools, tool => tool.Name == "v6_wait_event");
        Assert.Contains(tools, tool => tool.Name == "v6_wait_for");
        string editSchema = tools.Single(tool => tool.Name == "v6_edit_notes").JsonSchema.GetRawText();
        Assert.Contains("project_id", editSchema);
        Assert.Contains("expected_revision", editSchema);
        Assert.Contains("client_request_id", editSchema);
        foreach (string name in new[] { "v6_edit_notes", "v6_edit_structure", "v6_edit_parameters", "v6_apply_operations", "v6_select_view" })
        {
            string field = name == "v6_select_view" ? "request" : "operations";
            var parameter = tools.Single(tool => tool.Name == name).JsonSchema.GetProperty("properties").GetProperty(field);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, parameter.ValueKind);
            Assert.Empty(parameter.EnumerateObject());
        }
        var result = await client.CallToolAsync("v6_get_state", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Contains("fake-6.13", result.StructuredContent.Value.GetRawText());
        Assert.Contains("set_evec", tools.Single(tool => tool.Name == "v6_edit_notes").Description);
        var evecResult = await client.CallToolAsync("v6_edit_notes", new Dictionary<string, object?>
        {
            ["project_id"] = "fake-project",
            ["expected_revision"] = 1L,
            ["client_request_id"] = "evec-transport-check",
            ["dry_run"] = true,
            ["operations"] = new[] { new { op = "set_evec", track_index = 0, part_index = 0, note_index = 0,
                evec = new { voice_color_id = 101, attack_id = 302, release_id = 201, consonant_extension = 0 } } },
        });
        Assert.NotEqual(true, evecResult.IsError);
        Assert.Equal("v6_edit_notes", fake.LastRequest?.Method);
        var payload = fake.LastRequest!.Arguments!.Value;
        Assert.True(payload.GetProperty("dry_run").GetBoolean());
        var evec = payload.GetProperty("operations")[0].GetProperty("evec");
        Assert.Equal(101, evec.GetProperty("voice_color_id").GetInt32());
        Assert.Equal(302, evec.GetProperty("attack_id").GetInt32());
        Assert.Equal(201, evec.GetProperty("release_id").GetInt32());
        Assert.Equal(0, evec.GetProperty("consonant_extension").GetInt32());
        var stringResult = await client.CallToolAsync("v6_edit_notes", new Dictionary<string, object?>
        {
            ["project_id"] = "fake-project", ["expected_revision"] = 1L,
            ["client_request_id"] = "evec-string-transport-check", ["dry_run"] = true,
            ["operations"] = "[{\"op\":\"set_evec\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"evec\":{\"clear\":true}}]",
        });
        Assert.NotEqual(true, stringResult.IsError);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, fake.LastRequest!.Arguments!.Value.GetProperty("operations").ValueKind);
        Assert.True(fake.LastRequest.Arguments.Value.GetProperty("operations")[0].GetProperty("evec").GetProperty("clear").GetBoolean());
        var timingSchema = tools.Single(t => t.Name == "v6_edit_timing").JsonSchema.GetProperty("properties").GetProperty("edit");
        Assert.Equal("object", timingSchema.GetProperty("type").GetString());
        Assert.Contains("delta_frames", timingSchema.GetRawText());
        Assert.Contains("comp", timingSchema.GetRawText());
        var timingResult = await client.CallToolAsync("v6_edit_timing", new Dictionary<string, object?>
        {
            ["project_id"] = "fake-project", ["expected_revision"] = 1L, ["client_request_id"] = "timing-boundary",
            ["track_index"] = 0, ["part_index"] = 1, ["dry_run"] = true,
            ["edit"] = new { op = "boundary.move", note_index = 17, boundary_index = 1, delta_frames = -2 },
        });
        Assert.NotEqual(true, timingResult.IsError);
        Assert.Equal("v6_edit_timing", fake.LastRequest?.Method);
        var timing = fake.LastRequest!.Arguments!.Value.GetProperty("edit");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, timing.ValueKind);
        Assert.Equal(-2, timing.GetProperty("delta_frames").GetInt32());
        Assert.Equal(17, timing.GetProperty("note_index").GetInt32());
        var settingsResult = await client.CallToolAsync("v6_timing_settings", new Dictionary<string, object?>
        {
            ["project_id"] = "fake-project", ["expected_revision"] = 1L, ["client_request_id"] = "timing-settings",
            ["dry_run"] = true, ["settings"] = new { smoothing = false, crossfade_ms = 12.0, phase_alignment = false },
        });
        Assert.NotEqual(true, settingsResult.IsError);
        Assert.Equal("v6_timing_settings", fake.LastRequest?.Method);
        Assert.False(fake.LastRequest!.Arguments!.Value.GetProperty("settings").GetProperty("smoothing").GetBoolean());
        var compResult = await client.CallToolAsync("v6_edit_timing", new Dictionary<string, object?>
        {
            ["project_id"] = "fake-project", ["expected_revision"] = 1L, ["client_request_id"] = "timing-comp",
            ["track_index"] = 0, ["part_index"] = 1, ["dry_run"] = true,
            ["edit"] = new { op = "comp.set", note_index = 16, comp = new { body = new { length_ms = 1800, trim = "keep_tail",
                context = new { before = "t a", after = "Sil", note_ms = 550 } }, isolation = new { after = true, source_length_ms = 1600 } } },
        });
        Assert.NotEqual(true, compResult.IsError);
        Assert.Equal(1800, fake.LastRequest!.Arguments!.Value.GetProperty("edit").GetProperty("comp").GetProperty("body").GetProperty("length_ms").GetDouble());
        var compEditSchema = tools.Single(t => t.Name == "v6_edit_voice_comp").JsonSchema.GetProperty("properties").GetProperty("edit");
        Assert.Equal("object", compEditSchema.GetProperty("type").GetString());
        Assert.Contains("note_indices", compEditSchema.GetRawText());
        var batchResult = await client.CallToolAsync("v6_edit_voice_comp", new Dictionary<string, object?>
        {
            ["project_id"] = "fake-project", ["expected_revision"] = 1L, ["client_request_id"] = "comp-batch",
            ["track_index"] = 0, ["part_index"] = 1, ["dry_run"] = true,
            ["edit"] = new { op = "independent_sustain", note_indices = new[] { 16, 17 }, length_ms = 1800, fade_ms = 8 },
        });
        Assert.NotEqual(true, batchResult.IsError);
        Assert.Equal("v6_edit_voice_comp", fake.LastRequest!.Method);
        Assert.Equal(2, fake.LastRequest.Arguments!.Value.GetProperty("edit").GetProperty("note_indices").GetArrayLength());
        Assert.Equal(1800, fake.LastRequest.Arguments!.Value.GetProperty("edit").GetProperty("length_ms").GetDouble());
        var previewResult = await client.CallToolAsync("v6_preview_voice_comp", new Dictionary<string, object?>
        {
            ["project_id"] = "fake-project", ["expected_revision"] = 1L, ["client_request_id"] = "comp-preview",
            ["action"] = "source", ["track_index"] = 0, ["part_index"] = 1, ["note_index"] = 16, ["play"] = false,
        });
        Assert.NotEqual(true, previewResult.IsError);
        Assert.Equal("v6_preview_voice_comp", fake.LastRequest!.Method);
        Assert.False(fake.LastRequest.Arguments!.Value.GetProperty("play").GetBoolean());
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

            var tools = await client.ListToolsAsync();
            Assert.Equal(System.Text.Json.JsonValueKind.Object,
                tools.Single(tool => tool.Name == "v6_select_view").JsonSchema.GetProperty("properties").GetProperty("request").ValueKind);

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
        public BridgeRequest? LastRequest { get; private set; }
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
                    LastRequest = request;
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
