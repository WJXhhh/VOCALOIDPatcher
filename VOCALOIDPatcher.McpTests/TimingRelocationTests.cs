using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TimingRelocationTests
{
    [Fact]
    public void HistoricalTimingEditsFollowAuthorizedNativeMovesInBothDirections()
    {
        var doc = new SampledTimingDocument(); var history = new List<ICustomParameterHistoryEdit>();
        var before = new TimingNoteAddress(0, 0, 0, 960, 480, 60, "bank", "k a");
        var after = before with { Note = 1, Position = 1200, Pitch = 62 };
        var plan = new SampledTimingPlan("k a", [new(TimingAdjustment.PhonemeBoundary, 1, .02f)]);
        doc.Edit(before, plan, history.Add);
        doc.Relocate(new Dictionary<TimingNoteAddress, TimingNoteAddress> { [before] = after });
        Assert.Null(doc.Get(before)); Assert.Equal(plan.Fingerprint, doc.Get(after)!.Fingerprint);
        history[0].ApplyBefore(); Assert.Empty(doc.Snapshot());
        history[0].ApplyAfter(); Assert.Equal(after, Assert.Single(doc.Snapshot()).Key);
        doc.Relocate(new Dictionary<TimingNoteAddress, TimingNoteAddress> { [after] = before });
        history[0].ApplyBefore(); history[0].ApplyAfter();
        Assert.Equal(before, Assert.Single(doc.Snapshot()).Key);
    }
    [Fact]
    public void RelocationCollisionCannotPartiallyMutateTheDocument()
    {
        var doc = new SampledTimingDocument(); var a = new TimingNoteAddress(0, 0, 0, 0, 480, 60, "bank", "a");
        var b = a with { Note = 1, Position = 480 };
        doc.Edit(a, new("a", [new(TimingAdjustment.NoteCoefficient, -1, .2f)]), _ => { });
        doc.Edit(b, new("a", [new(TimingAdjustment.NoteCoefficient, -1, .3f)]), _ => { });
        string before = doc.Fingerprint;
        Assert.Throws<InvalidOperationException>(() => doc.Relocate(new Dictionary<TimingNoteAddress, TimingNoteAddress> { [a] = b }));
        Assert.Equal(before, doc.Fingerprint);
    }
}
