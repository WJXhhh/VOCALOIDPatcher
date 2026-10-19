using System.Collections.Immutable;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionCoverageTests
{
    private static TransitionSegment Row(int end = 162) => new(100, 126, end, "s\\", "iE_r", 1, 2, 0, false, "material",
        Enumerable.Range(100, end - 100).Select(f => new TransitionFrame(f, BitConverter.SingleToInt32Bits(f - 100), 1, 2)).ToImmutableArray())
        { MaterialCoverage = new(new(0, 25), new(26, 61)) };
    private static TransitionLayout Layout(int part = 0)
    {
        var note = new TimingNoteAddress(0, part, 0, 960, 960, 60, "bank", "s\\ iE_r");
        return new(Guid.NewGuid(), 1, "input", [Row()], [Row(138)], [new(note, [90, 126, 240])],
            [new(0, 0), new(90, 90), new(126, 126), new(240, 240), new(300, 300)]);
    }
    [Theory]
    [InlineData(-1f)] [InlineData(0f)] [InlineData(1f)]
    public void CoverageConsumesReferenceEndpointsBeforePhaseWithoutMovingAllocationOrFlags(float phase)
    {
        var reference = Row(); var current = Row(138);
        var covered = TransitionCoverageMapping.Apply(reference, current, TransitionSourceSides.Right);
        var result = TransitionPhaseMapping.Apply(current with { Frames = covered }, phase);
        Assert.Equal(26f, BitConverter.Int32BitsToSingle(result[26].SourceBits));
        Assert.Equal(61f, BitConverter.Int32BitsToSingle(result[^1].SourceBits));
        Assert.Equal(current.Frames.Take(26), covered.Take(26));
        Assert.Equal(current.Frames.Select(f => (f.Frame, f.Flag0, f.Flag1)), result.Select(f => (f.Frame, f.Flag0, f.Flag1)));
        Assert.True(result.Skip(26).Zip(result.Skip(27)).All(p => BitConverter.Int32BitsToSingle(p.First.SourceBits) <= BitConverter.Int32BitsToSingle(p.Second.SourceBits)));
        Assert.Equal(37f, BitConverter.Int32BitsToSingle(current.Frames[^1].SourceBits));
    }
    [Fact]
    public void DegenerateOrDifferentMaterialCannotSilentlyDiscardTheRequestedCoverage()
    {
        Assert.Throws<NotSupportedException>(() => TransitionCoverageMapping.Apply(Row(), Row(127), TransitionSourceSides.Right));
        Assert.Throws<NotSupportedException>(() => TransitionCoverageMapping.Apply(Row(), Row(126), TransitionSourceSides.Right));
        Assert.Throws<NotSupportedException>(() => TransitionCoverageMapping.Apply(Row(), Row(138) with { MaterialMetadata = "other" }, TransitionSourceSides.Right));
        Assert.Throws<NotSupportedException>(() => TransitionCoverageMapping.Apply(Row(), Row(138) with { Frames = [] }, TransitionSourceSides.Right));
    }
    [Fact]
    public void AlreadyCroppedReferenceAndReversePlaybackUseCertifiedStageExtents()
    {
        var shortReference = Row(138);
        var frames = TransitionCoverageMapping.Apply(shortReference, shortReference, TransitionSourceSides.Right);
        Assert.Equal(61f, BitConverter.Int32BitsToSingle(frames[^1].SourceBits));
        var reverse = Row(138) with { Reverse = true, MaterialCoverage = new(new(61, 26), new(25, 0)) };
        var mapped = TransitionCoverageMapping.Apply(reverse, reverse, TransitionSourceSides.Both);
        Assert.Equal(61f, BitConverter.Int32BitsToSingle(mapped[0].SourceBits));
        Assert.Equal(26f, BitConverter.Int32BitsToSingle(mapped[25].SourceBits));
        Assert.Equal(25f, BitConverter.Int32BitsToSingle(mapped[26].SourceBits));
        Assert.Equal(0f, BitConverter.Int32BitsToSingle(mapped[^1].SourceBits));
        Assert.Throws<NotSupportedException>(() => TransitionCoverageMapping.Apply(Row() with { MaterialCoverage = null }, Row(138), TransitionSourceSides.Right));
    }
    [Fact]
    public void CoverageIsExplicitAtZeroPhaseSurvivesEditsAndHasItsOwnFingerprint()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var plain = new TransitionControlPlan(layout.Fingerprint);
        var covered = plain.WithCoverage(edge, TransitionSourceSides.Right);
        Assert.NotEqual(plain.Compile(layout).Fingerprint, covered.Compile(layout).Fingerprint);
        Assert.Equal(plain.Compile(layout).Anchors, covered.Compile(layout).Anchors);
        var edited = covered.WithPhase(edge, .5f).WithOnset(edge with { From = "Sil" }, 0).WithJunction(layout, new(edge, TransitionEndpoint.End), 140);
        Assert.Single(edited.Coverages);
        Assert.Single(TransitionControlPlan.FromCompiled(layout, edited.Compile(layout)).Coverages);
        Assert.Empty(covered.WithCoverage(edge, TransitionSourceSides.None).Coverages);
        Assert.Throws<InvalidDataException>(() => covered.WithCoverage(edge, (TransitionSourceSides)4));
        Assert.Throws<InvalidDataException>(() => plain.WithCoverage(edge with { Occurrence = 9 }, TransitionSourceSides.Right).Compile(layout));
    }
    [Fact]
    public void SaveSharedUndoOtherPartsAndUnknownFieldsRetainCoverageIndependentlyOfNativeBackend()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var controls = new TransitionControlPlan(layout.Fingerprint).WithCoverage(edge, TransitionSourceSides.Right).WithPhase(edge, -.5f);
        var before = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, controls);
        var part2 = Layout(1);
        var both = TransitionProjectBinding.Encode(before, part2, new TransitionControlPlan(part2.Fingerprint).WithCoverage(part2.Edges[0].Identity, TransitionSourceSides.Left));
        Assert.Equal(2, both.Coverages.Length);
        var loaded = TransitionArchiveData.Parse(TransitionArchiveData.From(both).Payload.Span);
        Assert.True(loaded.Executable); Assert.True(TransitionProjectBinding.HasPart(loaded.Data!, 0, 1));
        Assert.Equal(TransitionSourceSides.Right, Assert.Single(TransitionProjectBinding.Resolve(loaded.Data!, layout).Coverages).Sides);
        var doc = new TransitionDocument(); doc.Load(loaded);
        var edits = new List<VOCALOIDPatcher.BreathVolume.ICustomParameterHistoryEdit>();
        doc.Edit(TransitionProjectBinding.Encode(both, layout, controls.WithCoverage(edge, TransitionSourceSides.None)), edits.Add);
        Assert.Single(doc.Snapshot.Data!.Coverages);
        edits.Single().ApplyBefore(); edits.Single().AfterApply(); Assert.Equal(loaded.Payload.ToArray(), doc.Snapshot.Payload.ToArray());
        edits.Single().ApplyAfter(); edits.Single().AfterApply(); Assert.Single(doc.Snapshot.Data!.Coverages);
        Assert.Empty(before.KeepNotes(_ => false).Coverages);
        var unknown = before with { Coverages = [before.Coverages[0] with { Extra = new() { ["future"] = JsonSerializer.SerializeToElement(1) } }] };
        var opaque = TransitionArchiveData.From(unknown);
        Assert.False(opaque.Executable); Assert.Equal(opaque.Payload.ToArray(), TransitionArchiveData.Parse(opaque.Payload.Span).Payload.ToArray());
        var old = JsonSerializer.SerializeToNode(before)!; old.AsObject().Remove("Coverages");
        Assert.Empty(TransitionArchiveData.Parse(System.Text.Encoding.UTF8.GetBytes(old.ToJsonString())).Data!.Coverages);
    }
}
