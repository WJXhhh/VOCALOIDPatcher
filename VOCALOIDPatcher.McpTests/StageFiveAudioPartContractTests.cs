using System.Text.Json;
using VOCALOIDPatcher.Mcp.Domains.AudioParts;

namespace VOCALOIDPatcher.McpTests;

public sealed class StageFiveAudioPartContractTests
{
    [Fact]
    public void OfflineOperationsArePublishedWithRequiredFields()
    {
        var normalize = Assert.Single(AudioPartContracts.Operations, item => item.Id == "audio_parts.normalize");
        Assert.Equal(new[] { "op", "track_index", "part_index" }, normalize.RequiredFields);

        var stretch = Assert.Single(AudioPartContracts.Operations, item => item.Id == "audio_parts.time_stretch");
        Assert.Contains("duration_tick", stretch.RequiredFields);
    }

    [Theory]
    [InlineData("{\"op\":\"audio_normalize\",\"track_index\":0,\"part_index\":1}")]
    [InlineData("{\"op\":\"audio_time_stretch\",\"track_index\":0,\"part_index\":1,\"duration_tick\":1920}")]
    [InlineData("{\"op\":\"normalize\",\"track_index\":0,\"part_index\":1}")]
    [InlineData("{\"op\":\"time_stretch\",\"track_index\":0,\"part_index\":1,\"duration_tick\":1920}")]
    public void OfflineOperationShapeValidationAcceptsValidRequests(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Empty(AudioPartContracts.ValidateOfflineOperation(document.RootElement));
    }

    [Fact]
    public void UnifiedOperationNamesMatchPublishedContractIds()
    {
        Assert.Contains(AudioPartContracts.Operations, item => item.Id == "audio_parts.normalize");
        Assert.Contains(AudioPartContracts.Operations, item => item.Id == "audio_parts.time_stretch");
    }

    [Theory]
    [InlineData("{\"op\":\"audio_normalize\",\"track_index\":-1,\"part_index\":1}", "track_index")]
    [InlineData("{\"op\":\"audio_time_stretch\",\"track_index\":0,\"part_index\":1,\"duration_tick\":0}", "duration_tick")]
    public void OfflineOperationShapeValidationRejectsInvalidRequests(string json, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Contains(AudioPartContracts.ValidateOfflineOperation(document.RootElement), error => error.Contains(expected, StringComparison.Ordinal));
    }
}
