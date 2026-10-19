using System.Collections.Immutable;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionPhaseTests
{
    private static TransitionSegment Row(int begin = 90, int split = 100, int end = 125)
        => new(begin, split, end, "Sil", "a", 1, 2, 0, false, "source",
            Enumerable.Range(begin, end - begin).Select((f, i) => new TransitionFrame(f, BitConverter.SingleToInt32Bits(i), (byte)(i % 2), 0)).ToImmutableArray());
    private static TransitionLayout Layout()
    {
        var note = new TimingNoteAddress(0, 0, 0, 960, 960, 60, "bank", "a");
        return new(Guid.NewGuid(), 1, "input", [Row()], [Row()], [new(note, [100, 240])], [new(0, 0), new(100, 100), new(240, 240), new(300, 300)]);
    }
    [Theory]
    [InlineData(-1f)] [InlineData(-.5f)] [InlineData(0f)] [InlineData(.5f)] [InlineData(1f)]
    public void BothHalvesKeepFrameCoordinatesFlagsSourceEndpointsAndSplit(float amount)
    {
        var row = Row(); var original = row.Frames.ToArray(); var result = TransitionPhaseMapping.Apply(row, amount);
        Assert.Equal(original, row.Frames);
        foreach (int f in new[] { row.Begin, row.Split - 1, row.Split, row.End - 1 })
            Assert.Equal(row.Frames[f - row.Begin], result[f - row.Begin]);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].Frame, result[i].Frame);
            Assert.Equal((original[i].Flag0, original[i].Flag1), (result[i].Flag0, result[i].Flag1));
            int lo = original[i].Frame < row.Split ? 0 : row.Split - row.Begin;
            int hi = original[i].Frame < row.Split ? row.Split - row.Begin - 1 : original.Length - 1;
            Assert.InRange(BitConverter.Int32BitsToSingle(result[i].SourceBits), BitConverter.Int32BitsToSingle(original[lo].SourceBits), BitConverter.Int32BitsToSingle(original[hi].SourceBits));
        }
        if (amount == 0) Assert.Equal(original, result);
        else Assert.NotEqual(original.Select(f => f.SourceBits), result.Select(f => f.SourceBits));
        double handle = TransitionPhaseMapping.HandleFrame(row, amount);
        Assert.InRange(handle, row.Split, row.End - 1);
        Assert.InRange(Math.Abs(TransitionPhaseMapping.AmountAt(row, handle) - amount), 0, 1e-5);
    }
    [Fact]
    public void ShortOrConstantIntervalsKeepTheMathematicallyDefinedIdentityAndDisableEditing()
    {
        var shortRow = Row(90, 92, 94);
        Assert.False(TransitionPhaseMapping.CanEdit(shortRow));
        Assert.True(shortRow.Frames.SequenceEqual(TransitionPhaseMapping.Apply(shortRow, .5f)));
        var flat = Row() with { Frames = Row().Frames.Select(f => f with { SourceBits = 0 }).ToImmutableArray() };
        Assert.False(TransitionPhaseMapping.CanEdit(flat));
        Assert.True(flat.Frames.SequenceEqual(TransitionPhaseMapping.Apply(flat, .5f)));
        Assert.Throws<InvalidDataException>(() => TransitionPhaseMapping.Apply(Row(), float.NaN));
    }
    [Fact]
    public void ArchiveSeparatesLegacyOffsetsFromDimensionlessPhaseAndPreservesBoth()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var controls = new TransitionControlPlan(layout.Fingerprint, gains: [new(edge, -6)]).WithOnset(edge, -8).WithPhase(edge, .375f);
        var data = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, controls);
        var archive = TransitionArchiveData.Parse(TransitionArchiveData.From(data).Payload.Span);
        var fresh = Layout(); var loaded = TransitionProjectBinding.Resolve(archive.Data!, fresh);
        Assert.Equal(-8, Assert.Single(loaded.Onsets).OffsetFrames);
        Assert.Equal(.375f, Assert.Single(loaded.Phases).Amount);
        Assert.Equal(-6, Assert.Single(loaded.Gains).Decibels);
        Assert.True(controls.Compile(layout).Anchors.SequenceEqual(loaded.Compile(fresh).Anchors));
        var cleared = loaded.WithPhase(fresh.Edges[0].Identity, 0);
        Assert.Single(cleared.Onsets); Assert.Single(cleared.Gains); Assert.Empty(cleared.Phases);
        Assert.Empty(data.KeepNotes(_ => false).Phases);
        var unknown = data with { Phases = [data.Phases[0] with { Extra = new() { ["future"] = JsonSerializer.SerializeToElement(1) } }] };
        var opaque = TransitionArchiveData.From(unknown);
        Assert.False(opaque.Executable); Assert.True(opaque.HasData);
        Assert.Equal(opaque.Payload.ToArray(), TransitionArchiveData.Parse(opaque.Payload.Span).Payload.ToArray());
    }
    [Fact]
    public void PhaseOnlyDataRetainsItsNoteAndSharedUndoRestoresTheWholeCombination()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var plan = new TransitionControlPlan(layout.Fingerprint).WithPhase(edge, -.5f);
        var before = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, plan);
        Assert.Single(before.Notes); Assert.True(before.HasControls); Assert.True(TransitionProjectBinding.HasPart(before, 0, 0));
        var after = TransitionProjectBinding.Encode(before, layout, plan.WithPhase(edge, .5f));
        var doc = new TransitionDocument(); doc.Load(TransitionArchiveData.From(before));
        var edits = new List<ICustomParameterHistoryEdit>(); doc.Edit(after, edits.Add);
        Assert.Single(edits); Assert.True(doc.IsDirty);
        edits[0].ApplyBefore(); edits[0].AfterApply(); Assert.Equal(-.5f, doc.Snapshot.Data!.Phases.Single().Amount);
        edits[0].ApplyAfter(); edits[0].AfterApply(); Assert.Equal(.5f, doc.Snapshot.Data!.Phases.Single().Amount);
        var reset = TransitionProjectBinding.Encode(after, layout, plan.WithPhase(edge, 0));
        Assert.False(reset.HasControls); Assert.Empty(reset.Notes);
    }
    [Fact]
    public void RejectsForeignDuplicateAndOutOfRangePhaseControls()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(layout.Fingerprint, phases: [new(edge, .5f), new(edge, -.5f)]));
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(layout.Fingerprint).WithPhase(edge, 1.1f));
        Assert.Throws<InvalidDataException>(() => new TransitionControlPlan(layout.Fingerprint).WithPhase(edge with { Occurrence = 10 }, .5f).Compile(layout));
        var a = new TransitionControlPlan(layout.Fingerprint).WithPhase(edge, .5f);
        var b = a.WithPhase(edge, -.5f);
        Assert.NotEqual(a.Compile(layout).Fingerprint, b.Compile(layout).Fingerprint);
        Assert.Equal(a.Compile(layout).Anchors, b.Compile(layout).Anchors);
    }
    [Fact]
    public void MissingNewFieldsAreANoopAndUndoRestoresOriginalArchiveBytesAndSavedState()
    {
        var layout = Layout(); var edge = layout.Edges[0].Identity;
        var data = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout,
            new TransitionControlPlan(layout.Fingerprint).WithOnset(edge, -8));
        var node = System.Text.Json.Nodes.JsonNode.Parse(TransitionArchiveData.From(data).Payload.Span)!;
        node.AsObject().Remove("Phases");
        byte[] original = System.Text.Encoding.UTF8.GetBytes(" \n" + node.ToJsonString() + "\n");
        var archive = TransitionArchiveData.Parse(original);
        var doc = new TransitionDocument(); doc.Load(archive);
        var edits = new List<ICustomParameterHistoryEdit>();
        doc.Edit(archive.Data!, edits.Add); Assert.Empty(edits); Assert.False(doc.IsDirty);
        var after = TransitionProjectBinding.Encode(archive.Data!, layout,
            TransitionProjectBinding.Resolve(archive.Data!, layout).WithPhase(edge, .5f));
        doc.Edit(after, edits.Add); Assert.Single(edits); Assert.True(doc.IsDirty);
        edits[0].ApplyBefore(); edits[0].AfterApply();
        Assert.Equal(original, doc.Snapshot.Payload.ToArray()); Assert.False(doc.IsDirty);
        edits[0].ApplyAfter(); edits[0].AfterApply(); Assert.Single(doc.Snapshot.Data!.Phases);
    }
}
