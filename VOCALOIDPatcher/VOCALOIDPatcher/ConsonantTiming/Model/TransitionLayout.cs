using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VOCALOIDPatcher.ConsonantTiming;

internal enum TransitionEndpoint { Begin, Split, End }
internal sealed record TransitionPhone(TimingNoteAddress Note, int Index, string Symbol);
internal sealed record TransitionIdentity(TransitionPhone? Left, TransitionPhone? Right,
    string From, string To, int Stage, bool Reverse, int Occurrence);
internal sealed record TransitionEndpointRef(TransitionIdentity Edge, TransitionEndpoint Endpoint);
internal sealed record TransitionFrame(int Frame, int SourceBits, byte Flag0, byte Flag1);
internal sealed record TransitionMaterialSpan(int First, int Last);
internal sealed record TransitionMaterialCoverage(TransitionMaterialSpan? Left, TransitionMaterialSpan? Right);
internal sealed record TransitionSegment(int Begin, int Split, int End, string From, string? To,
    int FromId, int ToId, int Stage, bool Reverse, string MaterialMetadata,
    ImmutableArray<TransitionFrame> Frames)
{
    internal TransitionMaterialCoverage? MaterialCoverage { get; init; }
}
internal sealed record TransitionNoteFrames(TimingNoteAddress Address, ImmutableArray<int> Original);
internal readonly record struct TransitionAnchor(int From, int To);
internal sealed record TransitionEdge(TransitionIdentity Identity, TransitionSegment Original, TransitionSegment Current);
internal sealed record TransitionJunction(int OriginalFrame, int CurrentFrame, bool Protected,
    ImmutableArray<TransitionEndpointRef> Aliases, int MinimumCandidate, int MaximumCandidate);
internal sealed record TransitionJunctionEdit(TransitionEndpointRef Endpoint, int Frame);
internal sealed record TransitionGainEdit(TransitionIdentity Edge, float Decibels);
internal sealed record TransitionOnsetEdit(TransitionIdentity Edge, int OffsetFrames);
internal sealed record TransitionPhaseEdit(TransitionIdentity Edge, float Amount);
[Flags]
internal enum TransitionSourceSides { None = 0, Left = 1, Right = 2, Both = Left | Right }
internal sealed record TransitionCoverageEdit(TransitionIdentity Edge, TransitionSourceSides Sides);

// Engine pointers, SDK objects and mutable arrays never cross this boundary.
// Identity names the phoneme occurrences on both sides, not just a part/stage.
internal sealed class TransitionLayout
{
    internal Guid Scope { get; }
    internal long Revision { get; }
    internal string Fingerprint { get; }
    internal ImmutableArray<TransitionEdge> Edges { get; }
    internal ImmutableArray<TransitionJunction> Junctions { get; }
    internal ImmutableArray<TransitionAnchor> Anchors { get; }
    internal ImmutableArray<TransitionNoteFrames> Notes { get; }
    internal int MinimumOutputFrame { get; }

