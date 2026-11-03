using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.VoiceComp;

namespace VOCALOIDPatcher.McpTests;

public sealed class VoiceCompMcpTests
{
    [Theory]
    [InlineData("long_slice")]
    [InlineData("phoneme_splice")]
    [InlineData("independent_sustain")]
    public void QuickActionsUseExistingModelsAndRoundtrip(string action)
    {
        var edit = new VoiceCompEdit { Op = action, NoteIndices = [1, 3] };
        edit.Validate();
        var restored = TimingContract.Parse<VoiceCompEdit>(JsonSerializer.SerializeToElement(edit, BridgeProtocol.JsonOptions));
        Assert.Equal(action, restored.Op); Assert.Equal(edit.NoteIndices, restored.NoteIndices);
        var plan = TimingMcpEdits.EditComp(edit, null)!;
        Assert.Equal(action == "long_slice", plan.Body != null);
        Assert.Equal(action == "phoneme_splice", plan.Consonant != null && plan.Vowel != null);
        Assert.Equal(action == "independent_sustain", plan.Isolation?.After == true);
        Assert.True(plan.Active);
    }
    [Fact]
    public void SustainSourceLengthAndAudibleEndpointRemainIndependent()
    {
        var plan = TimingMcpEdits.EditComp(new() { Op = "independent_sustain", NoteIndices = [1], LengthMs = 1800, EndOffsetMs = -25, FadeMs = 8 }, null)!;
        Assert.Equal(1800, plan.Isolation!.SourceLengthMs); Assert.Equal(-25, plan.Isolation.EndOffsetMs); Assert.Equal(8, plan.Isolation.FadeMs);
        Assert.Null(plan.Body); Assert.Null(plan.Consonant);
    }
    [Fact]
    public void LongSliceEditsVowelOfExistingSpliceWithoutDestroyingConsonantOrIsolation()
    {
        var before = new VoiceCompPlan(Consonant: new(Phonemes: "t"), Vowel: new(Phonemes: "a", OffsetMs: 10), Isolation: new(true, 20), Bypass: true);
        var after = TimingMcpEdits.EditComp(new() { Op = "long_slice", NoteIndices = [1], Trim = "keep_tail", LengthMs = 2400 }, before)!;
        Assert.Equal(before.Consonant, after.Consonant); Assert.Equal(before.Isolation, after.Isolation); Assert.True(after.Bypass);
        Assert.Null(after.Body); Assert.Equal(CompTrim.KeepTail, after.Vowel!.Trim); Assert.Equal(2400, after.Vowel.LengthMs); Assert.Equal(10, after.Vowel.OffsetMs);
    }
    [Fact]
    public void SnapshotAndSourceReplacementPreserveOtherRolesAndDonorIsNotStored()
    {
        var plan = new VoiceCompPlan(Consonant: new(LengthMs: 1400), Vowel: new(Phonemes: "a", LengthMs: 2000), SeamMs: 7);
        var copied = TimingMcpEdits.EditComp(new() { Op = "copy_source", NoteIndices = [1], SourceRole = "consonant", Donor = new(0, 0, 0) },
            plan, basis => basis with { Lyric = "donor", Phonemes = "k a", Expression = new(64, 50, 50, 0, 0, 60, false, false) })!;
        Assert.Equal(plan.Vowel, copied.Vowel); Assert.Equal(1400, copied.Consonant!.LengthMs); Assert.Equal(7, copied.SeamMs);
        Assert.Equal("k a", copied.Consonant.Phonemes);
        var replaced = TimingMcpEdits.EditComp(new() { Op = "replace_source", NoteIndices = [1], SourceRole = "vowel", Source = new(Phonemes: "a_n", LengthMs: 1700) }, copied)!;
        Assert.Equal(copied.Consonant, replaced.Consonant); Assert.Equal("a_n", replaced.Vowel!.Phonemes);
        string saved = JsonSerializer.Serialize(TimingMcpEdits.Comp(replaced), BridgeProtocol.JsonOptions);
        Assert.Contains("\"lyric\":\"donor\"", saved);
        Assert.DoesNotContain("\"donor\":", saved); Assert.DoesNotContain("track_index", saved);
        Assert.Throws<ArgumentException>(() => TimingMcpEdits.EditComp(new() { Op = "copy_source", NoteIndices = [1], SourceRole = "body", Donor = new(0, 0, 0) }, plan, b => b));
    }
    [Theory]
    [InlineData("{\"op\":\"long_slice\",\"note_indices\":[]}")]
    [InlineData("{\"op\":\"long_slice\",\"note_indices\":[1,1]}")]
    [InlineData("{\"op\":\"long_slice\",\"note_indices\":[-1]}")]
    [InlineData("{\"op\":\"independent_sustain\",\"note_indices\":[1],\"length_ms\":-1}")]
    [InlineData("{\"op\":\"long_slice\",\"note_indices\":[1],\"trim\":\"typo\"}")]
    [InlineData("{\"op\":\"phoneme_splice\",\"note_indices\":[1],\"seam_ms\":31}")]
    [InlineData("{\"op\":\"bypass\",\"note_indices\":[1]}")]
    [InlineData("{\"op\":\"reset\",\"note_indices\":[1],\"length_ms\":1000}")]
    [InlineData("{\"op\":\"replace\",\"note_indices\":[1]}")]
    [InlineData("{\"op\":\"copy_source\",\"note_indices\":[1],\"source_role\":\"vowel\"}")]
    [InlineData("{\"op\":\"copy_source\",\"note_indices\":[1],\"source_role\":\"vowel\",\"donor\":{\"track_index\":-1,\"part_index\":0,\"note_index\":0}}")]
    [InlineData("{\"op\":\"reset\",\"note_indices\":[1],\"unknown\":true}")]
    [InlineData("{\"op\":\"reset\",\"note_indices\":[1],\"op\":\"bypass\"}")]
    public void MalformedBatchRequestsFailBeforeEditing(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => TimingContract.Parse<VoiceCompEdit>(doc.RootElement).Validate());
    }
    [Fact]
    public void BypassPreservesEveryRecipeAndResetRemovesIt()
    {
        var before = new VoiceCompPlan(new(LengthMs: 1800), Isolation: new(true), SeamMs: 9);
        var bypassed = TimingMcpEdits.EditComp(new() { Op = "bypass", NoteIndices = [1], Bypass = true }, before)!;
        Assert.Equal(before, bypassed with { Bypass = false });
        Assert.Null(TimingMcpEdits.EditComp(new() { Op = "reset", NoteIndices = [1] }, bypassed));
    }
}
