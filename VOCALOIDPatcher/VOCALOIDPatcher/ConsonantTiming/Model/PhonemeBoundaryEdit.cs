using System;
using System.Collections.Generic;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

// Aliases come from exact native baseline frames, never from musical note
// adjacency or rounded screen coordinates. All aliases change in one edit.
internal static class PhonemeBoundaryEdit
{
    internal sealed record Note(TimingNoteAddress Address, int[] Frames, SampledTimingPlan? Plan);

    internal static SampledTimingPlan WithBoundary(SampledTimingPlan plan, int index, float? seconds)
    {
        var values = plan.Adjustments.Where(a => a.Kind != TimingAdjustment.PhonemeBoundary || a.Index != index).ToList();
        if (seconds is { } value && Math.Abs(value) > 1e-7f) values.Add(new(TimingAdjustment.PhonemeBoundary, index, value));
        return new(plan.Topology, values);
    }

    internal static IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?> Resolve(
        IReadOnlyList<Note> notes, int target, int index, SampledTimingPlan candidate)
    {
        if (target < 0 || target >= notes.Count || index < 0 || index >= notes[target].Frames.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        var selected = notes[target];
        _ = candidate.PhonemeCoefficient(selected.Address.Topology);
        int frame = selected.Frames[index];
        float? seconds = candidate.Adjustments.SingleOrDefault(a => a.Kind == TimingAdjustment.PhonemeBoundary && a.Index == index)?.Value;
        var result = new Dictionary<TimingNoteAddress, SampledTimingPlan?>();
        for (int n = 0; n < notes.Count; n++)
        {
            var note = notes[n];
            if (note.Address.Track != selected.Address.Track || note.Address.Part != selected.Address.Part)
                throw new ArgumentException("Boundary aliases must belong to one Part.");
            if (note.Frames.Length != note.Address.Topology.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1)
                throw new ArgumentException("Boundary alias topology changed.");
            var plan = n == target ? candidate : note.Plan ?? new(note.Address.Topology, []);
            bool touched = n == target;
            for (int i = 0; i < note.Frames.Length; i++)
                if (note.Frames[i] == frame) { plan = WithBoundary(plan, i, seconds); touched = true; }
            if (touched) result.Add(note.Address, plan.Adjustments.Count == 0 ? null : plan);
        }
        return result;
    }
}