    internal TransitionLayout(Guid scope, long revision, string inputKey,
        IEnumerable<TransitionSegment> original, IEnumerable<TransitionSegment> current,
        IEnumerable<TransitionNoteFrames> notes, IEnumerable<TransitionAnchor> anchors, int minimumOutputFrame = 0)
    {
        Scope = scope; Revision = revision; MinimumOutputFrame = minimumOutputFrame;
        Notes = notes.ToImmutableArray(); Anchors = anchors.ToImmutableArray();
        if (Notes.Length is < 1 or > 33 || Notes.Select(n => n.Address).Distinct().Count() != Notes.Length ||
            Notes.Any(n => n.Original.IsDefault || n.Original.Length != n.Address.Topology.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1 ||
                n.Original.Length < 2 || n.Original.Zip(n.Original.Skip(1)).Any(p => p.First > p.Second)))
            throw new NotSupportedException("Invalid or ambiguous transition note topology.");
        ValidateAnchors(Anchors);
        var before = original.Where(r => r.To != null).ToArray();
        var after = current.Where(r => r.To != null).ToArray();
        if (before.Length == 0 || before.Length != after.Length || before.Length > 1024)
            throw new NotSupportedException("Transition topology changed or is unavailable.");
        var phones = Notes.SelectMany(n => n.Address.Topology.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((symbol, i) => (Phone: new TransitionPhone(n.Address, i, symbol), Begin: n.Original[i], End: n.Original[i + 1]))).ToArray();
        TransitionPhone? Bind(TransitionSegment row, bool left)
        {
            string symbol = left ? row.From : row.To!;
            if (symbol == "Sil") return null;
            var candidates = phones.Where(p => p.Phone.Symbol == symbol && p.Begin <= row.Split && row.Split <= p.End).ToArray();
            var exact = candidates.Where(p => (left ? p.End : p.Begin) == row.Split).ToArray();
            if (exact.Length == 1) return exact[0].Phone;
            if (candidates.Length != 1)
                throw new NotSupportedException($"Transition occurrence is ambiguous: {row.From}/{row.To}@{row.Split} ({candidates.Length}).");
            return candidates[0].Phone;
        }
        var edges = new List<TransitionEdge>();
        for (int i = 0; i < before.Length; i++)
        {
            var a = before[i]; var b = after[i];
            if ((a.From, a.To, a.Stage, a.Reverse, a.MaterialMetadata) != (b.From, b.To, b.Stage, b.Reverse, b.MaterialMetadata) ||
                a.Begin > a.Split || a.Split > a.End || b.Begin > b.Split || b.Split > b.End)
                throw new NotSupportedException("Timing changed transition source/stage identity.");
            var identity = new TransitionIdentity(Bind(a, true), Bind(a, false), a.From, a.To!, a.Stage, a.Reverse, 0);
            identity = identity with { Occurrence = edges.Count(e => (e.Identity with { Occurrence = 0 }) == identity) };
            edges.Add(new(identity, a, b));
        }
        Edges = edges.ToImmutableArray();
        var points = Anchors.ToDictionary(p => p.From, p => (p.To, Protected: true));
        var aliases = new Dictionary<int, List<TransitionEndpointRef>>();
        foreach (var edge in Edges)
            foreach (var endpoint in Enum.GetValues<TransitionEndpoint>())
            {
                int from = Position(edge.Original, endpoint), to = Position(edge.Current, endpoint);
                if (points.TryGetValue(from, out var old) && old.To != to)
                    throw new NotSupportedException("Shared transition endpoint has inconsistent native coordinates.");
                bool protect = endpoint == TransitionEndpoint.Split || (points.TryGetValue(from, out old) && old.Protected);
                points[from] = (to, protect);
                if (!aliases.TryGetValue(from, out var list)) aliases[from] = list = new();
                list.Add(new(edge.Identity, endpoint));
            }
        var ordered = points.OrderBy(p => p.Key).ToArray();
        ValidateAnchors(ordered.Select(p => new TransitionAnchor(p.Key, p.Value.To)));
        Junctions = ordered.Select((p, i) => new TransitionJunction(p.Key, p.Value.To, p.Value.Protected,
            aliases.TryGetValue(p.Key, out var list) ? list.ToImmutableArray() : [],
            p.Value.Protected || i == 0 ? p.Value.To : Math.Max(minimumOutputFrame, ordered[i - 1].Value.To + 1),
            p.Value.Protected || i == ordered.Length - 1 ? p.Value.To : ordered[i + 1].Value.To - 1)).ToImmutableArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(inputKey); writer.Write(revision); writer.Write(minimumOutputFrame);
            foreach (var note in Notes) { writer.Write(note.Address.ToString()); foreach (int f in note.Original) writer.Write(f); }
            foreach (var edge in Edges)
            {
                writer.Write(edge.Identity.ToString()); writer.Write(edge.Original.MaterialMetadata);
                foreach (var row in new[] { edge.Original, edge.Current })
                {
                    writer.Write(row.Begin); writer.Write(row.Split); writer.Write(row.End);
                    writer.Write(row.MaterialCoverage != null);
                    if (row.MaterialCoverage is { } coverage)
                        foreach (var span in new[] { coverage.Left, coverage.Right })
                        { writer.Write(span != null); if (span != null) { writer.Write(span.First); writer.Write(span.Last); } }
                    foreach (var f in row.Frames) { writer.Write(f.Frame); writer.Write(f.SourceBits); writer.Write(f.Flag0); writer.Write(f.Flag1); }
                }
            }
            foreach (var a in Anchors) { writer.Write(a.From); writer.Write(a.To); }
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal TransitionJunction Junction(TransitionEndpointRef endpoint) => Junctions.SingleOrDefault(j => j.Aliases.Contains(endpoint))
        ?? throw new InvalidDataException("Transition endpoint does not belong to this layout.");
    internal TransitionJunction ConditionalJunction(TransitionEndpointRef endpoint, TransitionControlPlan controls)
        => ConditionalJunctions(controls).Single(j => j.Aliases.Contains(endpoint));
    internal ImmutableArray<TransitionJunction> ConditionalJunctions(TransitionControlPlan controls)
    {
        var compiled = controls.Compile(this);
        var coordinates = compiled.Anchors.ToDictionary(a => a.From, a => a.To);
        int At(int i) => coordinates.GetValueOrDefault(Junctions[i].OriginalFrame, Junctions[i].CurrentFrame);
        return Junctions.Select((joint, index) => joint with { CurrentFrame = At(index),
            MinimumCandidate = joint.Protected || index == 0 ? At(index) : Math.Max(MinimumOutputFrame, At(index - 1) + 1),
            MaximumCandidate = joint.Protected || index == Junctions.Length - 1 ? At(index) : At(index + 1) - 1 }).ToImmutableArray();
    }
    internal static int Position(TransitionSegment row, TransitionEndpoint endpoint) => endpoint switch
    { TransitionEndpoint.Begin => row.Begin, TransitionEndpoint.Split => row.Split, TransitionEndpoint.End => row.End, _ => throw new ArgumentOutOfRangeException(nameof(endpoint)) };
    internal static void ValidateAnchors(IEnumerable<TransitionAnchor> points)
    {
        var array = points.ToArray();
        if (array.Length is < 2 or > 8192 || array[0].From != array[0].To || array[^1].From != array[^1].To ||
            array.Zip(array.Skip(1)).Any(p => p.First.From >= p.Second.From || p.First.To >= p.Second.To))
            throw new NotSupportedException("Transition anchors cross, collapse or lack fixed guards.");
    }
}

internal sealed class TransitionControlPlan
{
    internal string LayoutFingerprint { get; }
    internal ImmutableArray<TransitionJunctionEdit> Junctions { get; }
    internal ImmutableArray<TransitionGainEdit> Gains { get; }
    internal ImmutableArray<TransitionOnsetEdit> Onsets { get; }
    internal ImmutableArray<TransitionPhaseEdit> Phases { get; }
    internal ImmutableArray<TransitionCoverageEdit> Coverages { get; }
    internal TransitionControlPlan(string layoutFingerprint, IEnumerable<TransitionJunctionEdit>? junctions = null,
        IEnumerable<TransitionGainEdit>? gains = null, IEnumerable<TransitionOnsetEdit>? onsets = null,
        IEnumerable<TransitionPhaseEdit>? phases = null, IEnumerable<TransitionCoverageEdit>? coverages = null)
    {
        LayoutFingerprint = layoutFingerprint;
        Junctions = (junctions ?? []).Take(1025).ToImmutableArray(); Gains = (gains ?? []).Take(1025).ToImmutableArray();
        Onsets = (onsets ?? []).Take(1025).ToImmutableArray();
        Phases = (phases ?? []).Take(1025).ToImmutableArray();
        Coverages = (coverages ?? []).Take(1025).ToImmutableArray();
        if (string.IsNullOrWhiteSpace(layoutFingerprint) || Junctions.Length > 1024 || Gains.Length > 1024 ||
            Onsets.Length > 1024 || Onsets.Any(o => o.OffsetFrames is < -4096 or > 4096) ||
            Onsets.Select(o => o.Edge).Distinct().Count() != Onsets.Length ||
            Phases.Length > 1024 || Phases.Any(p => !float.IsFinite(p.Amount) || p.Amount is < -1 or > 1) ||
            Phases.Select(p => p.Edge).Distinct().Count() != Phases.Length ||
            Coverages.Length > 1024 || Coverages.Any(c => c.Sides is not (TransitionSourceSides.Left or TransitionSourceSides.Right or TransitionSourceSides.Both)) ||
            Coverages.Select(c => c.Edge).Distinct().Count() != Coverages.Length ||
            Gains.Any(g => !float.IsFinite(g.Decibels) || g.Decibels is < -60 or > 12))
            throw new InvalidDataException("Invalid transition controls.");
    }

