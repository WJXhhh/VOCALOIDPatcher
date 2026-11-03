using System.Collections.Immutable;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class TimingMcpContractTests
{
    private static TimingEdit Parse(string json)
    { using var doc = JsonDocument.Parse(json); var edit = TimingContract.Parse<TimingEdit>(doc.RootElement); edit.Validate(); return edit; }

    [Theory]
    [InlineData("boundary.set", "\"boundary_index\":1,\"offset_frames\":-2")]
    [InlineData("boundary.move", "\"boundary_index\":1,\"delta_frames\":2")]
    [InlineData("boundary.reset", "\"boundary_index\":0")]
    [InlineData("note.reset", "")]
    [InlineData("coefficient.set", "\"coefficient\":0.4")]
    [InlineData("coefficient.reset", "")]
    [InlineData("phoneme_gain.set", "\"phoneme_index\":1,\"decibels\":-60")]
    [InlineData("phoneme_gain.reset", "")]
    [InlineData("comp.set", "\"comp\":{\"isolation\":{\"after\":true}}")]
    [InlineData("comp.reset", "")]
    public void NoteOperationsHaveConcreteLosslessContracts(string op, string fields)
    {
        var edit = Parse("{\"op\":\"" + op + "\",\"note_index\":17" + (fields.Length == 0 ? "" : "," + fields) + "}");
        var roundtrip = JsonSerializer.SerializeToElement(edit, BridgeProtocol.JsonOptions);
        Assert.Equal(edit, TimingContract.Parse<TimingEdit>(roundtrip));
    }
    [Theory]
    [InlineData("{\"op\":\"boundary.move\",\"note_index\":0,\"boundary_index\":1}")]
    [InlineData("{\"op\":\"boundary.move\",\"note_index\":0,\"boundary_index\":1,\"delta_frames\":1,\"offset_frames\":2}")]
    [InlineData("{\"op\":\"boundary.reset\",\"note_index\":0,\"boundary_index\":1,\"delta_frames\":1}")]
    [InlineData("{\"op\":\"coefficient.set\",\"note_index\":0,\"coefficient\":1.1}")]
    [InlineData("{\"op\":\"phoneme_gain.set\",\"note_index\":0,\"phoneme_index\":0,\"decibels\":12.1}")]
    [InlineData("{\"op\":\"transition.set\",\"transition_id\":\"id\",\"layout_fingerprint\":\"fp\",\"phase\":-1.1}")]
    [InlineData("{\"op\":\"transition.set\",\"transition_id\":\"id\",\"layout_fingerprint\":\"fp\",\"onset_frames\":1}")]
    [InlineData("{\"op\":\"transition.set\",\"transition_id\":\"id\",\"layout_fingerprint\":\"fp\",\"coverage\":\"maybe\"}")]
    [InlineData("{\"op\":\"note.reset\",\"note_index\":-1}")]
    [InlineData("{\"op\":\"duration.set\",\"note_index\":0}")]
    [InlineData("{\"op\":\"note.reset\",\"note_index\":0,\"typo\":1}")]
    [InlineData("{\"op\":\"note.reset\",\"note_index\":0,\"note_index\":1}")]
    [InlineData("{\"op\":\"comp.set\",\"note_index\":0,\"comp\":{\"body\":{\"file_path\":\"x\"}}}")]
    [InlineData("\"{\\\"op\\\":\\\"note.reset\\\"}\"")]
    public void InvalidAmbiguousOrRetiredInputsAreRejected(string json) => Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact]
    public void CompRoundtripPreservesEverySourceAxisAndDoesNotInventFileInputs()
    {
        var source = new TimingCompSource("zhong", "ts` UN", 1800, "keep_tail", -10, 1, 20, 40,
            new(2, 3, 4, 1), new("a", "t a_n", 550), new(70, 20, 30, 40, 50, 60, true, false));
        var dto = new TimingCompRecipe(source, source, source with { Trim = "middle" }, new(true, 150, 8, 2200), 6, true);
        var model = TimingMcpEdits.Comp(dto);
        Assert.Equal(dto, TimingMcpEdits.Comp(model));
        Assert.Throws<ArgumentException>(() => TimingMcpEdits.Comp(dto with { Body = source with { Trim = "typo" } }));
        Assert.Throws<InvalidDataException>(() => TimingMcpEdits.Comp(dto with { Vowel = null }));
        Assert.Throws<InvalidDataException>(() => TimingMcpEdits.Comp(dto with { Isolation = new(true, 1001) }));
        Assert.Throws<InvalidDataException>(() => TimingMcpEdits.Comp(dto with { Body = source with { LengthMs = double.NaN } }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"crossfade_ms\":0}")]
    [InlineData("{\"loudness_window_ms\":301}")]
    [InlineData("{\"timing_offset_ms\":-51}")]
    [InlineData("{\"smoothing\":true,\"smoothing\":false}")]
    public void SettingsValidateBeforeAnyPreferenceIsWritten(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => TimingContract.Parse<TimingSettingsEdit>(doc.RootElement).Validate());
    }

    private static TransitionLayout Layout(long revision = 1)
    {
        var note = new TimingNoteAddress(0, 1, 17, 960, 960, 60, "bank", "t a");
        var row = new TransitionSegment(90, 110, 130, "t", "a", 1, 2, 0, false, "source",
            Enumerable.Range(90, 40).Select(f => new TransitionFrame(f, 0, 0, 0)).ToImmutableArray())
            { MaterialCoverage = new(new(0, 25), new(26, 61)) };
        return new(Guid.NewGuid(), revision, "input", [row], [row], [new(note, [85, 110, 180])],
            [new(0, 0), new(85, 85), new(110, 110), new(180, 180), new(300, 300)]);
    }
    [Fact]
    public void TransitionIdsAreStableWithinALayoutAndRejectDifferentRevisionsOrForeignOccurrences()
    {
        var layout = Layout(); var same = Layout(); var edge = layout.Edges[0]; var id = TimingMcpEdits.Id(layout, edge.Identity);
        Assert.Equal(id, TimingMcpEdits.Id(same, same.Edges[0].Identity));
        Assert.NotEqual(id, TimingMcpEdits.Id(Layout(2), edge.Identity));
        Assert.Equal(edge, TimingMcpEdits.Edge(layout, layout.Fingerprint, id));
        Assert.Throws<InvalidOperationException>(() => TimingMcpEdits.Edge(layout, "stale", id));
        Assert.Throws<ArgumentException>(() => TimingMcpEdits.Edge(layout, layout.Fingerprint, "foreign"));
    }
    [Fact]
    public void ProgressAndCoverageAreIndependentForMcpIncludingExplicitNoneAndZero()
    {
        var layout = Layout(); var edge = layout.Edges[0];
        var neutral = new TransitionControlPlan(layout.Fingerprint);
        var execution = new TransitionExecutionPlan(layout, neutral.Compile(layout), [edge.Current], 0, false, 0);
        var request = new TimingEdit { Op = "transition.set", LayoutFingerprint = layout.Fingerprint,
            TransitionId = TimingMcpEdits.Id(layout, edge.Identity), Phase = .001f };
        var tiny = TimingMcpEdits.Transition(request, layout, neutral, execution);
        Assert.Empty(tiny.Coverages);
        Assert.Empty(TimingMcpEdits.Transition(request with { Phase = 0 }, layout, tiny, execution).Phases);
        var full = TimingMcpEdits.Transition(request with { Coverage = "auto" }, layout, neutral, execution);
        Assert.Single(full.Coverages);
        Assert.Single(TimingMcpEdits.Transition(request with { Phase = 0 }, layout, full, execution).Coverages);
        var native = TimingMcpEdits.Transition(request with { Coverage = "none" }, layout, full, execution);
        Assert.Empty(native.Coverages); Assert.Single(native.Phases);
    }
    [Fact]
    public void CombinedPrecisionEditAndResetsPreserveIndependentAxesAndHaveOneUndoStep()
    {
        var layout = Layout(); var edge = layout.Edges[0]; var neutral = new TransitionControlPlan(layout.Fingerprint);
        var execution = new TransitionExecutionPlan(layout, neutral.Compile(layout), [edge.Current], 0, false, 0);
        TimingEdit Request(string op) => new() { Op = op, TransitionId = TimingMcpEdits.Id(layout, edge.Identity), LayoutFingerprint = layout.Fingerprint };
        var edit = Request("transition.set") with { BeginFrame = 92, EndFrame = 128, Decibels = -6, Phase = .5f, Coverage = "both" };
        var controlled = TimingMcpEdits.Transition(edit, layout, neutral, execution);
        Assert.Equal(2, controlled.Junctions.Length); Assert.Equal(-6, Assert.Single(controlled.Gains).Decibels);
        Assert.Equal(.5f, Assert.Single(controlled.Phases).Amount); Assert.Equal(TransitionSourceSides.Both, Assert.Single(controlled.Coverages).Sides);
        var expressionReset = TimingMcpEdits.Transition(Request("transition.reset_expression"), layout, controlled, execution);
        Assert.Empty(expressionReset.Phases); Assert.Empty(expressionReset.Coverages); Assert.True(controlled.Gains.SequenceEqual(expressionReset.Gains));
        var reset = TimingMcpEdits.Transition(Request("transition.reset"), layout, controlled, execution);
        Assert.Equal(neutral.Compile(layout).Fingerprint, reset.Compile(layout).Fingerprint);
        Assert.Throws<NotSupportedException>(() => TimingMcpEdits.Transition(edit with { BeginFrame = 110 }, layout, neutral, execution));
        var doc = new TransitionDocument(); var before = doc.Snapshot.Fingerprint;
        ICustomParameterHistoryEdit? history = null; int pushes = 0;
        doc.Edit(TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, controlled), h => { history = h; pushes++; });
        Assert.Equal(1, pushes); var after = doc.Snapshot.Fingerprint;
        history!.ApplyBefore(); history.AfterApply(); Assert.Equal(before, doc.Snapshot.Fingerprint);
        history.ApplyAfter(); history.AfterApply(); Assert.Equal(after, doc.Snapshot.Fingerprint);
    }
    [Fact]
    public void ResettingAllBoundariesClearsNeighborAliasesAndPreservesTheirOtherControls()
    {
        var left = new TimingNoteAddress(0, 0, 0, 0, 480, 60, "bank", "a"); var target = left with { Note = 1, Position = 480, Topology = "t a" };
        var right = left with { Note = 2, Position = 960 };
        SampledTimingPlan Plan(TimingNoteAddress a, int index) => new(a.Topology, [new(TimingAdjustment.NoteCoefficient, -1, .4f), new(TimingAdjustment.PhonemeBoundary, index, .02f)]);
        var plans = PhonemeBoundaryEdit.ResolveMany([new(left, [10, 20], Plan(left, 1)), new(target, [20, 25, 30], Plan(target, 0)),
            new(right, [30, 40], Plan(right, 0))], 1, [0, 1, 2], new(target.Topology, []));
        Assert.Null(plans[target]); Assert.False(plans[left]!.HasPhonemeControls); Assert.False(plans[right]!.HasPhonemeControls);
        Assert.Equal(.4f, plans[left]!.PhonemeCoefficient(left.Topology)); Assert.Equal(.4f, plans[right]!.PhonemeCoefficient(right.Topology));
    }
}
