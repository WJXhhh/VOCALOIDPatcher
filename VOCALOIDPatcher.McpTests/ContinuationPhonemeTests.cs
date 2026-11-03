using System.Collections.Immutable;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class ContinuationPhonemeTests
{
    [Fact]
    public void FinalContinuationTailRetainsItsSharedOutgoingPhonemeEdit()
    {
        var tail = new SampledTimingPlan("-", [new(TimingAdjustment.PhonemeBoundary, 1, -.017414967f)]);
        ContinuationPhonemes.ValidateBoundaryEdits(tail, continuesAfter: false);
        Assert.Throws<NotSupportedException>(() => ContinuationPhonemes.ValidateBoundaryEdits(tail, continuesAfter: true));
        var start = new SampledTimingPlan("-", [new(TimingAdjustment.PhonemeBoundary, 0, -.017414967f)]);
        Assert.Throws<NotSupportedException>(() => ContinuationPhonemes.ValidateBoundaryEdits(start, continuesAfter: false));
        ContinuationPhonemes.ValidateBoundaryEdits(new("-", [new(TimingAdjustment.NoteCoefficient, -1, .5f)]), true);
        ContinuationPhonemes.ValidateBoundaryEdits(new("-", [new(TimingAdjustment.PhonemeBoundary, 0, 0)]), false);
    }

    private static TransitionNoteFrames Note(int index, string topology, params int[] frames)
        => new(new(0, 0, index, 1920 + index * 240, 240, 62 - index, "bank", topology), frames.ToImmutableArray());

    [Fact]
    public void ContinuationExtendsTheOriginalPhonemeWithoutInventingAnOwner()
    {
        var root = Note(0, "m u", 3504, 3517, 3560);
        var continuation = Note(1, "-", 3560, 3603);
        var notes = ContinuationPhonemes.Join([root, continuation]);
        Assert.Single(notes);
        Assert.Equal(root.Address, notes[0].Address);
        Assert.Equal(new[] { 3504, 3517, 3603 }, notes[0].Original);
        Assert.Equal(3560, root.Original[^1]);
        Assert.Equal(new[] { 0, 0 }, ContinuationPhonemes.Owners([root.Address, continuation.Address]));
    }

    [Fact]
    public void MultipleContinuationsKeepTheRootAndNextIndependentNote()
    {
        var notes = ContinuationPhonemes.Join([Note(0, "m u", 100, 110, 120), Note(1, "-", 120, 130),
            Note(2, "-", 130, 140), Note(3, "l a", 140, 150, 160)]);
        Assert.Equal(2, notes.Length);
        Assert.Equal(new[] { 100, 110, 140 }, notes[0].Original);
        Assert.Equal(3, notes[1].Address.Note);
    }

    [Fact]
    public void MissingNoncontiguousOrUnprovenContinuationIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => ContinuationPhonemes.Join([Note(0, "-", 100, 120)]));
        var root = Note(0, "m u", 100, 110, 120);
        var second = Note(1, "-", 120, 130);
        Assert.Throws<NotSupportedException>(() => ContinuationPhonemes.Join([root, second with
            { Address = second.Address with { Position = 2161 } }]));
        Assert.Throws<NotSupportedException>(() => ContinuationPhonemes.Join([root, Note(1, "-", 121, 130)]));
        Assert.Throws<NotSupportedException>(() => ContinuationPhonemes.Join([root, Note(1, "-", 120, 120)]));
    }

    [Fact]
    public void ContinuationProofRequiresBothAdjacentNativeRowsAndTheExactPhone()
    {
        var before = new TimingBoundaryEvidence(3271, 3302, 3302, "u", null);
        var after = new TimingBoundaryEvidence(3302, 3324, 3324, "u", null);
        Assert.True(TimingBoundaryEvidence.MatchesContinuation(3302, "u", before, after));
        Assert.False(TimingBoundaryEvidence.MatchesContinuation(3302, "-", before, after));
        Assert.False(TimingBoundaryEvidence.MatchesContinuation(3302, "a", before, after));
        Assert.False(TimingBoundaryEvidence.MatchesContinuation(3302, "u", before, after with { Begin = 3303 }));
        Assert.False(TimingBoundaryEvidence.MatchesContinuation(3302, "u", before, after with { To = "Sil", Split = 3302 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyInternalContinuationPitchJunctionsMayMove(bool coincidesWithPhonemeBoundary)
    {
        TransitionSegment Row(int begin, int split, int end, string from, string to) =>
            new(begin, split, end, from, to, 1, 2, 0, false, "material", []);
        int split = coincidesWithPhonemeBoundary ? 120 : 110;
        TransitionSegment[] rows = [Row(95, 100, 105, "Sil", "m"), Row(105, split, 120, "m", "u"), Row(135, 140, 145, "u", "Sil")];
        var layout = new TransitionLayout(Guid.NewGuid(), 1, "input", rows, rows,
            [Note(0, "m u", 100, split, 120), Note(1, "-", 120, 140)],
            new[] { 0, 100, split, 120, 140, 200 }.Distinct().Select(frame => new TransitionAnchor(frame, frame)));
        var endpoint = new TransitionEndpointRef(layout.Edges[1].Identity, TransitionEndpoint.End);
        Assert.Equal(coincidesWithPhonemeBoundary, layout.Junction(endpoint).Protected);
        if (coincidesWithPhonemeBoundary)
        {
            Assert.Throws<NotSupportedException>(() => new TransitionControlPlan(layout.Fingerprint)
                .WithJunction(layout, endpoint, 126).Compile(layout));
            return;
        }
        var compiled = new TransitionControlPlan(layout.Fingerprint).WithJunction(layout, endpoint, 126).Compile(layout);
        Assert.Contains(new TransitionAnchor(120, 126), compiled.Anchors);
        Assert.Contains(new TransitionAnchor(110, 110), compiled.Anchors);
        Assert.Contains(new TransitionAnchor(140, 140), compiled.Anchors);
    }
}