    internal TransitionCompiled Compile(TransitionLayout layout)
    {
        if (LayoutFingerprint != layout.Fingerprint) throw new InvalidOperationException("Transition layout is stale.");
        var targets = new Dictionary<int, int>();
        foreach (var coverage in Coverages)
            if (!layout.Edges.Any(e => e.Identity == coverage.Edge)) throw new InvalidDataException("Foreign transition coverage identity.");
        foreach (var phase in Phases)
            if (!layout.Edges.Any(e => e.Identity == phase.Edge)) throw new InvalidDataException("Foreign transition phase identity.");
        foreach (var onset in Onsets)
            if (onset.OffsetFrames > 0 || !layout.Edges.Any(e => e.Identity == onset.Edge) || onset.Edge.From != "Sil" || onset.Edge.Right == null)
                throw new NotSupportedException("Source progress requires an identified incoming onset.");
        foreach (var edit in Junctions)
        {
            var joint = layout.Junction(edit.Endpoint);
            if ((!joint.Protected && edit.Frame < layout.MinimumOutputFrame) || (joint.Protected && edit.Frame != joint.CurrentFrame))
                throw new NotSupportedException("Requested transition endpoint is outside its native geometry domain.");
            if (targets.TryGetValue(joint.OriginalFrame, out int old) && old != edit.Frame)
                throw new NotSupportedException("Conflicting controls at a shared transition junction.");
            targets[joint.OriginalFrame] = edit.Frame;
        }
        var gains = new Dictionary<TransitionIdentity, float>();
        foreach (var gain in Gains)
        {
            if (!layout.Edges.Any(e => e.Identity == gain.Edge) || !gains.TryAdd(gain.Edge, gain.Decibels == 0 ? 0 : gain.Decibels))
                throw new InvalidDataException("Duplicate or foreign transition gain identity.");
        }
        var anchors = targets.All(p => layout.Junctions.Single(j => j.OriginalFrame == p.Key).CurrentFrame == p.Value)
            ? layout.Anchors : layout.Junctions.Select(j => new TransitionAnchor(j.OriginalFrame,
                targets.TryGetValue(j.OriginalFrame, out int frame) ? frame : j.CurrentFrame)).ToImmutableArray();
        TransitionLayout.ValidateAnchors(anchors);
        var orderedGains = layout.Edges.Where(e => gains.TryGetValue(e.Identity, out float value) && value != 0)
            .Select(e => new TransitionGainEdit(e.Identity, gains[e.Identity])).ToImmutableArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(layout.Fingerprint);
            foreach (var a in anchors) { writer.Write(a.From); writer.Write(a.To); }
            foreach (var g in orderedGains) { writer.Write(g.Edge.ToString()); writer.Write(g.Decibels); }
            foreach (var o in Onsets.Where(o => o.OffsetFrames != 0).OrderBy(o => layout.Edges.IndexOf(layout.Edges.Single(e => e.Identity == o.Edge))))
            { writer.Write("onset.source-progress.v1"); writer.Write(o.Edge.ToString()); writer.Write(o.OffsetFrames); }
            foreach (var p in Phases.Where(p => p.Amount != 0).OrderBy(p => layout.Edges.IndexOf(layout.Edges.Single(e => e.Identity == p.Edge))))
            { writer.Write("transition.fixed-phase.v1"); writer.Write(p.Edge.ToString()); writer.Write(p.Amount); }
            foreach (var c in Coverages.OrderBy(c => layout.Edges.IndexOf(layout.Edges.Single(e => e.Identity == c.Edge))))
            { writer.Write("transition.reference-coverage.v1"); writer.Write(c.Edge.ToString()); writer.Write((int)c.Sides); }
        }
        return new(anchors, orderedGains, Convert.ToHexString(SHA256.HashData(stream.ToArray())))
        { Onsets = Onsets.Where(o => o.OffsetFrames != 0).ToImmutableArray(), Phases = Phases.Where(p => p.Amount != 0).ToImmutableArray(), Coverages = Coverages };
    }

