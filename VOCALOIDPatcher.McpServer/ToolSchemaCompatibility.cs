using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace VOCALOIDPatcher.McpServer;

public static class ToolSchemaCompatibility
{
    public static IMcpServerBuilder WithCompatibleToolSchemas(this IMcpServerBuilder builder)
        => builder.WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            // Copy the response: protocol tools may be shared across concurrent sessions.
            var copy = JsonSerializer.SerializeToNode(result)!.AsObject();
            foreach (JsonObject tool in copy["tools"]!.AsArray().OfType<JsonObject>())
            {
                tool["inputSchema"] = Normalize(tool["inputSchema"]!.DeepClone());
                if (tool["outputSchema"] is { } output)
                    tool["outputSchema"] = Normalize(output.DeepClone());
            }
            return copy.Deserialize<ListToolsResult>()!;
        }));

    public static JsonNode Normalize(JsonNode schema)
    {
        if (schema is JsonValue value && value.TryGetValue<bool>(out bool allowed))
            return allowed ? new JsonObject() : schema;
        if (schema is not JsonObject obj) return schema;
        // Visit only schema-valued keywords. Do not rewrite defaults, examples,
        // annotations, enum values, required lists, or arbitrary extension data.
        foreach (string key in new[] { "properties", "patternProperties", "$defs", "definitions", "dependentSchemas" })
            if (obj[key] is JsonObject map)
                foreach (string name in map.Select(pair => pair.Key).ToArray())
                    if (map[name] is { } child) map[name] = Normalize(child.DeepClone());
        foreach (string key in new[] { "items", "additionalItems", "additionalProperties", "unevaluatedItems",
                     "unevaluatedProperties", "contains", "propertyNames", "not", "if", "then", "else", "contentSchema" })
            if (obj[key] is { } child && child is not JsonArray)
                obj[key] = Normalize(child.DeepClone());
        foreach (string key in new[] { "allOf", "anyOf", "oneOf", "prefixItems", "items" })
            if (obj[key] is JsonArray children)
                for (int index = 0; index < children.Count; index++)
                    if (children[index] is { } child) children[index] = Normalize(child.DeepClone());
        return obj;
    }
}
