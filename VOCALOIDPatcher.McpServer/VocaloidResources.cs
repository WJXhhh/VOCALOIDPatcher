using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using VOCALOIDPatcher.McpBridge;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

namespace VOCALOIDPatcher.McpServer;

[McpServerResourceType]
public sealed class VocaloidResources
{
    private readonly BridgeGateway _gateway;

    public VocaloidResources(BridgeGateway gateway)
    {
        _gateway = gateway;
    }

    [McpServerResource(Name = "vocaloid_instances", UriTemplate = "vocaloid://instances", MimeType = "application/json")]
    [Description("MCP-enabled VOCALOID instances visible to the current Windows user.")]
    public string Instances()
        => JsonSerializer.Serialize(new { instances = _gateway.ListInstances() }, BridgeProtocol.JsonOptions);

    [McpServerResource(Name = "vocaloid_instance_state", UriTemplate = "vocaloid://instances/{instance_id}/state", MimeType = "application/json")]
    public Task<string> State(McpServerInstance server, string instance_id, CancellationToken cancellationToken = default)
        => _gateway.ReadResourceAsync(server, instance_id, "v6_get_state", cancellationToken);

    [McpServerResource(Name = "vocaloid_project_summary", UriTemplate = "vocaloid://instances/{instance_id}/project/summary", MimeType = "application/json")]
    public Task<string> ProjectSummary(McpServerInstance server, string instance_id, CancellationToken cancellationToken = default)
        => _gateway.ReadResourceAsync(server, instance_id, "v6_query_project", cancellationToken);

    [McpServerResource(Name = "vocaloid_selection", UriTemplate = "vocaloid://instances/{instance_id}/selection", MimeType = "application/json")]
    public Task<string> Selection(McpServerInstance server, string instance_id, CancellationToken cancellationToken = default)
        => _gateway.ReadResourceAsync(server, instance_id, "v6_selection_resource", cancellationToken);

    [McpServerResource(Name = "vocaloid_catalog", UriTemplate = "vocaloid://instances/{instance_id}/catalog", MimeType = "application/json")]
    public Task<string> Catalog(McpServerInstance server, string instance_id, CancellationToken cancellationToken = default)
        => _gateway.ReadResourceAsync(server, instance_id, "v6_get_catalog", cancellationToken);
}
