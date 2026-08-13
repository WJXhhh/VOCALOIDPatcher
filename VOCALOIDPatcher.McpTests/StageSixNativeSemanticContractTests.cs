using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class StageSixNativeSemanticContractTests
{
    [Fact]
    public void JobIdsAreUniqueAndUnavailableJobsExplainWhy()
    {
        Assert.Equal(NativeSemanticJobCatalog.Jobs.Count, NativeSemanticJobCatalog.Jobs.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(NativeSemanticJobCatalog.Jobs.Where(item => !item.Implemented), item => Assert.False(string.IsNullOrWhiteSpace(item.UnavailableReason)));
    }

    [Theory]
    [InlineData("transpose_note", "semitones")]
    [InlineData("insert_rest", "absolute_tick")]
    [InlineData("insert_rest", "length_tick")]
    [InlineData("split_note", "phoneme_strategy")]
    [InlineData("quantize_position", "strength")]
    [InlineData("parameter_range_delete", "start_tick")]
    [InlineData("parameter_range_delete", "end_tick")]
    [InlineData("insert_lyrics_batch", "lyrics")]
    public void MutatingJobsPublishRequiredOptions(string id, string field)
    {
        NativeSemanticJobContract contract = Assert.Single(NativeSemanticJobCatalog.Jobs, item => item.Id == id);
        Assert.Contains(field, contract.RequiredOptions);
        Assert.True(contract.Implemented);
    }

    [Theory]
    [InlineData("half_tempo")]
    [InlineData("double_tempo")]
    [InlineData("parameter_selection_reset")]
    public void NewlyVerifiedNativeJobsArePublished(string id)
    {
        NativeSemanticJobContract contract = Assert.Single(NativeSemanticJobCatalog.Jobs, item => item.Id == id);
        Assert.True(contract.Implemented);
    }

    [Theory]
    [InlineData("quantize_duration", "duration")]
    [InlineData("parameter_range_transform", "translate")]
    [InlineData("phonetic_conversion", "independent")]
    public void MissingNativeBusinessEntriesRemainUnavailable(string id, string evidence)
    {
        NativeSemanticJobContract contract = Assert.Single(NativeSemanticJobCatalog.Jobs, item => item.Id == id);
        Assert.False(contract.Implemented);
        Assert.Contains(evidence, contract.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DialogDependentNormalizeRemainsUnavailable()
    {
        NativeSemanticJobContract contract = Assert.Single(NativeSemanticJobCatalog.Jobs, item => item.Id == "normalize_note");
        Assert.False(contract.Implemented);
        Assert.Contains("dialog", contract.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }
}
