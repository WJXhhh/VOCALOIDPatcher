using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;

namespace VOCALOIDPatcher.McpTests;

public sealed class SharedPhonemeBoundaryTests
{
    private static readonly TimingNoteAddress Left = new(0, 0, 0, 960, 540, 58, "bank", "k uo");
    private static readonly TimingNoteAddress Right = new(0, 0, 1, 1500, 360, 56, "bank", "t 7");
    private static SampledTimingPlan Plan(TimingNoteAddress address, int index, float value) => new(address.Topology,
        [new(TimingAdjustment.NoteCoefficient, -1, .4f), new(TimingAdjustment.PhonemeBoundary, index, value)]);
    private static PhonemeBoundaryEdit.Note[] Context(SampledTimingPlan? left, SampledTimingPlan? right) =>
        [new(Left, [80, 90, 100], left), new(Right, [100, 110, 130], right)];

    [Theory]
    [InlineData(0, 2, -.02f)]
    [InlineData(1, 0, .03f)]
    public void EitherSideUpdatesTheSameNativeBoundaryAndPreservesOtherAxes(int target, int index, float seconds)
    {
        var context = Context(Plan(Left, 2, -.04f), Plan(Right, 0, -.04f));
        var candidate = PhonemeBoundaryEdit.WithBoundary(context[target].Plan!, index, seconds);
        var plans = PhonemeBoundaryEdit.Resolve(context, target, index, candidate);
        Assert.Equal(2, plans.Count);
        Assert.Equal(seconds, plans[Left]!.Adjustments.Single(a => a.Kind == TimingAdjustment.PhonemeBoundary && a.Index == 2).Value);
        Assert.Equal(seconds, plans[Right]!.Adjustments.Single(a => a.Kind == TimingAdjustment.PhonemeBoundary && a.Index == 0).Value);
        Assert.All(plans, p => Assert.Equal(.4f, p.Value!.PhonemeCoefficient(p.Key.Topology)));
    }

    [Fact]
    public void ResetClearsTheNeighborEvenWhenTheClickedSideHasNoStoredOffset()
    {
        var context = Context(null, Plan(Right, 0, -.04f));
        var plans = PhonemeBoundaryEdit.Resolve(context, 0, 2, new(Left.Topology, []));
        Assert.Null(plans[Left]);
        Assert.False(plans[Right]!.HasPhonemeControls);
        Assert.Equal(.4f, plans[Right]!.PhonemeCoefficient(Right.Topology));
    }

    [Fact]
    public void NeighboringNotesWithDifferentNativeFramesKeepIndependentControls()
    {
        var context = Context(Plan(Left, 2, -.04f), Plan(Right, 0, -.04f));
        context[1] = context[1] with { Frames = [101, 110, 130] };
        var plans = PhonemeBoundaryEdit.Resolve(context, 0, 2, Plan(Left, 2, .02f));
        Assert.Single(plans);
        Assert.False(plans.ContainsKey(Right));
    }

    [Fact]
    public void SharedEditHasOneHistoryEntryAndOneNotificationAndRestoresSavedState()
    {
        var document = new SampledTimingDocument();
        document.Load(new Dictionary<TimingNoteAddress, SampledTimingPlan> { [Left] = Plan(Left, 2, -.04f), [Right] = Plan(Right, 0, -.04f) });
        string original = document.Fingerprint;
        int notifications = 0, pushes = 0;
        document.Changed += () => notifications++;
        ICustomParameterHistoryEdit? history = null;
        document.EditMany(new Dictionary<TimingNoteAddress, SampledTimingPlan?> { [Left] = Plan(Left, 2, .02f), [Right] = Plan(Right, 0, .02f) },
            edit => { history = edit; pushes++; });
        Assert.Equal(1, pushes); Assert.Equal(1, notifications); Assert.True(document.IsDirty);
        string changed = document.Fingerprint;
        history!.ApplyBefore(); history.AfterApply();
        Assert.Equal(original, document.Fingerprint); Assert.False(document.IsDirty);
        history.ApplyAfter(); history.AfterApply();
        Assert.Equal(changed, document.Fingerprint); Assert.Equal(3, notifications);
    }

    [Fact]
    public void FailedHistoryRegistrationRollsBackBothAliasesWithoutNotifying()
    {
        var document = new SampledTimingDocument();
        document.Load(new Dictionary<TimingNoteAddress, SampledTimingPlan> { [Left] = Plan(Left, 2, -.04f), [Right] = Plan(Right, 0, -.04f) });
        string before = document.Fingerprint; int notifications = 0;
        document.Changed += () => notifications++;
        Assert.Throws<InvalidOperationException>(() => document.EditMany(
            new Dictionary<TimingNoteAddress, SampledTimingPlan?> { [Left] = null, [Right] = null }, _ => throw new InvalidOperationException()));
        Assert.Equal(before, document.Fingerprint); Assert.False(document.IsDirty); Assert.Equal(0, notifications);
    }

    [Fact]
    public void BatchUndoFollowsBothNativeNoteRelocations()
    {
        var document = new SampledTimingDocument();
        var history = document.EditMany(new Dictionary<TimingNoteAddress, SampledTimingPlan?>
            { [Left] = Plan(Left, 2, .02f), [Right] = Plan(Right, 0, .02f) }, _ => { })!;
        var movedLeft = Left with { Position = 1920 }; var movedRight = Right with { Position = 2460 };
        document.Relocate(new Dictionary<TimingNoteAddress, TimingNoteAddress> { [Left] = movedLeft, [Right] = movedRight });
        history.ApplyBefore(); Assert.Empty(document.Snapshot());
        history.ApplyAfter();
        Assert.NotNull(document.Get(movedLeft)); Assert.NotNull(document.Get(movedRight));
        Assert.Null(document.Get(Left)); Assert.Null(document.Get(Right));
    }
}
