using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionProjectTests
{
    [Fact]
    public void SourceProgressPersistsSeparatelyFromProtectedPhonemeAndJunctions()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var plan = new TransitionControlPlan(layout.Fingerprint).WithOnset(edge, -24);
        Assert.Equal(layout.Anchors, plan.Compile(layout).Anchors);
        var data = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, plan);
        Assert.Empty(data.Junctions); Assert.Single(data.Onsets); Assert.Single(data.Notes);
        var archive = TransitionArchiveData.Parse(TransitionArchiveData.From(data).Payload.Span);
        var fresh = Layout(); var roundtrip = TransitionProjectBinding.Resolve(archive.Data!, fresh);
        Assert.Equal(-24, Assert.Single(roundtrip.Onsets).OffsetFrames);
        Assert.Empty(roundtrip.WithOnset(fresh.Edges[0].Identity, 0).Onsets);
        Assert.Empty(TransitionProjectBinding.Encode(data, fresh, roundtrip.WithOnset(fresh.Edges[0].Identity, 0)).Notes);
        Assert.Empty(data.KeepNotes(_ => false).Onsets);
    }

    [Fact]
    public void SourceProgressRejectsDuplicatesForeignEdgesAndNonFiniteProjectValues()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(layout.Fingerprint, onsets: [new(edge, -8), new(edge, -9)]));
        Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint,
            onsets: [new(layout.Edges[1].Identity, -8)]).Compile(layout));
        Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint).WithOnset(edge, 1).Compile(layout));
        var data = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, new TransitionControlPlan(layout.Fingerprint).WithOnset(edge, -8));
        Assert.Throws<InvalidDataException>(() => (data with { Onsets = [data.Onsets[0] with { OffsetSeconds = double.NaN }] }).Validate());
    }

    [Fact]
    public void SourceProgressUsesSharedUndoWithoutLosingGainOrEndpointEdits()
    {
        var layout = Layout(); var before = Controlled(layout);
        var plan = TransitionProjectBinding.Resolve(before, layout).WithOnset(layout.Edges[0].Identity, -8);
        var after = TransitionProjectBinding.Encode(before, layout, plan);
        var document = new TransitionDocument(); document.Load(TransitionArchiveData.From(before));
        var edits = new List<ICustomParameterHistoryEdit>(); document.Edit(after, edits.Add);
        Assert.Single(document.Snapshot.Data!.Onsets); Assert.Single(document.Snapshot.Data.Gains); Assert.Single(document.Snapshot.Data.Junctions);
        edits[0].ApplyBefore(); edits[0].AfterApply(); Assert.Empty(document.Snapshot.Data!.Onsets);
        edits[0].ApplyAfter(); edits[0].AfterApply(); Assert.Single(document.Snapshot.Data!.Onsets);
    }

    private static TransitionLayout Layout(int offset = 0, long position = 960)
    {
        var note = new TimingNoteAddress(0, 0, 0, position, 960, 60, "bank", "a b");
        TransitionSegment Row(int l, int s, int r, string from, string to) => new(l + offset, s + offset, r + offset, from, to, 1, 2, 0, false, "source", []);
        TransitionSegment[] rows = [Row(90, 100, 125, "Sil", "a"), Row(125, 180, 200, "a", "b")];
        return new(Guid.NewGuid(), 1, "input", rows, rows, [new(note, [100 + offset, 180 + offset, 240 + offset])],
            [new(0, 0), new(100 + offset, 100 + offset), new(180 + offset, 180 + offset), new(240 + offset, 240 + offset), new(300 + offset, 300 + offset)]);
    }
    private static TransitionProjectData Controlled(TransitionLayout layout)
        => TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, new(layout.Fingerprint,
            [new(new(layout.Edges[0].Identity, TransitionEndpoint.End), layout.Edges[0].Current.End + 2)],
            [new(layout.Edges[1].Identity, -6)]));

    [Fact]
    public void SharedAliasesPersistOnceAndResolveAgainstFreshPreparation()
    {
        var layout = Layout(); var data = Controlled(layout);
        Assert.Single(data.Junctions); Assert.Equal(2, data.Junctions[0].Aliases.Length);
        var reloaded = TransitionArchiveData.Parse(TransitionArchiveData.From(data).Payload.Span).Data!;
        var fresh = Layout(); var controls = TransitionProjectBinding.Resolve(reloaded, fresh);
        Assert.Equal(127, controls.Junctions.Single().Frame);
        Assert.Equal(-6, controls.Gains.Single().Decibels);
        Assert.Equal(data.Notes[0].Id, reloaded.Notes[0].Id);
    }

    [Fact]
    public void RelativeOffsetsFollowExplicitNativeRelocationAndNeverNearestName()
    {
        var data = Controlled(Layout()); var moved = Layout(500, 4800);
        Assert.Throws<NotSupportedException>(() => TransitionProjectBinding.Resolve(data, moved));
        var relocated = data with { Notes = [data.Notes[0] with { Address = moved.Notes[0].Address }] };
        var resolved = TransitionProjectBinding.Resolve(relocated, moved);
        Assert.Equal(627, resolved.Junctions.Single().Frame);
    }

    [Fact]
    public void OneSharedHistoryEditSurvivesNativeMoveThenControlUndoRedo()
    {
        var document = new TransitionDocument(); var edits = new List<ICustomParameterHistoryEdit>(); int notifications = 0;
        document.Changed += () => notifications++;
        var data = Controlled(Layout()); document.Edit(data, edits.Add);
        Assert.Single(edits); Assert.True(document.IsDirty);
        var moved = data.Notes[0].Address with { Position = 1920 };
        document.Relocate(new Dictionary<Guid, TimingNoteAddress> { [data.Notes[0].Id] = moved });
        edits[0].ApplyBefore(); edits[0].AfterApply();
        Assert.False(document.Snapshot.HasData); Assert.False(document.IsDirty);
        edits[0].ApplyAfter(); edits[0].AfterApply();
        Assert.Equal(moved, document.Snapshot.Data!.Notes.Single().Address);
        Assert.Equal(3, notifications); Assert.Single(edits);
    }

    [Fact]
    public void RejectedHistoryPushRollsBackTheWholeDocument()
    {
        var document = new TransitionDocument(); string before = document.Snapshot.Fingerprint;
        Assert.Throws<InvalidOperationException>(() => document.Edit(Controlled(Layout()), _ => throw new InvalidOperationException()));
        Assert.Equal(before, document.Snapshot.Fingerprint); Assert.False(document.IsDirty);
    }

    [Fact]
    public void IncompleteSharedAliasSetCannotBeReboundSilently()
    {
        var data = Controlled(Layout());
        var bad = data with { Junctions = [data.Junctions[0] with { Aliases = [data.Junctions[0].Aliases[0]] }] };
        Assert.Throws<NotSupportedException>(() => TransitionProjectBinding.Resolve(bad, Layout()));
    }

    [Fact]
    public void ReplacedPronunciationHasNoActiveOldTargetsAndDoesNotRewriteUndoData()
    {
        var layout = Layout();
        var data = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout,
            new TransitionControlPlan(layout.Fingerprint,
                [new(new(layout.Edges[0].Identity, TransitionEndpoint.End), 127)],
                [new(layout.Edges[1].Identity, -6)], [new(layout.Edges[0].Identity, -8)],
                [new(layout.Edges[0].Identity, .5f)]));
        var archive = TransitionArchiveData.From(data);
        var note = layout.Notes[0] with { Address = layout.Notes[0].Address with { Topology = "a c" } };
        var rows = layout.Edges.Select(e => e.Original.To == "b" ? e.Original with { To = "c" } : e.Original).ToArray();
        var changed = new TransitionLayout(Guid.NewGuid(), 2, "changed", rows, rows, [note], layout.Anchors);
        var active = TransitionProjectBinding.ResolveAvailable(data, changed);
        Assert.Empty(active.Junctions); Assert.Empty(active.Gains); Assert.Empty(active.Onsets); Assert.Empty(active.Phases);
        Assert.Equal(archive.Payload.ToArray(), TransitionArchiveData.From(data).Payload.ToArray());
        var restored = TransitionProjectBinding.ResolveAvailable(data, Layout());
        Assert.Single(restored.Junctions); Assert.Single(restored.Gains); Assert.Single(restored.Onsets); Assert.Single(restored.Phases);
    }

    [Fact]
    public void ContextReplacementRetiresWholeSharedJunctionAndOnlyAbsentEdgeControls()
    {
        var layout = Layout(); var data = Controlled(layout);
        var plan = TransitionProjectBinding.Resolve(data, layout).WithOnset(layout.Edges[0].Identity, -8)
            .WithPhase(layout.Edges[0].Identity, .5f).WithPhase(layout.Edges[1].Identity, -.5f);
        data = TransitionProjectBinding.Encode(data, layout, plan);
        var rows = layout.Edges.Select((e, i) => i == 0 ? e.Original with { Stage = 1 } : e.Original).ToArray();
        var changed = new TransitionLayout(Guid.NewGuid(), 2, "context", rows, rows, layout.Notes, layout.Anchors);
        var active = TransitionProjectBinding.ResolveAvailable(data, changed);
        Assert.Empty(active.Junctions); Assert.Empty(active.Onsets);
        Assert.Equal(-6, Assert.Single(active.Gains).Decibels);
        Assert.Equal(layout.Edges[1].Identity, Assert.Single(active.Phases).Edge);
        Assert.Equal(-.5f, active.Phases[0].Amount);
        Assert.Throws<NotSupportedException>(() => TransitionProjectBinding.Resolve(data, changed));
        Assert.Single(TransitionProjectBinding.ResolveAvailable(data, Layout()).Junctions);
    }

    [Fact]
    public void AvailableTargetResolutionStillRejectsInvalidStoredValues()
    {
        var layout = Layout(); var data = Controlled(layout);
        var bad = data with { Gains = [data.Gains[0] with { Decibels = float.NaN }] };
        Assert.Throws<InvalidDataException>(() => TransitionProjectBinding.ResolveAvailable(bad, layout));
    }

    [Fact]
    public void FilteringDeletedBoundSideDropsItsControlsWithoutChangingOtherParts()
    {
        var data = Controlled(Layout());
        Assert.False(data.KeepNotes(_ => false).HasControls);
        Assert.Empty(data.KeepNotes(_ => false).Notes);
        Assert.Equal(TransitionArchiveData.From(data).Fingerprint, TransitionArchiveData.From(data.KeepNotes(_ => true)).Fingerprint);
    }

    [Fact]
    public void FutureSchemaAndNestedFieldsRoundTripWithoutNativeAdapter()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vpr");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("keep.txt").Open()); writer.Write("keep"); }
            byte[] bytes = Encoding.UTF8.GetBytes("{\"Version\":22,\"Unknown\":{\"keep\":[1,2,3]}}");
            var future = TransitionArchiveData.Parse(bytes);
            Assert.False(future.Executable);
            var document = new TransitionDocument(); document.Load(future);
            TransitionProjectArchive.Write(path, document.Snapshot);
            Assert.Equal(bytes, TransitionProjectArchive.Read(path).Payload.ToArray());
            using (var zip = ZipFile.OpenRead(path)) Assert.NotNull(zip.GetEntry("keep.txt"));
            Assert.Throws<NotSupportedException>(() => document.Edit(TransitionProjectData.Empty, _ => { }));
            var data = Controlled(Layout());
            using var metadata = JsonDocument.Parse("{\"roundtrip\":true}");
            var nested = data with { Gains = [data.Gains[0] with { Extra = new() { ["FutureGain"] = metadata.RootElement.Clone() } }] };
            var saved = TransitionArchiveData.From(nested);
            Assert.False(saved.Executable); Assert.True(saved.HasData);
            TransitionProjectArchive.Write(path, saved);
            Assert.Equal("{\"roundtrip\":true}", TransitionProjectArchive.Read(path).Data!.Gains[0].Extra!["FutureGain"].GetRawText());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AuthorizedReplacementAtSameLocatorDoesNotReuseDeletedNoteIdentity()
    {
        var layout = Layout(); var old = Controlled(layout);
        var freshId = Guid.NewGuid();
        var replacement = TransitionProjectBinding.Encode(old, layout, new(layout.Fingerprint,
            gains: [new(layout.Edges[1].Identity, -4)]), new Dictionary<TimingNoteAddress, Guid> { [layout.Notes[0].Address] = freshId });
        Assert.Equal(freshId, replacement.Notes.Single().Id);
        Assert.NotEqual(old.Notes.Single().Id, replacement.Gains.Single().Edge.Left!.Note);
        Assert.Equal(freshId, replacement.Gains.Single().Edge.Left!.Note);
        Assert.Single(TransitionProjectBinding.Resolve(replacement, Layout()).Gains);
    }

    [Fact]
    public void UnknownAddressFieldsArePreservedAsOpaqueBytes()
    {
        string json = Encoding.UTF8.GetString(TransitionArchiveData.From(Controlled(Layout())).Payload.Span);
        byte[] bytes = Encoding.UTF8.GetBytes(json.Replace("\"Address\":{", "\"Address\":{\"FutureAddress\":{\"keep\":true},"));
        var value = TransitionArchiveData.Parse(bytes);
        Assert.Null(value.Data); Assert.False(value.Executable);
        var document = new TransitionDocument(); document.Load(value);
        document.Relocate(new Dictionary<Guid, TimingNoteAddress>());
        Assert.Equal(bytes, document.Snapshot.Payload.ToArray());
    }

    [Fact]
    public void MalformedDuplicateAndCrossPartIdentitiesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TransitionArchiveData.Parse(Encoding.UTF8.GetBytes("{\"Version\":2,\"Version\":3}")));
        var data = Controlled(Layout());
        Assert.Throws<InvalidDataException>(() => TransitionArchiveData.From(data with { Notes = [data.Notes[0], data.Notes[0]] }));
        var gain = data.Gains[0];
        Assert.Throws<InvalidDataException>(() => TransitionArchiveData.From(data with { Gains = [gain, gain] }));
        Assert.Throws<InvalidDataException>(() => TransitionArchiveData.From(data with { Gains = [gain with { Decibels = float.NaN }] }));
    }

    [Fact]
    public void ConditionalDomainUsesOtherEditedJunctionRatherThanBaseline()
    {
        var note = new TimingNoteAddress(0, 0, 0, 960, 960, 60, "bank", "a b");
        TransitionSegment[] rows = [new(90, 100, 130, "Sil", "a", 1, 2, 0, false, "s", []), new(150, 180, 200, "a", "b", 1, 2, 0, false, "s", [])];
        var layout = new TransitionLayout(Guid.NewGuid(), 1, "conditional", rows, rows, [new(note, [100, 180, 240])],
            [new(0, 0), new(100, 100), new(180, 180), new(240, 240), new(300, 300)]);
        var a = new TransitionEndpointRef(layout.Edges[0].Identity, TransitionEndpoint.End);
        var b = new TransitionEndpointRef(layout.Edges[1].Identity, TransitionEndpoint.Begin);
        var plan = new TransitionControlPlan(layout.Fingerprint, [new(a, 120)]);
        Assert.Equal(131, layout.Junction(b).MinimumCandidate);
        Assert.Equal(121, layout.ConditionalJunction(b, plan).MinimumCandidate);
        var compiled = plan.WithJunction(layout, b, 125).Compile(layout);
        Assert.Equal(125, compiled.Anchors.Single(p => p.From == 150).To);
        Assert.Throws<NotSupportedException>(() => plan.WithJunction(layout, b, 120).Compile(layout));
    }
}
