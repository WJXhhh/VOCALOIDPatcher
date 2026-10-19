using System.Collections.Immutable;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionTimingScaleTests
{
    private static TransitionLayout Layout(int width = 140)
    {
        var address = new TimingNoteAddress(0, 0, 0, 960, 960, 60, "bank", "a");
        TransitionSegment Row(int begin, int split, int end, string from, string to) => new(begin, split, end, from, to, 1, 2, 0, false, "source", []);
        TransitionSegment[] original = [Row(90, 100, 125, "Sil", "a"), Row(200, 240, 260, "a", "Sil")];
        TransitionAnchor[] anchors = [new(0, 0), new(100, 100), new(240, 100 + width), new(400, 400)];
        int Map(int frame)
        {
            int i = Array.FindIndex(anchors, a => a.From > frame);
            if (i < 1) return frame;
            var a = anchors[i - 1]; var b = anchors[i];
            return a.To + (int)Math.Round((frame - a.From) * (b.To - (double)a.To) / (b.From - a.From), MidpointRounding.AwayFromZero);
        }
        return new(Guid.NewGuid(), 1, "scale", original, original.Select(r => r with { Begin = Map(r.Begin), Split = Map(r.Split), End = Map(r.End) }),
            [new(address, [100, 240])], anchors);
    }

    private static TransitionProjectData Controlled()
    {
        var layout = Layout();
        return TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout,
            new(layout.Fingerprint, [new(new(layout.Edges[0].Identity, TransitionEndpoint.Begin), 66)],
                [new(layout.Edges[0].Identity, -6)], [new(layout.Edges[0].Identity, -12)], [new(layout.Edges[0].Identity, -.75f)]));
    }

    [Fact]
    public void ExtensionsScaleWithPresetWhilePhaseAndGainRemainIndependent()
    {
        var data = Controlled(); var small = Layout(7);
        var plan = TransitionProjectBinding.Resolve(data, small);
        Assert.Equal(89, Assert.Single(plan.Junctions).Frame); // -24 * 7 / 140 rounds to -1
        Assert.Equal(-1, Assert.Single(plan.Onsets).OffsetFrames);
        Assert.Equal(-6, Assert.Single(plan.Gains).Decibels); Assert.Equal(-.75f, Assert.Single(plan.Phases).Amount);
        var restored = TransitionProjectBinding.Resolve(data, Layout());
        Assert.Equal(66, Assert.Single(restored.Junctions).Frame); Assert.Equal(-12, Assert.Single(restored.Onsets).OffsetFrames);
    }

    [Fact]
    public void QuantizedZeroIntentSurvivesSaveAndUnrelatedEditButExplicitResetRemovesIt()
    {
        var large = Layout(); var edge = large.Edges[0].Identity;
        var data = TransitionProjectBinding.Encode(TransitionProjectData.Empty, large,
            new TransitionControlPlan(large.Fingerprint).WithOnset(edge, -1).WithJunction(large, new(edge, TransitionEndpoint.Begin), 89));
        var small = Layout(7); var controls = TransitionProjectBinding.Resolve(data, small);
        Assert.Equal(0, Assert.Single(controls.Onsets).OffsetFrames);
        Assert.Equal(90, Assert.Single(controls.Junctions).Frame);
        var edited = TransitionProjectBinding.Encode(data, small, controls.WithPhase(small.Edges[0].Identity, .5f));
        var reopened = TransitionArchiveData.Parse(TransitionArchiveData.From(edited).Payload.Span).Data!;
        Assert.Equal(data.Onsets[0], reopened.Onsets[0]); Assert.Equal(data.Junctions[0].OffsetSeconds, reopened.Junctions[0].OffsetSeconds);
        var restored = TransitionProjectBinding.Resolve(reopened, Layout());
        Assert.Equal(-1, restored.Onsets[0].OffsetFrames); Assert.Equal(89, restored.Junctions[0].Frame);
        var reset = controls.WithOnset(small.Edges[0].Identity, 0).WithJunction(small, new(small.Edges[0].Identity, TransitionEndpoint.Begin), 90);
        var cleared = TransitionProjectBinding.Encode(data, small, reset);
        Assert.Empty(cleared.Onsets); Assert.Empty(cleared.Junctions);
    }

    [Fact]
    public void LegacyArchiveKeepsItsSoundUntilTimingEditAdoptsReference()
    {
        var current = Controlled();
        var old = current with { Junctions = current.Junctions.Select(j => j with { TimingScale = null }).ToImmutableArray(),
            Onsets = current.Onsets.Select(o => o with { TimingScale = null }).ToImmutableArray() };
        var payload = TransitionArchiveData.From(old).Payload.ToArray();
        Assert.Equal(66, TransitionProjectBinding.Resolve(old, Layout(7)).Junctions[0].Frame);
        Assert.Equal(payload, TransitionArchiveData.From(TransitionArchiveData.Parse(payload).Data!).Payload.ToArray());
        var adopted = TransitionTimingScale.Adopt(old, old, Layout());
        Assert.Equal(89, TransitionProjectBinding.Resolve(adopted, Layout(7)).Junctions[0].Frame);
        Assert.Equal(66, TransitionProjectBinding.Resolve(adopted, Layout()).Junctions[0].Frame);
        Assert.Equal(payload, TransitionArchiveData.From(TransitionTimingScale.Adopt(old, TransitionProjectData.Empty, Layout())).Payload.ToArray());
    }

    [Fact]
    public void TransitionEditedAfterCompressionUsesCurrentPresetAsItsReference()
    {
        var small = Layout(7); var edge = small.Edges[0].Identity;
        var data = TransitionProjectBinding.Encode(TransitionProjectData.Empty, small,
            new TransitionControlPlan(small.Fingerprint).WithJunction(small, new(edge, TransitionEndpoint.Begin), 89));
        Assert.Equal(7, data.Junctions[0].TimingScale!.Frames);
        Assert.Equal(70, TransitionProjectBinding.Resolve(data, Layout()).Junctions[0].Frame);
        Assert.Equal(89, TransitionProjectBinding.Resolve(data, Layout(7)).Junctions[0].Frame);
    }

    [Fact]
    public void InvalidOrForeignReferencesRejectAndUnknownFieldsRemainOpaque()
    {
        var data = Controlled(); var item = data.Junctions[0]; var scale = item.TimingScale!;
        Assert.Throws<InvalidDataException>(() => (data with { Junctions = [item with { TimingScale = scale with { Frames = 0 } }] }).Validate());
        Assert.Throws<InvalidDataException>(() => (data with { Junctions = [item with { TimingScale = scale with { Phone = scale.Phone with { Note = Guid.NewGuid() } } }] }).Validate());
        using var extra = JsonDocument.Parse("1");
        var future = data with { Junctions = [item with { TimingScale = scale with { Extra = new() { ["future"] = extra.RootElement.Clone() } } }] };
        var payload = TransitionArchiveData.From(future).Payload.ToArray(); var archive = TransitionArchiveData.Parse(payload);
        Assert.False(archive.Executable); Assert.Equal(payload, archive.Payload.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedUndoAndRejectedHistoryRestoreBothDocuments(bool reject)
    {
        var phone = new SampledTimingDocument(); var transitions = new TransitionDocument(); var before = Controlled();
        transitions.Load(TransitionArchiveData.From(before)); var after = before with { Gains = [] };
        var edits = new List<ICustomParameterHistoryEdit>(); int depth = 0, notifications = 0; bool pending = false;
        void Changed() { if (depth != 0) pending = true; else notifications++; }
        void Batch(Action action) { depth++; try { action(); } finally { if (--depth == 0 && pending) { pending = false; notifications++; } } }
        phone.Changed += Changed; transitions.Changed += Changed;
        void Execute() => TimingCompositeEdit.Execute(push =>
        {
            phone.Edit(before.Notes[0].Address, new("a", [new(TimingAdjustment.PhonemeBoundary, 0, .01f)]), push);
            transitions.Edit(after, push);
        }, edit => { if (reject) throw new InvalidOperationException(); edits.Add(edit); }, Batch);
        if (reject)
        {
            Assert.Throws<InvalidOperationException>(Execute); Assert.Empty(phone.Snapshot());
            Assert.Equal(TransitionArchiveData.From(before).Fingerprint, transitions.Snapshot.Fingerprint); Assert.Empty(edits);
        }
        else
        {
            Execute(); var edit = Assert.Single(edits); Assert.Single(phone.Snapshot()); Assert.Empty(transitions.Snapshot.Data!.Gains);
            edit.ApplyBefore(); edit.AfterApply(); Assert.Empty(phone.Snapshot()); Assert.Single(transitions.Snapshot.Data!.Gains);
            edit.ApplyAfter(); edit.AfterApply(); Assert.Single(phone.Snapshot()); Assert.Empty(transitions.Snapshot.Data!.Gains);
        }
        Assert.Equal(reject ? 1 : 3, notifications);
    }
}
