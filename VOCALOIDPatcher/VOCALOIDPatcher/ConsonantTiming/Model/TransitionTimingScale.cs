using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

// Preserve the user's reference displacement, rather than repeatedly rounding
// and rewriting it when phoneme timing changes. Only ART extensions scale;
// dimensionless source phase and dB retain their independent semantics.
internal static class TransitionTimingScale
{
    internal static int Offset(TransitionProjectData data, TransitionLayout layout, double seconds,
        TransitionProjectTimingScale? scale)
        => checked((int)Math.Round(seconds * TransitionProjectBinding.FramesPerSecond *
            (scale == null ? 1 : Width(data, layout, scale.Phone) / (double)scale.Frames), MidpointRounding.AwayFromZero));

    private static int Width(TransitionProjectData data, TransitionLayout layout, TransitionProjectPhone phone)
    {
        var address = data.Notes.Single(n => n.Id == phone.Note).Address;
        var note = layout.Notes.Single(n => n.Address == address);
        int At(int frame) => layout.Anchors.Single(a => a.From == frame).To;
        int width = At(note.Original[phone.Index + 1]) - At(note.Original[phone.Index]);
        if (width <= 0) throw new NotSupportedException("Transition reference phoneme has no positive timing interval.");
        return width;
    }

    internal static TransitionProjectTimingScale Create(TransitionProjectData data, TransitionLayout layout,
        IEnumerable<TransitionProjectEndpoint> aliases)
    {
        var edges = TransitionProjectBinding.MatchEdges(data, layout);
        var endpoints = aliases.ToArray();
        int frame = layout.Junction(new(edges[TransitionProjectData.EdgeKey(endpoints[0].Edge)].Identity, endpoints[0].Endpoint)).OriginalFrame;
        var candidates = endpoints.SelectMany(a => new[] { a.Edge.Left, a.Edge.Right }).Where(p => p != null)
            .Cast<TransitionProjectPhone>().Distinct().Select(p =>
            {
                var note = layout.Notes.Single(n => n.Address == data.Notes.Single(s => s.Id == p.Note).Address);
                int distance = Math.Max(note.Original[p.Index] - frame, Math.Max(0, frame - note.Original[p.Index + 1]));
                return (Phone: p, Distance: distance);
            }).OrderBy(p => p.Distance).ThenBy(p => p.Phone.Index).ThenBy(p => p.Phone.Note).ToArray();
        if (candidates.Length == 0) throw new NotSupportedException("Transition reference has no phoneme owner.");
        var phone = candidates[0].Phone;
        return new(phone, Width(data, layout, phone));
    }

    internal static TransitionProjectData Adopt(TransitionProjectData data, TransitionProjectData active, TransitionLayout layout)
    {
        // Only complete surviving aliases may acquire a reference. Dormant
        // pronunciation/deletion targets remain byte-for-byte undo data.
        var edges = TransitionProjectBinding.MatchEdges(active, layout);
        bool Available(TransitionProjectEdge edge) => edges.ContainsKey(TransitionProjectData.EdgeKey(edge));
        bool Complete(TransitionProjectJunction junction)
        {
            if (!junction.Aliases.All(a => Available(a.Edge))) return false;
            var aliases = junction.Aliases.Select(a => new TransitionEndpointRef(edges[TransitionProjectData.EdgeKey(a.Edge)].Identity, a.Endpoint)).ToArray();
            var actual = layout.Junction(aliases[0]);
            return aliases.All(a => layout.Junction(a).OriginalFrame == actual.OriginalFrame) && actual.Aliases.ToHashSet().SetEquals(aliases);
        }
        return data with
        {
            Junctions = data.Junctions.Select(j => j.TimingScale != null || !Complete(j) ? j :
                j with { TimingScale = Create(active, layout, j.Aliases) }).ToImmutableArray(),
            Onsets = data.Onsets.Select(o => o.TimingScale != null || !Available(o.Edge) ? o :
                o with { TimingScale = Create(active, layout, [new(o.Edge, TransitionEndpoint.Begin)]) }).ToImmutableArray()
        };
    }
}
