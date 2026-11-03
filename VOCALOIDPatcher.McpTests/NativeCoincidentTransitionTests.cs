using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class NativeCoincidentTransitionTests
{
    private static TransitionLayout Layout()
    {
        TransitionSegment Row(int begin, int split, int end, string from, string to) =>
            new(begin, split, end, from, to, 1, 2, 0, false, "material", []);
        TransitionSegment[] original = [Row(98, 100, 101, "Sil", "m"), Row(101, 111, 120, "m", "u"), Row(145, 150, 155, "u", "Sil")];
        TransitionSegment[] current = [Row(107, 109, 109, "Sil", "m"), Row(109, 111, 120, "m", "u"), Row(145, 150, 155, "u", "Sil")];
        return new(Guid.NewGuid(), 1, "native-crop", original, current,
            [new(new(0, 0, 0, 960, 480, 60, "bank", "m u"), [100, 111, 150])],
            [new(0, 0), new(100, 109), new(111, 111), new(150, 150), new(200, 200)]);
    }

    [Fact]
    public void NativeZeroWidthHalfPreservesNeutralWarpAndReachableBaseline()
    {
        var layout = Layout(); var neutral = new TransitionControlPlan(layout.Fingerprint);
        Assert.Equal(layout.Anchors, neutral.Compile(layout).Anchors);
        var joint = layout.Junction(new(layout.Edges[0].Identity, TransitionEndpoint.End));
        Assert.Equal(109, joint.CurrentFrame);
        Assert.InRange(joint.CurrentFrame, joint.MinimumCandidate, joint.MaximumCandidate);
        var conditional = layout.ConditionalJunction(new(layout.Edges[0].Identity, TransitionEndpoint.End), neutral);
        Assert.InRange(conditional.CurrentFrame, conditional.MinimumCandidate, conditional.MaximumCandidate);
    }

    [Fact]
    public void UnrelatedGeometryEditKeepsCoincidenceImplicitAndWarpStrict()
    {
        var layout = Layout();
        var compiled = new TransitionControlPlan(layout.Fingerprint)
            .WithJunction(layout, new(layout.Edges[1].Identity, TransitionEndpoint.End), 121).Compile(layout);
        TransitionLayout.ValidateAnchors(compiled.Anchors);
        Assert.DoesNotContain(compiled.Anchors, a => a.From == 101);
        Assert.Contains(new TransitionAnchor(100, 109), compiled.Anchors);
        Assert.Contains(new TransitionAnchor(120, 121), compiled.Anchors);
    }

    [Fact]
    public void ExistingCollapsedHalfCanExpandButANewCollapseIsRejected()
    {
        var layout = Layout(); var plan = new TransitionControlPlan(layout.Fingerprint);
        var expanded = plan.WithJunction(layout, new(layout.Edges[0].Identity, TransitionEndpoint.End), 110).Compile(layout);
        Assert.Contains(new TransitionAnchor(101, 110), expanded.Anchors);
        Assert.Throws<NotSupportedException>(() => plan.WithJunction(layout,
            new(layout.Edges[1].Identity, TransitionEndpoint.End), 111).Compile(layout));
        Assert.Throws<NotSupportedException>(() => TransitionLayout.ValidateAnchors([new(0, 0), new(10, 20), new(11, 20), new(30, 30)]));
    }
}