    internal TransitionControlPlan WithJunction(TransitionLayout layout, TransitionEndpointRef endpoint, int frame)
    {
        var joint = layout.Junction(endpoint);
        return new(LayoutFingerprint, Junctions.Where(e => layout.Junction(e.Endpoint).OriginalFrame != joint.OriginalFrame)
            .Concat(frame == joint.CurrentFrame ? [] : new[] { new TransitionJunctionEdit(endpoint, frame) }), Gains, Onsets, Phases, Coverages);
    }
    internal TransitionControlPlan WithOnset(TransitionIdentity edge, int offset)
        => new(LayoutFingerprint, Junctions, Gains, Onsets.Where(o => o.Edge != edge).Concat(offset == 0 ? [] : new[] { new TransitionOnsetEdit(edge, offset) }), Phases, Coverages);
    internal TransitionControlPlan WithPhase(TransitionIdentity edge, float amount)
        => new(LayoutFingerprint, Junctions, Gains, Onsets, Phases.Where(p => p.Edge != edge)
            .Concat(amount == 0 ? [] : new[] { new TransitionPhaseEdit(edge, amount) }), Coverages);
    internal TransitionControlPlan WithCoverage(TransitionIdentity edge, TransitionSourceSides sides)
        => new(LayoutFingerprint, Junctions, Gains, Onsets, Phases, Coverages.Where(c => c.Edge != edge)
            .Concat(sides == TransitionSourceSides.None ? [] : new[] { new TransitionCoverageEdit(edge, sides) }));
    internal static TransitionControlPlan FromCompiled(TransitionLayout layout, TransitionCompiled compiled)
    {
        var points = compiled.Anchors.ToDictionary(a => a.From, a => a.To);
        var edits = new List<TransitionJunctionEdit>();
        foreach (var joint in layout.Junctions)
            if (points.TryGetValue(joint.OriginalFrame, out int to) && to != joint.CurrentFrame)
            {
                if (joint.Aliases.Length == 0) throw new InvalidDataException("Compiled controls moved a context guard.");
                edits.Add(new(joint.Aliases[0], to));
            }
        return new(layout.Fingerprint, edits, compiled.Gains, compiled.Onsets, compiled.Phases, compiled.Coverages);
    }
}
internal sealed record TransitionCompiled(ImmutableArray<TransitionAnchor> Anchors,
    ImmutableArray<TransitionGainEdit> Gains, string Fingerprint)
{
    internal ImmutableArray<TransitionOnsetEdit> Onsets { get; init; } = [];
    internal ImmutableArray<TransitionPhaseEdit> Phases { get; init; } = [];
    internal ImmutableArray<TransitionCoverageEdit> Coverages { get; init; } = [];
}
internal sealed record TransitionReachableRange(TransitionJunction Junction, ImmutableArray<int> ReachableFrames,
    int NativePlans, bool CacheHit, double Milliseconds)
{
    internal int Minimum => ReachableFrames.Min();
    internal int Maximum => ReachableFrames.Max();
    internal bool IsContiguous => ReachableFrames.Length == Maximum - Minimum + 1;
}
internal sealed record TransitionExecutionPlan(TransitionLayout Layout, TransitionCompiled Controls,
    ImmutableArray<TransitionSegment> Segments, int NativePlans, bool CacheHit, double Milliseconds)
{
    internal TransitionControlPlan? RequestedControls { get; init; }
    internal ImmutableArray<TransitionAnchor> RenderAnchors { get; init; } = Controls.Anchors;
    internal ImmutableArray<TransitionSourceProgress> SourceProgress { get; init; } = [];
}
internal sealed record TransitionSourceProgress(TransitionIdentity Edge, ImmutableArray<TransitionFrame> Frames);
