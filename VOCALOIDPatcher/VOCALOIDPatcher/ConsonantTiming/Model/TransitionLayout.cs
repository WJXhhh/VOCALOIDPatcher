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
internal sealed record TransitionSegment(int Begin, int Split, int End, string From, string? To,
    int FromId, int ToId, int Stage, bool Reverse, string MaterialMetadata,
    ImmutableArray<TransitionFrame> Frames);
internal sealed record TransitionNoteFrames(TimingNoteAddress Address, ImmutableArray<int> Original);
internal readonly record struct TransitionAnchor(int From, int To);
internal sealed record TransitionEdge(TransitionIdentity Identity, TransitionSegment Original, TransitionSegment Current);
internal sealed record TransitionJunction(int OriginalFrame, int CurrentFrame, bool Protected,
    ImmutableArray<TransitionEndpointRef> Aliases, int MinimumCandidate, int MaximumCandidate);
internal sealed record TransitionJunctionEdit(TransitionEndpointRef Endpoint, int Frame);
internal sealed record TransitionGainEdit(TransitionIdentity Edge, float Decibels);

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

    internal TransitionLayout(Guid scope, long revision, string inputKey,
        IEnumerable<TransitionSegment> original, IEnumerable<TransitionSegment> current,
        IEnumerable<TransitionNoteFrames> notes, IEnumerable<TransitionAnchor> anchors, int minimumOutputFrame = 0)
    {
        Scope = scope; Revision = revision;
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
                    foreach (var f in row.Frames) { writer.Write(f.Frame); writer.Write(f.SourceBits); writer.Write(f.Flag0); writer.Write(f.Flag1); }
                }
            }
            foreach (var a in Anchors) { writer.Write(a.From); writer.Write(a.To); }
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal TransitionJunction Junction(TransitionEndpointRef endpoint) => Junctions.SingleOrDefault(j => j.Aliases.Contains(endpoint))
        ?? throw new InvalidDataException("Transition endpoint does not belong to this layout.");
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
    internal TransitionControlPlan(string layoutFingerprint, IEnumerable<TransitionJunctionEdit>? junctions = null,
        IEnumerable<TransitionGainEdit>? gains = null)
    {
        LayoutFingerprint = layoutFingerprint;
        Junctions = (junctions ?? []).Take(1025).ToImmutableArray(); Gains = (gains ?? []).Take(1025).ToImmutableArray();
        if (string.IsNullOrWhiteSpace(layoutFingerprint) || Junctions.Length > 1024 || Gains.Length > 1024 ||
            Gains.Any(g => !float.IsFinite(g.Decibels) || g.Decibels is < -60 or > 12))
            throw new InvalidDataException("Invalid transition controls.");
    }

    internal TransitionCompiled Compile(TransitionLayout layout)
    {
        if (LayoutFingerprint != layout.Fingerprint) throw new InvalidOperationException("Transition layout is stale.");
        var targets = new Dictionary<int, int>();
        foreach (var edit in Junctions)
        {
            var joint = layout.Junction(edit.Endpoint);
            if (edit.Frame < joint.MinimumCandidate || edit.Frame > joint.MaximumCandidate || (joint.Protected && edit.Frame != joint.CurrentFrame))
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
        }
        return new(anchors, orderedGains, Convert.ToHexString(SHA256.HashData(stream.ToArray())));
    }
}
internal sealed record TransitionCompiled(ImmutableArray<TransitionAnchor> Anchors,
    ImmutableArray<TransitionGainEdit> Gains, string Fingerprint);
internal sealed record TransitionReachableRange(TransitionJunction Junction, ImmutableArray<int> ReachableFrames,
    int NativePlans, bool CacheHit, double Milliseconds)
{
    internal int Minimum => ReachableFrames.Min();
    internal int Maximum => ReachableFrames.Max();
    internal bool IsContiguous => ReachableFrames.Length == Maximum - Minimum + 1;
}
internal sealed record TransitionExecutionPlan(TransitionLayout Layout, TransitionCompiled Controls,
    ImmutableArray<TransitionSegment> Segments, int NativePlans, bool CacheHit, double Milliseconds);
