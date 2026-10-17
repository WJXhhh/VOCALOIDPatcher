using System.Text.Json.Nodes;
using VOCALOIDPatcher.McpServer;

namespace VOCALOIDPatcher.McpTests;

public sealed class ToolSchemaCompatibilityTests
{
    [Fact]
    public void NormalizesSchemasWithoutChangingBooleanDataOrFalseSchemas()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"request":true,"disabled":false,
              "flag":{"type":"boolean","default":true,"examples":[true,false]},
              "nested":{"anyOf":[true,{"type":"array","items":true}]}},
             "additionalProperties":false,"examples":[{"request":true}],"x-custom":true}
            """)!;
        var normalized = ToolSchemaCompatibility.Normalize(schema);
        Assert.IsType<JsonObject>(normalized["properties"]!["request"]);
        Assert.False(normalized["properties"]!["disabled"]!.GetValue<bool>());
        Assert.True(normalized["properties"]!["flag"]!["default"]!.GetValue<bool>());
        Assert.True(normalized["examples"]![0]!["request"]!.GetValue<bool>());
        Assert.True(normalized["x-custom"]!.GetValue<bool>());
        Assert.IsType<JsonObject>(normalized["properties"]!["nested"]!["anyOf"]![0]);
        Assert.IsType<JsonObject>(normalized["properties"]!["nested"]!["anyOf"]![1]!["items"]);
        Assert.False(normalized["additionalProperties"]!.GetValue<bool>());
    }
}
