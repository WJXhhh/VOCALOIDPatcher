using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static class TransitionProjectBinding
{
    internal const double FramesPerSecond = 44100d / 256;
    internal static bool InPart(TransitionProjectData data, TransitionProjectEdge edge, int track, int part)
        => TransitionProjectData.NoteIds(edge).Any(id => data.Notes.Any(n => n.Id == id && n.Address.Track == track && n.Address.Part == part));
    internal static bool HasPart(TransitionProjectData data, int track, int part)
        => data.Gains.Any(g => InPart(data, g.Edge, track, part)) || data.Junctions.Any(j => j.Aliases.Any(a => InPart(data, a.Edge, track, part))) ||
            data.Onsets.Any(o => InPart(data, o.Edge, track, part)) || data.Phases.Any(p => InPart(data, p.Edge, track, part)) || data.Coverages.Any(c => InPart(data, c.Edge, track, part));

    internal static IReadOnlyDictionary<string, TransitionEdge> MatchEdges(TransitionProjectData data, TransitionLayout layout)
    {
        var ids = data.Notes.ToDictionary(n => n.Address, n => n.Id);
        var result = new Dictionary<string, TransitionEdge>(StringComparer.Ordinal);
        foreach (var edge in layout.Edges)
        {
            var e = edge.Identity;
            if ((e.Left != null && !ids.ContainsKey(e.Left.Note)) || (e.Right != null && !ids.ContainsKey(e.Right.Note))) continue;
            TransitionProjectPhone? Phone(TransitionPhone? p) => p == null ? null : new(ids[p.Note], p.Index, p.Symbol);
            var key = TransitionProjectData.EdgeKey(new(Phone(e.Left), Phone(e.Right), e.From, e.To, e.Stage, e.Reverse, e.Occurrence));
            if (!result.TryAdd(key, edge)) throw new NotSupportedException("Transition identity is ambiguous.");
        }
        return result;
    }

    internal static TransitionControlPlan Resolve(TransitionProjectData data, TransitionLayout layout)
        => Resolve(data, layout, availableOnly: false);

    // Native edits can replace the context even when a controlled note keeps
    // its own phonemes (e.g. a new neighbour replaces Sil -> vowel). Resolve
    // only complete surviving identities; retain the archive for native Undo.
    // This is target lifetime reconciliation, never a retry of failed timing
    // planning: malformed data, ambiguous identities and execution failures
    // still reject publication.
    internal static TransitionControlPlan ResolveAvailable(TransitionProjectData data, TransitionLayout layout)
        => Resolve(data, layout, availableOnly: true);

    private static TransitionControlPlan Resolve(TransitionProjectData data, TransitionLayout layout, bool availableOnly)
    {
        data.Validate();
        var first = layout.Notes[0].Address;
        var edges = MatchEdges(data, layout);
        TransitionIdentity? Edge(TransitionProjectEdge stored)
        {
            if (edges.TryGetValue(TransitionProjectData.EdgeKey(stored), out var edge)) return edge.Identity;
            if (availableOnly) return null;
            throw new NotSupportedException("Stored transition identity is absent or ambiguous.");
        }
        var joints = new List<TransitionJunctionEdit>();
        foreach (var saved in data.Junctions.Where(j => j.Aliases.Any(a => InPart(data, a.Edge, first.Track, first.Part))))
        {
            var resolved = saved.Aliases.Select(a => (Edge: Edge(a.Edge), a.Endpoint)).ToArray();
            if (resolved.Any(a => a.Edge == null)) continue;
            var aliases = resolved.Select(a => new TransitionEndpointRef(a.Edge!, a.Endpoint)).ToArray();
            var actual = layout.Junction(aliases[0]);
            if (aliases.Any(a => layout.Junction(a).OriginalFrame != actual.OriginalFrame) ||
                !actual.Aliases.ToHashSet().SetEquals(aliases))
            {
                if (availableOnly) continue;
                throw new NotSupportedException("Stored shared transition junction changed its complete alias set.");
            }
            int frame = checked(actual.CurrentFrame + TransitionTimingScale.Offset(data, layout, saved.OffsetSeconds, saved.TimingScale));
            joints.Add(new(aliases[0], frame));
        }
        var gains = data.Gains.Where(g => InPart(data, g.Edge, first.Track, first.Part)).Select(g => (Edge: Edge(g.Edge), g.Decibels))
            .Where(g => g.Edge != null).Select(g => new TransitionGainEdit(g.Edge!, g.Decibels));
        var onsets = data.Onsets.Where(o => InPart(data, o.Edge, first.Track, first.Part)).Select(o => (Edge: Edge(o.Edge), o.OffsetSeconds, o.TimingScale))
            .Where(o => o.Edge != null).Select(o => new TransitionOnsetEdit(o.Edge!,
                TransitionTimingScale.Offset(data, layout, o.OffsetSeconds, o.TimingScale)));
        var phases = data.Phases.Where(p => InPart(data, p.Edge, first.Track, first.Part)).Select(p => (Edge: Edge(p.Edge), p.Amount))
            .Where(p => p.Edge != null).Select(p => new TransitionPhaseEdit(p.Edge!, p.Amount));
        var coverages = data.Coverages.Where(c => InPart(data, c.Edge, first.Track, first.Part)).Select(c => (Edge: Edge(c.Edge), c.Sides))
            .Where(c => c.Edge != null).Select(c => new TransitionCoverageEdit(c.Edge!, c.Sides));
        var plan = new TransitionControlPlan(layout.Fingerprint, joints, gains, onsets, phases, coverages);
        _ = plan.Compile(layout); return plan;
    }

    internal static TransitionProjectData Encode(TransitionProjectData data, TransitionLayout layout, TransitionControlPlan controls,
        IReadOnlyDictionary<TimingNoteAddress, Guid>? authorizedIds = null)
    {
        data.Validate(); var compiled = controls.Compile(layout);
        var first = layout.Notes[0].Address;
        var allNotes = data.Notes.ToDictionary(n => n.Address);
        foreach (var n in layout.Notes)
        {
            if (authorizedIds != null)
            {
                if (!authorizedIds.TryGetValue(n.Address, out var id) || id == Guid.Empty) throw new InvalidOperationException("Missing authorized transition note ID.");
                var old = allNotes.GetValueOrDefault(n.Address);
                allNotes[n.Address] = new(id, n.Address) { Extra = old?.Id == id ? old.Extra : null };
            }
            else allNotes.TryAdd(n.Address, new(Guid.NewGuid(), n.Address));
        }
        TransitionProjectPhone? Phone(TransitionPhone? phone) => phone == null ? null : new(allNotes[phone.Note].Id, phone.Index, phone.Symbol);
        TransitionProjectEdge Edge(TransitionIdentity edge) => new(Phone(edge.Left), Phone(edge.Right), edge.From, edge.To, edge.Stage, edge.Reverse, edge.Occurrence);
        var referenceData = data with { Notes = allNotes.Values.ToImmutableArray() };
        var oldEdges = data.Junctions.SelectMany(j => j.Aliases).Select(a => a.Edge).Concat(data.Gains.Select(g => g.Edge)).Concat(data.Onsets.Select(o => o.Edge))
            .Concat(data.Phases.Select(p => p.Edge)).Concat(data.Coverages.Select(c => c.Edge))
            .GroupBy(TransitionProjectData.EdgeKey).ToDictionary(g => g.Key, g => g.First());
        TransitionProjectEdge PreservedEdge(TransitionIdentity edge)
        {
            var value = Edge(edge); return oldEdges.GetValueOrDefault(TransitionProjectData.EdgeKey(value)) ?? value;
        }
        var joints = data.Junctions.Where(j => !j.Aliases.Any(a => InPart(data, a.Edge, first.Track, first.Part))).ToList();
        foreach (var group in controls.Junctions.GroupBy(e => layout.Junction(e.Endpoint).OriginalFrame))
        {
            var joint = layout.Junction(group.First().Endpoint); int frame = group.First().Frame;
            var aliases = joint.Aliases.Select(a => new TransitionProjectEndpoint(PreservedEdge(a.Edge), a.Endpoint))
                .OrderBy(TransitionProjectData.EndpointKey, StringComparer.Ordinal).ToImmutableArray();
            var old = data.Junctions.FirstOrDefault(j => j.Aliases.Select(TransitionProjectData.EndpointKey).ToHashSet()
                .SetEquals(aliases.Select(TransitionProjectData.EndpointKey)));
            if (old != null && TransitionTimingScale.Offset(referenceData, layout, old.OffsetSeconds, old.TimingScale) == frame - joint.CurrentFrame)
                joints.Add(old);
            else if (frame != joint.CurrentFrame)
                joints.Add(new(aliases, (frame - (double)joint.CurrentFrame) / FramesPerSecond)
                { Extra = old?.Extra, TimingScale = TransitionTimingScale.Create(referenceData, layout, aliases) });
        }
        var gains = data.Gains.Where(g => !InPart(data, g.Edge, first.Track, first.Part)).ToList();
        foreach (var gain in compiled.Gains)
        {
            var edge = PreservedEdge(gain.Edge);
            gains.Add(new(edge, gain.Decibels) { Extra = data.Gains.FirstOrDefault(g => TransitionProjectData.EdgeKey(g.Edge) == TransitionProjectData.EdgeKey(edge))?.Extra });
        }
        var onsets = data.Onsets.Where(o => !InPart(data, o.Edge, first.Track, first.Part)).ToList();
        foreach (var onset in controls.Onsets)
        {
            var edge = PreservedEdge(onset.Edge);
            var old = data.Onsets.FirstOrDefault(o => TransitionProjectData.EdgeKey(o.Edge) == TransitionProjectData.EdgeKey(edge));
            if (old != null && TransitionTimingScale.Offset(referenceData, layout, old.OffsetSeconds, old.TimingScale) == onset.OffsetFrames)
                onsets.Add(old);
            else if (onset.OffsetFrames != 0)
                onsets.Add(new(edge, onset.OffsetFrames / FramesPerSecond)
                { Extra = old?.Extra, TimingScale = TransitionTimingScale.Create(referenceData, layout, [new(edge, TransitionEndpoint.Begin)]) });
        }
        var phases = data.Phases.Where(p => !InPart(data, p.Edge, first.Track, first.Part)).ToList();
        foreach (var phase in compiled.Phases)
        {
            var edge = PreservedEdge(phase.Edge);
            phases.Add(new(edge, phase.Amount) { Extra = data.Phases.FirstOrDefault(p =>
                TransitionProjectData.EdgeKey(p.Edge) == TransitionProjectData.EdgeKey(edge))?.Extra });
        }
        var coverages = data.Coverages.Where(c => !InPart(data, c.Edge, first.Track, first.Part)).ToList();
        foreach (var coverage in compiled.Coverages)
        {
            var edge = PreservedEdge(coverage.Edge);
            coverages.Add(new(edge, coverage.Sides) { Extra = data.Coverages.FirstOrDefault(c =>
                TransitionProjectData.EdgeKey(c.Edge) == TransitionProjectData.EdgeKey(edge))?.Extra });
        }
        var result = data with
        {
            Notes = allNotes.Values.OrderBy(n => n.Id).ToImmutableArray(),
            Junctions = joints.OrderBy(j => TransitionProjectData.EndpointKey(j.Aliases[0]), StringComparer.Ordinal).ToImmutableArray(),
            Gains = gains.OrderBy(g => TransitionProjectData.EdgeKey(g.Edge), StringComparer.Ordinal).ToImmutableArray(),
            Onsets = onsets.OrderBy(o => TransitionProjectData.EdgeKey(o.Edge), StringComparer.Ordinal).ToImmutableArray(),
            Phases = phases.OrderBy(p => TransitionProjectData.EdgeKey(p.Edge), StringComparer.Ordinal).ToImmutableArray(),
            Coverages = coverages.OrderBy(c => TransitionProjectData.EdgeKey(c.Edge), StringComparer.Ordinal).ToImmutableArray()
        };
        return result.KeepNotes(_ => true).Validate();
    }
}
