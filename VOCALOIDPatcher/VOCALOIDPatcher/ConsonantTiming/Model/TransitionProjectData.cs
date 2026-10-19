using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VOCALOIDPatcher.ConsonantTiming;

internal abstract record TransitionExtensible
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
internal sealed record TransitionProjectNote(Guid Id, TimingNoteAddress Address) : TransitionExtensible;
internal sealed record TransitionProjectPhone(Guid Note, int Index, string Symbol) : TransitionExtensible;
internal sealed record TransitionProjectEdge(TransitionProjectPhone? Left, TransitionProjectPhone? Right,
    string From, string To, int Stage, bool Reverse, int Occurrence) : TransitionExtensible;
internal sealed record TransitionProjectEndpoint(TransitionProjectEdge Edge, TransitionEndpoint Endpoint) : TransitionExtensible;
internal sealed record TransitionProjectTimingScale(TransitionProjectPhone Phone, int Frames) : TransitionExtensible;
internal sealed record TransitionProjectJunction(ImmutableArray<TransitionProjectEndpoint> Aliases, double OffsetSeconds) : TransitionExtensible
{
    public TransitionProjectTimingScale? TimingScale { get; init; }
}
internal sealed record TransitionProjectGain(TransitionProjectEdge Edge, float Decibels) : TransitionExtensible;
internal sealed record TransitionProjectOnset(TransitionProjectEdge Edge, double OffsetSeconds) : TransitionExtensible
{
    public TransitionProjectTimingScale? TimingScale { get; init; }
}
internal sealed record TransitionProjectCoverage(TransitionProjectEdge Edge, TransitionSourceSides Sides) : TransitionExtensible;
internal sealed record TransitionProjectPhase(TransitionProjectEdge Edge, float Amount) : TransitionExtensible;
internal sealed record TransitionProjectData(int Version, ImmutableArray<TransitionProjectNote> Notes,
    ImmutableArray<TransitionProjectJunction> Junctions, ImmutableArray<TransitionProjectGain> Gains) : TransitionExtensible
{
    public ImmutableArray<TransitionProjectOnset> Onsets { get; init; } = [];
    public ImmutableArray<TransitionProjectPhase> Phases { get; init; } = [];
    public ImmutableArray<TransitionProjectCoverage> Coverages { get; init; } = [];
    internal static TransitionProjectData Empty => new(1, [], [], []);
    internal bool HasControls => Junctions.Length != 0 || Gains.Length != 0 || Onsets.Length != 0 || Phases.Length != 0 || Coverages.Length != 0;
    internal bool HasExtensions => Extra?.Count > 0 || Notes.Any(n => n.Extra?.Count > 0) ||
        Junctions.Any(j => j.Extra?.Count > 0 || Extended(j.TimingScale) || j.Aliases.Any(a => a.Extra?.Count > 0 || Extended(a.Edge))) ||
        Gains.Any(g => g.Extra?.Count > 0 || Extended(g.Edge)) || Onsets.Any(o => o.Extra?.Count > 0 || Extended(o.Edge) || Extended(o.TimingScale)) ||
        Phases.Any(p => p.Extra?.Count > 0 || Extended(p.Edge)) || Coverages.Any(c => c.Extra?.Count > 0 || Extended(c.Edge));
    private static bool Extended(TransitionProjectEdge edge) => edge.Extra?.Count > 0 || edge.Left?.Extra?.Count > 0 || edge.Right?.Extra?.Count > 0;
    private static bool Extended(TransitionProjectTimingScale? scale) => scale?.Extra?.Count > 0 || scale?.Phone?.Extra?.Count > 0;

    internal static string EdgeKey(TransitionProjectEdge edge) => JsonSerializer.Serialize(new
    {
        Left = PhoneKey(edge.Left), Right = PhoneKey(edge.Right), edge.From, edge.To, edge.Stage, edge.Reverse, edge.Occurrence
    });
    private static string? PhoneKey(TransitionProjectPhone? phone) => phone == null ? null :
        JsonSerializer.Serialize(new { phone.Note, phone.Index, phone.Symbol });
    internal static string EndpointKey(TransitionProjectEndpoint endpoint) => EdgeKey(endpoint.Edge) + ":" + (int)endpoint.Endpoint;
    internal static IEnumerable<Guid> NoteIds(TransitionProjectEdge edge) => new[] { edge.Left?.Note, edge.Right?.Note }.OfType<Guid>().Distinct();

    internal TransitionProjectData Validate()
    {
        if (Version != 1 || Notes.IsDefault || Junctions.IsDefault || Gains.IsDefault || Onsets.IsDefault || Phases.IsDefault || Coverages.IsDefault ||
            Notes.Length > 100000 || Junctions.Length > 100000 || Gains.Length > 100000 || Onsets.Length > 100000 || Phases.Length > 100000 || Coverages.Length > 100000)
            throw new InvalidDataException("Invalid transition project structure.");
        var notes = new Dictionary<Guid, TransitionProjectNote>();
        var addresses = new HashSet<TimingNoteAddress>();
        foreach (var note in Notes)
        {
            if (note == null || note.Id == Guid.Empty || note.Address == null || !notes.TryAdd(note.Id, note) || !addresses.Add(note.Address))
                throw new InvalidDataException("Duplicate or invalid transition note identity.");
            note.Address.Validate();
        }
        void Phone(TransitionProjectPhone? phone, string symbol)
        {
            if (phone == null) { if (symbol != "Sil") throw new InvalidDataException("Missing transition side."); return; }
            if (!notes.TryGetValue(phone.Note, out var note)) throw new InvalidDataException("Missing transition note.");
            var tokens = note.Address.Topology.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (phone.Index < 0 || phone.Index >= tokens.Length || tokens[phone.Index] != phone.Symbol || phone.Symbol != symbol)
                throw new InvalidDataException("Transition phoneme identity changed.");
        }
        void Edge(TransitionProjectEdge edge)
        {
            if (edge == null || edge.Stage is < 0 or > 127 || edge.Occurrence is < 0 or > 1023 ||
                string.IsNullOrWhiteSpace(edge.From) || string.IsNullOrWhiteSpace(edge.To) || edge.From.Length > 64 || edge.To.Length > 64 ||
                (edge.Left == null && edge.Right == null)) throw new InvalidDataException("Invalid transition edge.");
            Phone(edge.Left, edge.From); Phone(edge.Right, edge.To);
            var parts = NoteIds(edge).Select(id => notes[id].Address).Select(a => (a.Track, a.Part)).Distinct();
            if (parts.Count() != 1) throw new InvalidDataException("Transition crosses Part ownership.");
        }
        void Scale(TransitionProjectTimingScale? scale, IEnumerable<TransitionProjectEdge> owners)
        {
            if (scale == null) return;
            if (scale.Phone == null || scale.Frames is < 1 or > 1000000)
                throw new InvalidDataException("Invalid transition timing reference.");
            Phone(scale.Phone, scale.Phone.Symbol);
            if (!owners.SelectMany(e => new[] { e.Left, e.Right }).Any(p => p != null &&
                (p.Note, p.Index, p.Symbol) == (scale.Phone.Note, scale.Phone.Index, scale.Phone.Symbol)))
                throw new InvalidDataException("Transition timing reference has a foreign owner.");
        }
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var junction in Junctions)
        {
            if (junction == null || junction.Aliases.IsDefault || junction.Aliases.Length is < 1 or > 3072 ||
                !double.IsFinite(junction.OffsetSeconds) || Math.Abs(junction.OffsetSeconds) > 24)
                throw new InvalidDataException("Invalid transition junction.");
            var part = new HashSet<(int, int)>();
            foreach (var alias in junction.Aliases)
            {
                if (alias == null || alias.Endpoint is not (TransitionEndpoint.Begin or TransitionEndpoint.End or TransitionEndpoint.Split))
                    throw new InvalidDataException("Invalid transition endpoint.");
                Edge(alias.Edge);
                if (!endpoints.Add(EndpointKey(alias))) throw new InvalidDataException("Transition endpoint is stored twice.");
                foreach (var id in NoteIds(alias.Edge)) { var a = notes[id].Address; part.Add((a.Track, a.Part)); }
            }
            if (part.Count != 1) throw new InvalidDataException("Shared junction crosses Part ownership.");
            Scale(junction.TimingScale, junction.Aliases.Select(a => a.Edge));
        }
        var gains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gain in Gains)
        {
            if (gain == null || !float.IsFinite(gain.Decibels) || gain.Decibels is < -60 or > 12)
                throw new InvalidDataException("Invalid transition gain.");
            Edge(gain.Edge);
            if (!gains.Add(EdgeKey(gain.Edge))) throw new InvalidDataException("Duplicate transition gain.");
        }
        var onsets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var onset in Onsets)
        {
            if (onset == null || !double.IsFinite(onset.OffsetSeconds) || Math.Abs(onset.OffsetSeconds) > 4096 * 256d / 44100)
                throw new InvalidDataException("Invalid onset source progress.");
            Edge(onset.Edge);
            if (onset.Edge.From != "Sil" || onset.Edge.Right == null || !onsets.Add(EdgeKey(onset.Edge)))
                throw new InvalidDataException("Duplicate or unsupported onset source target.");
            Scale(onset.TimingScale, [onset.Edge]);
        }
        var phases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phase in Phases)
        {
            if (phase == null || !float.IsFinite(phase.Amount) || phase.Amount is < -1 or > 1)
                throw new InvalidDataException("Invalid fixed-range phase.");
            Edge(phase.Edge);
            if (!phases.Add(EdgeKey(phase.Edge))) throw new InvalidDataException("Duplicate fixed-range phase.");
        }
        var coverages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var coverage in Coverages)
        {
            if (coverage == null || coverage.Sides is not (TransitionSourceSides.Left or TransitionSourceSides.Right or TransitionSourceSides.Both))
                throw new InvalidDataException("Invalid transition source coverage.");
            Edge(coverage.Edge);
            if (!coverages.Add(EdgeKey(coverage.Edge))) throw new InvalidDataException("Duplicate source coverage.");
        }
        return this;
    }

    internal TransitionProjectData KeepNotes(Func<TransitionProjectNote, bool> keep)
    {
        var ids = Notes.Where(keep).Select(n => n.Id).ToHashSet();
        var joints = Junctions.Where(j => j.Aliases.All(a => NoteIds(a.Edge).All(ids.Contains))).ToImmutableArray();
        var gains = Gains.Where(g => NoteIds(g.Edge).All(ids.Contains)).ToImmutableArray();
        var onsets = Onsets.Where(o => NoteIds(o.Edge).All(ids.Contains)).ToImmutableArray();
        var phases = Phases.Where(p => NoteIds(p.Edge).All(ids.Contains)).ToImmutableArray();
        var coverages = Coverages.Where(c => NoteIds(c.Edge).All(ids.Contains)).ToImmutableArray();
        var used = joints.SelectMany(j => j.Aliases).SelectMany(a => NoteIds(a.Edge)).Concat(gains.SelectMany(g => NoteIds(g.Edge)))
            .Concat(onsets.SelectMany(o => NoteIds(o.Edge))).Concat(phases.SelectMany(p => NoteIds(p.Edge))).Concat(coverages.SelectMany(c => NoteIds(c.Edge))).ToHashSet();
        return this with { Notes = Notes.Where(n => used.Contains(n.Id)).ToImmutableArray(), Junctions = joints, Gains = gains, Onsets = onsets, Phases = phases, Coverages = coverages };
    }
}
