using System;
using System.Collections.Generic;
using System.Linq;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Explicit document adapter, not registered with the live Editor yet.
internal static class SampledTimingVsmBinding
{
    internal static TimingNoteAddress Address(WIVSMNote note)
    {
        var part = note.Parent ?? throw new InvalidOperationException("Missing Part.");
        var sequence = note.Sequence ?? throw new InvalidOperationException("Missing sequence.");
        for (ulong t = 0; t < sequence.NumTrack; t++)
            if (sequence.GetTrack(t) is WIVSMMidiTrack track)
                for (ulong p = 0; p < track.NumParts; p++)
                    if (track.GetPart(p) is WIVSMMidiPart candidate && (nint)candidate == (nint)part)
                        for (ulong n = 0; n < candidate.NumNotes; n++)
                            if (candidate.GetNote(n) is { } current && (nint)current == (nint)note)
                                return new(checked((int)t), checked((int)p), checked((int)n), note.RelPosTick.Value,
                                    checked((int)note.DurationTick.Value), note.NoteNumber, part.VoiceBankID, note.Phonemes);
        throw new InvalidOperationException("Timing note is detached.");
    }

    internal static IReadOnlyDictionary<nint, float> Resolve(WIVSMSequence sequence, SampledTimingDocument document)
    {
        var result = new Dictionary<nint, float>();
        foreach (var pair in ResolvePlans(sequence,document))
            if (pair.Value.PhonemeCoefficient(pair.Value.Topology) is { } coefficient) result.Add(pair.Key,coefficient);
        return result;
    }

    internal static IReadOnlyDictionary<nint, SampledTimingPlan> ResolvePlans(WIVSMSequence sequence, SampledTimingDocument document)
        => ResolvePlans(sequence,document,true);

    // Retain detached plans for undo/save, but never apply them to another
    // note or prevent unrelated native edits from rendering.
    internal static IReadOnlyDictionary<nint, SampledTimingPlan> ResolveActivePlans(WIVSMSequence sequence, SampledTimingDocument document)
        => ResolvePlans(sequence,document,false);

    private static IReadOnlyDictionary<nint, SampledTimingPlan> ResolvePlans(WIVSMSequence sequence, SampledTimingDocument document,bool strict)
    {
        var result = new Dictionary<nint, SampledTimingPlan>();
        foreach (var pair in document.Snapshot())
        {
            var a = pair.Key;
            var track = (ulong)a.Track < sequence.NumTrack ? sequence.GetTrack((ulong)a.Track) as WIVSMMidiTrack : null;
            var part = track != null && (ulong)a.Part < track.NumParts ? track.GetPart((ulong)a.Part) as WIVSMMidiPart : null;
            var note = part != null && (ulong)a.Note < part.NumNotes ? part.GetNote((ulong)a.Note) : null;
            if (note == null || part == null || note.RelPosTick.Value != a.Position || note.DurationTick.Value != a.Duration ||
                note.NoteNumber != a.Pitch || part.VoiceBankID != a.Bank || note.Phonemes != a.Topology)
            {
                if (strict) throw new NotSupportedException("Stored timing note no longer matches the project.");
                continue;
            }
            try { _ = pair.Value.PhonemeCoefficient(note.Phonemes); }
            catch (NotSupportedException) when (!strict) { continue; }
            result.Add((nint)note,pair.Value);
        }
        return result;
    }

    // Native cache invalidation for a real edit, never for candidate queries.
    // No temporary value is committed, so no native undo entry is introduced.
    // The caller must schedule one normal render afterwards, under its epoch.
    internal static void InvalidateRenderedPart(WIVSMMidiPart part, IEnumerable<WIVSMNote> affectedNotes)
    {
        var sequence = part.Sequence ?? throw new InvalidOperationException("Missing sequence.");
        if (sequence.IsStaged)
            throw new InvalidOperationException("Cannot invalidate a staged source.");
        var values = affectedNotes.Take(34).Select(n => (Note: n, Velocity: n.NoteVelocity)).ToArray();
        if (values.Length is < 1 or > 33 || values.Any(v => (nint)v.Note.Parent! != (nint)part) ||
            values.Select(v => (nint)v.Note).Distinct().Count() != values.Length)
            throw new ArgumentException("Cache invalidation needs one bounded source context.");
        try
        {
            foreach (var value in values)
            {
                int temporary = value.Velocity < 127 ? value.Velocity + 1 : value.Velocity - 1;
                value.Note.NoteVelocity = temporary;
                if (value.Note.NoteVelocity != temporary) throw new InvalidOperationException("Cannot invalidate native note cache.");
            }
            foreach (var value in values) value.Note.NoteVelocity = value.Velocity;
            if (sequence.IsStaged && !sequence.Commit(false)) throw new InvalidOperationException("Cannot commit timing invalidation.");
        }
        finally
        {
            foreach (var value in values)
                if (value.Note.NoteVelocity != value.Velocity) value.Note.NoteVelocity = value.Velocity;
        }
    }
}
