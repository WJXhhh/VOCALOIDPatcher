using System.Collections.Immutable;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionBackendTests
{
    private static TransitionSegment Row(int begin, int split, int end, string from, string to, int stage = 0) =>
        new(begin, split, end, from, to, 1, 2, stage, false, "metadata", []);

    private static TransitionLayout Layout(bool protectedJoin = false)
    {
        var note = new TimingNoteAddress(0, 0, 0, 960, 960, 60, "bank", "a b c");
        TransitionSegment[] rows = [Row(90, 100, 125, "Sil", "a"), Row(125, 140, 160, "a", "b"), Row(160, 180, 200, "b", "c")];
        return new(Guid.NewGuid(), 3, "source", rows, rows,
            [new(note, [100, 140, 180, 240])], protectedJoin
                ? [new(0, 0), new(100, 100), new(125, 125), new(140, 140), new(180, 180), new(240, 240), new(300, 300)]
                : [new(0, 0), new(100, 100), new(140, 140), new(180, 180), new(240, 240), new(300, 300)]);
    }

    [Fact]
    public void OneSharedJunctionHasBothNativeEndpointAliases()
    {
        var layout = Layout();
        var end = new TransitionEndpointRef(layout.Edges[0].Identity, TransitionEndpoint.End);
        var begin = new TransitionEndpointRef(layout.Edges[1].Identity, TransitionEndpoint.Begin);
        Assert.Same(layout.Junction(end), layout.Junction(begin));
        var compiled = new TransitionControlPlan(layout.Fingerprint, [new(end, 130), new(begin, 130)]).Compile(layout);
        Assert.Equal(130, compiled.Anchors.Single(a => a.From == 125).To);
        Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint, [new(end, 130), new(begin, 131)]).Compile(layout));
    }

    [Fact]
    public void ProtectedPhonemeAndContextAnchorsCannotMove()
    {
        var layout = Layout(true);
        var endpoint = new TransitionEndpointRef(layout.Edges[0].Identity, TransitionEndpoint.End);
        Assert.True(layout.Junction(endpoint).Protected);
        Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint, [new(endpoint, 126)]).Compile(layout));
        var split = new TransitionEndpointRef(layout.Edges[1].Identity, TransitionEndpoint.Split);
        Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint, [new(split, 141)]).Compile(layout));
    }

    [Fact]
    public void GeometryHasIntegerFrameGuardsAndCrossingRequestsAreRejected()
    {
        var layout = Layout();
        var endpoint = new TransitionEndpointRef(layout.Edges[0].Identity, TransitionEndpoint.End);
        var joint = layout.Junction(endpoint);
        Assert.Equal(101, joint.MinimumCandidate); Assert.Equal(139, joint.MaximumCandidate);
        Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint, [new(endpoint, 140)]).Compile(layout));
        Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint, [new(endpoint, 100)]).Compile(layout));
    }

    [Fact]
    public void NeutralGainAndNoOpEndpointsPreserveOriginalWarpAndFingerprint()
    {
        var layout = Layout();
        var baseline = new TransitionControlPlan(layout.Fingerprint).Compile(layout);
        var endpoint = new TransitionEndpointRef(layout.Edges[0].Identity, TransitionEndpoint.End);
        var neutral = new TransitionControlPlan(layout.Fingerprint, [new(endpoint, 125)], [new(layout.Edges[1].Identity, -0f)]).Compile(layout);
        Assert.Equal(baseline.Fingerprint, neutral.Fingerprint);
        Assert.Equal(layout.Anchors, neutral.Anchors);
        Assert.Empty(neutral.Gains);
    }

    [Fact]
    public void GainCanonicalizationIsOrderIndependentAndStageSpecific()
    {
        var layout = Layout(); var a = layout.Edges[0].Identity; var b = layout.Edges[1].Identity;
        var x = new TransitionControlPlan(layout.Fingerprint, gains: [new(a, -12), new(b, 6)]).Compile(layout);
        var y = new TransitionControlPlan(layout.Fingerprint, gains: [new(b, 6), new(a, -12)]).Compile(layout);
        Assert.Equal(x.Fingerprint, y.Fingerprint);
        Assert.NotEqual(x.Fingerprint, new TransitionControlPlan(layout.Fingerprint, gains: [new(a, 6), new(b, -12)]).Compile(layout).Fingerprint);
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(layout.Fingerprint, gains: [new(a, 6), new(a, -12)]).Compile(layout));
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(layout.Fingerprint, gains: [new(a with { Stage = 1 }, 6)]).Compile(layout));
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(-61)] [InlineData(13)]
    public void InvalidGainCannotEnterAnExecutionPlan(float decibels)
    {
        var layout = Layout();
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(layout.Fingerprint, gains: [new(layout.Edges[0].Identity, decibels)]));
    }

    [Fact]
    public void StaleAndForeignIdentitiesDoNotRetargetByIndexOrPart()
    {
        var a = Layout(); var b = Layout(true);
        Assert.Throws<InvalidOperationException>(() => new TransitionControlPlan(a.Fingerprint).Compile(b));
        var foreign = a.Edges[1].Identity with { Right = a.Edges[1].Identity.Right! with { Index = 2 } };
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(a.Fingerprint, gains: [new(foreign, -3)]).Compile(a));
    }

    [Fact]
    public void ImmutablePlansCompileConcurrentlyWithoutSharingCandidateState()
    {
        var layout = Layout();
        var endpoint = new TransitionEndpointRef(layout.Edges[0].Identity, TransitionEndpoint.End);
        var plans = Enumerable.Range(0, 32).Select(i => new TransitionControlPlan(layout.Fingerprint,
            [new(endpoint, 110 + i % 20)], [new(layout.Edges[1].Identity, i % 2 == 0 ? -12 : 6)])).ToArray();
        string[] expected = plans.Select(p => p.Compile(layout).Fingerprint).ToArray();
        Parallel.For(0, 2048, i => Assert.Equal(expected[i % plans.Length], plans[i % plans.Length].Compile(layout).Fingerprint));
        Assert.Equal(125, layout.Junction(endpoint).CurrentFrame);
    }

    [Fact]
    public void RepeatedSamePhoneAndStageBindToDifferentNoteOccurrences()
    {
        var a = new TimingNoteAddress(0, 0, 0, 960, 480, 60, "bank", "a");
        var b = a with { Note = 1, Position = 1440 };
        TransitionSegment[] rows = [Row(90, 100, 125, "Sil", "a"), Row(125, 140, 160, "a", "a")];
        var layout = new TransitionLayout(Guid.NewGuid(), 1, "repeat", rows, rows,
            [new(a, [100, 140]), new(b, [140, 200])], [new(0, 0), new(100, 100), new(140, 140), new(200, 200), new(300, 300)]);
        Assert.Equal(a, layout.Edges[1].Identity.Left!.Note);
        Assert.Equal(b, layout.Edges[1].Identity.Right!.Note);
        Assert.Equal(2, layout.Junctions.Single(j => j.OriginalFrame == 125).Aliases.Length);
    }

    [Fact]
    public void MalformedNoteBoundaryTopologyIsRejectedBeforeIndexing()
    {
        var a = new TimingNoteAddress(0, 0, 0, 960, 480, 60, "bank", "a b");
        TransitionSegment[] rows = [Row(90, 100, 125, "Sil", "a")];
        Assert.Throws<NotSupportedException>(() => new TransitionLayout(Guid.NewGuid(), 1, "bad", rows, rows,
            [new(a, [100, 140])], [new(0, 0), new(300, 300)]));
    }
}
