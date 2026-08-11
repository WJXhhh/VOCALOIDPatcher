using System.Security.Cryptography;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ModelContextProtocol.Protocol;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.McpServer;

CompanionOptions options;
try
{
    options = CompanionOptions.Parse(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

string token = options.ExplicitToken ?? HttpTokenStore.GetOrCreate();
if (options.PrintConfig)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        stdio = new { command = Environment.ProcessPath, args = new[] { "--transport", "stdio", "--instance", "auto" } },
        http = new { url = $"http://127.0.0.1:{options.Port}/mcp", authorization = $"Bearer {token}" },
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (options.Transport == "stdio")
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<BridgeGateway>();
    builder.Services.AddMcpServer(server => server.ServerInfo = new Implementation { Name = "vocaloid6", Version = "1.0.0" })
        .WithStdioServerTransport()
        .WithTools<VocaloidTools>()
        .WithResources<VocaloidResources>();
    await builder.Build().RunAsync();
    return 0;
}

using var httpMutex = new Mutex(true, $"Local\\VOCALOIDPatcher.Mcp.Http.{options.Port}", out bool ownsHttpMutex);
if (!ownsHttpMutex)
{
    Console.Error.WriteLine($"An MCP HTTP companion is already listening for port {options.Port}.");
    return 3;
}

var webBuilder = WebApplication.CreateBuilder(args);
webBuilder.Logging.ClearProviders();
webBuilder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Information);
webBuilder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, options.Port);
    kestrel.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
    kestrel.Limits.MaxConcurrentConnections = 64;
    kestrel.Limits.MaxConcurrentUpgradedConnections = 16;
    kestrel.Limits.MinRequestBodyDataRate = new MinDataRate(240, TimeSpan.FromSeconds(10));
});
webBuilder.Services.AddSingleton(options);
webBuilder.Services.AddSingleton<BridgeGateway>();
webBuilder.Services.AddMcpServer(server => server.ServerInfo = new Implementation { Name = "vocaloid6", Version = "1.0.0" })
    .WithHttpTransport(http =>
    {
        http.Stateless = false;
        http.IdleTimeout = TimeSpan.FromMinutes(15);
        http.MaxIdleSessionCount = 64;
    })
    .WithTools<VocaloidTools>()
    .WithResources<VocaloidResources>();

var app = webBuilder.Build();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/mcp"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    string host = context.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost" or "::1"))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    string origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin)
        && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri)
            || originUri.Scheme is not ("http" or "https")
            || originUri.Host is not ("127.0.0.1" or "localhost" or "::1")))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    string supplied = context.Request.Headers.Authorization.ToString();
    string expected = "Bearer " + token;
    if (supplied.Length != expected.Length
        || !CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied),
            System.Text.Encoding.UTF8.GetBytes(expected)))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }

    await next();
});
app.MapMcp("/mcp");
await app.RunAsync();
return 0;
