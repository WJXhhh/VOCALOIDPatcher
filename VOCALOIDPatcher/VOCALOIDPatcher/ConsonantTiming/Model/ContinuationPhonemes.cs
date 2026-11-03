using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

// A '-' note changes pitch/duration while continuing the preceding phoneme.
// Keep every native NOTE event and pitch anchor, but bind ART ownership to the
// original phoneme rather than inventing a '-' recording or archive identity.
internal static class ContinuationPhonemes
{
    internal static bool IsContinuation(string topology) => topology.Trim() == "-";

    internal static void ValidateBoundaryEdits(SampledTimingPlan plan, bool continuesAfter)
    {
        // Only the final '-' tail can be a real outgoing phoneme boundary.
        // Its start (and any intermediate tail) is still a pitch junction.
        if (IsContinuation(plan.Topology) && plan.Adjustments.Any(a =>
            a.Kind == TimingAdjustment.PhonemeBoundary && a.Value != 0 && (a.Index != 1 || continuesAfter)))
            throw new NotSupportedException("Continuation pitch boundaries are not independent phoneme edits.");
    }

    internal static int[] Owners(IReadOnlyList<TimingNoteAddress> notes)
    {
        var owners = new int[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            notes[i].Validate();
            owners[i] = i;
            if (!IsContinuation(notes[i].Topology)) continue;
            if (i == 0 || notes[i].Position - notes[i - 1].Position != notes[i - 1].Duration ||
                notes[i - 1].Bank != notes[i].Bank || notes[i - 1].Track != notes[i].Track ||
                notes[i - 1].Part != notes[i].Part || (long)notes[i - 1].Note + 1 != notes[i].Note)
                throw new NotSupportedException("A continuation note requires its contiguous preceding phoneme context.");
            owners[i] = owners[i - 1];
        }
        return owners;
    }

    internal static ImmutableArray<TransitionNoteFrames> Join(IEnumerable<TransitionNoteFrames> input)
    {
        var notes = input.Take(34).ToArray();
        if (notes.Length is < 1 or > 33) throw new NotSupportedException("Continuation context exceeds its bound.");
        var owners = Owners(notes.Select(n => n.Address).ToArray());
        var result = new List<TransitionNoteFrames>();
        var indices = new Dictionary<int, int>();
        for (int i = 0; i < notes.Length; i++)
        {
            if (owners[i] == i)
            {
                indices[i] = result.Count; result.Add(notes[i]); continue;
            }
            var frames = notes[i].Original;
            var previous = notes[i - 1].Original;
            if (frames.Length != 2 || previous.IsDefaultOrEmpty || frames[0] != previous[^1] || frames[1] <= frames[0])
                throw new NotSupportedException("Native continuation boundaries do not prove a shared phoneme interval.");
            int index = indices[owners[i]];
            var owner = result[index];
            if (owner.Original.IsDefaultOrEmpty) throw new NotSupportedException("Missing continuation owner boundaries.");
            result[index] = owner with { Original = owner.Original.SetItem(owner.Original.Length - 1, frames[^1]) };
        }
        return result.ToImmutableArray();
    }
}
