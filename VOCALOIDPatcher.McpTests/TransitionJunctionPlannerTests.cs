using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionJunctionPlannerTests
{
    private static TransitionExecutionPlan Plan(bool gains)
    {
        var note = new TimingNoteAddress(0, 0, 0, 960, 960, 60, "bank", "a b");
        TransitionSegment[] rows =
        [
            new(3100, 3110, 3120, "Sil", "a", 1, 2, 0, false, "metadata", []),
            new(3120, 3130, 3140, "a", "b", 1, 2, 0, false, "metadata", [])
        ];
        var layout = new TransitionLayout(Guid.NewGuid(), 1, "source", rows, rows,
            [new(note, [3110, 3130, 3160])], [new(0, 0), new(3110, 3110), new(3130, 3130), new(3160, 3160), new(3200, 3200)]);
        var controls = new TransitionControlPlan(layout.Fingerprint,
            gains: gains ? [new(layout.Edges[0].Identity, -6), new(layout.Edges[1].Identity, 6)] : []);
        return new(layout, controls.Compile(layout), [.. rows], 0, false, 0);
    }

    [Fact]
    public void TimingOnlyAndSplitSeamsAreEligibleBelowTheVolumeThreshold()
    {
        var junctions = TransitionJunctionPlanner.Collect(Plan(false), new() { VolumeThresholdDb = 30 });
        Assert.Equal(new long[] { 3100, 3110, 3120, 3130, 3140 }.Select(f => (f - 3087) * 256), junctions.Select(j => j.JunctionSample));
        Assert.All(junctions, j => { Assert.Equal(0d, j.LeftNominalDb); Assert.Equal(0d, j.RightNominalDb); });
    }

    [Fact]
    public void SharedSeamHasBothAdjacentGainsAndIsOnlyProcessedOnce()
    {
        var junctions = TransitionJunctionPlanner.Collect(Plan(true), new());
        var shared = Assert.Single(junctions, j => j.JunctionSample == (3120 - 3087) * 256);
        Assert.Equal(-6d, shared.LeftNominalDb);
        Assert.Equal(6d, shared.RightNominalDb);
        Assert.Empty(TransitionJunctionPlanner.Collect(Plan(true), new() { Mode = TransitionSmoothingMode.Disabled }));
    }
}
