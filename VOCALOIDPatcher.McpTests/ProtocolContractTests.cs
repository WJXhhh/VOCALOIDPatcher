using System.Text.Json;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class ProtocolContractTests
{
    [Fact]
    public void UsesSnakeCaseAndDoesNotExposePointers()
    {
        var entity = new EntityRef("project", 7, "note", 1, 2, 3);
        string json = JsonSerializer.Serialize(entity, BridgeProtocol.JsonOptions);

        Assert.Contains("\"project_id\"", json);
        Assert.Contains("\"track_index\"", json);
        Assert.DoesNotContain("pointer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ErrorResponseHasStableCode()
    {
        BridgeResponse response = BridgeResponse.Failure("id", "stale_project", "changed", details: new { revision = 8 });

        Assert.False(response.Ok);
        Assert.Equal("stale_project", response.Error!.Code);
        Assert.Equal(8, response.Error.Details!.Value.GetProperty("revision").GetInt32());
    }
}
