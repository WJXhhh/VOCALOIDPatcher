using System.Collections.Immutable;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionExpressionTests
{
    private static TransitionSegment Row(int split = 110, int end = 122) => new(90, split, end, "l", "a", 1, 2, 0, false, "source",
        Enumerable.Range(90, end - 90).Select(f => new TransitionFrame(f, BitConverter.SingleToInt32Bits(f - 90), 0, 0)).ToImmutableArray())
        { MaterialCoverage = new(new(0, 25), new(26, 61)) };
    private static TransitionLayout Layout()
    {
        var note = new TimingNoteAddress(0, 0, 0, 960, 960, 60, "bank", "l a");
        return new(Guid.NewGuid(), 1, "input", [Row()], [Row()], [new(note, [85, 110, 180])],
            [new(0, 0), new(85, 85), new(110, 110), new(180, 180), new(300, 300)]);
    }
    [Fact]
    public void SelectingLegacyDataDoesNotActivateCoverageAndIncomingDiamondUsesVowelSide()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var legacy = new TransitionControlPlan(layout.Fingerprint).WithPhase(edge, .5f);
        string before = legacy.Compile(layout).Fingerprint;
        var range = TransitionExpressionControl.Range(Row(), edge, legacy)!.Value;
        Assert.Equal(TransitionSourceSides.Right, range.Side); Assert.Equal(110, range.Begin);
        Assert.Empty(legacy.Coverages); Assert.Equal(before, legacy.Compile(layout).Fingerprint);
        Assert.Equal(TransitionSourceSides.Left, TransitionExpressionControl.Range(Row() with { To = "Sil" }, edge, legacy)!.Value.Side);
    }
    [Theory]
    [InlineData(-1f)] [InlineData(-.75f)] [InlineData(0f)] [InlineData(.75f)] [InlineData(1f)]
    public void ProgressEnablesCompleteSourceAtZeroAndPreservesTimingVolumeAndOtherEdges(float amount)
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var original = new TransitionControlPlan(layout.Fingerprint, gains: [new(edge, -6)])
            .WithJunction(layout, new(edge, TransitionEndpoint.End), 124);
        var plan = TransitionExpressionControl.Progress(original, Row(), edge, amount);
        Assert.Equal(TransitionSourceSides.Right, Assert.Single(plan.Coverages).Sides);
        Assert.True(original.Compile(layout).Anchors.SequenceEqual(plan.Compile(layout).Anchors));
        Assert.True(original.Gains.SequenceEqual(plan.Gains)); Assert.True(original.Junctions.SequenceEqual(plan.Junctions));
        var range = TransitionExpressionControl.Range(Row(), edge, plan)!.Value;
        double marker = TransitionExpressionControl.HandleFrame(range, amount);
        Assert.InRange(marker, range.Begin, range.End);
        Assert.InRange(Math.Abs(TransitionExpressionControl.AmountAt(range, marker) - amount), 0, 1e-5);
        var restored = TransitionExpressionControl.Reset(plan, edge);
        Assert.Empty(restored.Phases); Assert.Empty(restored.Coverages);
        Assert.Equal(original.Compile(layout).Fingerprint, restored.Compile(layout).Fingerprint);
    }
    [Fact]
    public void TwoFramesPermitCompleteCoverageButDoNotMoveTheDiamondToTheOtherSide()
    {
        var row = Row(110, 112); var layout = Layout(); var edge = layout.Edges[0].Identity;
        var controls = new TransitionControlPlan(layout.Fingerprint);
        Assert.Null(TransitionExpressionControl.Range(row, edge, controls));
        Assert.Equal(TransitionSourceSides.Right, TransitionExpressionControl.Range(row, edge, controls, false)!.Value.Side);
        Assert.Single(TransitionExpressionControl.Enable(controls, row, edge).Coverages);
        Assert.Null(TransitionExpressionControl.Range(Row(110, 111), edge, controls, false));
        Assert.Throws<NotSupportedException>(() => TransitionExpressionControl.Enable(controls, Row(110, 111), edge));
    }
    [Fact]
    public void ExplicitExistingSidesAndUnrelatedControlsSurviveProgressAndReset()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var another = edge with { Occurrence = 1 };
        var controls = new TransitionControlPlan(layout.Fingerprint, gains: [new(edge, -6)])
            .WithCoverage(edge, TransitionSourceSides.Both).WithCoverage(another, TransitionSourceSides.Left).WithPhase(another, .5f);
        var edited = TransitionExpressionControl.Progress(controls, Row(), edge, -.75f);
        Assert.Equal(TransitionSourceSides.Both, edited.Coverages.Single(c => c.Edge == edge).Sides);
        var reset = TransitionExpressionControl.Reset(edited, edge);
        Assert.Equal(another, Assert.Single(reset.Coverages).Edge); Assert.Equal(another, Assert.Single(reset.Phases).Edge);
        Assert.True(controls.Gains.SequenceEqual(reset.Gains));
        var leftOnly = controls.WithCoverage(edge, TransitionSourceSides.Left);
        Assert.Equal(TransitionSourceSides.Left, TransitionExpressionControl.Range(Row(), edge, leftOnly)!.Value.Side);
    }
    [Fact]
    public void FlatCroppedMapsCanUseFullSourceButMissingOrUnrepresentableMetadataIsRejected()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var controls = new TransitionControlPlan(layout.Fingerprint);
        var flat = Row() with { Frames = Row().Frames.Select(f => f with { SourceBits = 0 }).ToImmutableArray() };
        Assert.False(TransitionPhaseMapping.CanEdit(flat)); Assert.NotNull(TransitionExpressionControl.Range(flat, edge, controls));
        Assert.Null(TransitionExpressionControl.Range(flat with { MaterialCoverage = null }, edge, controls));
        Assert.Null(TransitionExpressionControl.Range(Row(91), edge, controls.WithCoverage(edge, TransitionSourceSides.Both)));
        Assert.Throws<InvalidDataException>(() => TransitionExpressionControl.Progress(controls, Row(), edge, float.NaN));
    }
}
