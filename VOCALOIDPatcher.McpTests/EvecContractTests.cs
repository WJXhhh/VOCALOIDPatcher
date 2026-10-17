using System.Text.Json;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class EvecContractTests
{
    private static EvecEditRequest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return EvecEditRequest.Parse(document.RootElement);
    }

    [Fact]
    public void PartialUpdatePreservesOmittedFieldsAndZeroMeansClearOneOption()
    {
        var request = Parse("{\"release_id\":0}");
        Assert.Equal(0, request.ReleaseId);
        Assert.Null(request.VoiceColorId);
        Assert.Null(request.AttackId);
        Assert.Null(request.ConsonantExtension);
        Assert.False(request.Clear);
        Assert.True(Parse("{\"clear\":true}").Clear);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"clear\":false}")]
    [InlineData("{\"clear\":1}")]
    [InlineData("{\"clear\":true,\"attack_id\":0}")]
    [InlineData("{\"attack_id\":\"302\"}")]
    [InlineData("{\"release_id\":null}")]
    [InlineData("{\"consonant_extension\":4}")]
    [InlineData("{\"consonant_extension\":-1}")]
    [InlineData("{\"consonant_extension\":1.5}")]
    [InlineData("{\"voice_color_id\":101,\"voice_color_id\":105}")]
    [InlineData("{\"color\":101}")]
    public void MalformedRequestsAreRejected(string json)
        => Assert.Throws<ArgumentException>(() => Parse(json));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExtensionUsesAdditionalCopies(int copies)
        => Assert.Equal(copies, Parse($"{{\"consonant_extension\":{copies}}}").ConsonantExtension);

    [Fact]
    public void DedicatedOperationIsDiscoverableAndRequiresLogicalPayload()
    {
        var contract = Assert.Single(McpContractCatalog.Operations, item => item.Id == "notes.set_evec");
        Assert.Equal("v6_edit_notes", contract.LegacyTool);
        Assert.Contains("notes.set_evec", McpContractCatalog.Domains.Single(domain => domain.Id == "notes").OperationIds);
        using var missing = JsonDocument.Parse("{\"op\":\"set_evec\",\"track_index\":0,\"part_index\":0,\"note_index\":0}");
        Assert.Contains("evec", OperationContractValidator.Validate(contract.Id, missing.RootElement));
    }
}
