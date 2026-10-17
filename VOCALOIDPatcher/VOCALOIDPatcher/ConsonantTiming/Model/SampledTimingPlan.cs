using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VOCALOIDPatcher.ConsonantTiming;

// Schema vocabulary is independent of the current native implementation.
// Boundary i lies before phoneme i; transition i joins i-1 to i, with 0/N
// representing the incoming/outgoing context. Indices always bind to Topology.
internal sealed record TimingAdjustment(string Kind, int Index, float Value)
{
    internal const string NoteCoefficient = "note.coefficient";
    internal const string PhonemeBoundary = "phoneme.boundary.offsetSeconds";
    internal const string Transition = "transition.offsetSeconds";
}

internal sealed class SampledTimingPlan
{
    private readonly TimingAdjustment[] _adjustments;
    internal string Topology { get; }
    internal IReadOnlyList<TimingAdjustment> Adjustments => Array.AsReadOnly(_adjustments);
    internal string Fingerprint { get; }

    internal SampledTimingPlan(string topology, IEnumerable<TimingAdjustment> adjustments)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        if (string.IsNullOrWhiteSpace(topology) || topology.Length > 60) throw new InvalidDataException("Invalid timing topology.");
        Topology = topology;
        _adjustments = adjustments.Take(129).OrderBy(a => a?.Kind, StringComparer.Ordinal).ThenBy(a => a?.Index).ToArray()!;
        int count = topology.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (_adjustments.Length > 128 || _adjustments.Any(a => a == null || string.IsNullOrWhiteSpace(a.Kind) ||
            a.Kind.Length > 64 || !float.IsFinite(a.Value) || a.Index < -1 || a.Index > count || Math.Abs(a.Value) > 10) ||
            _adjustments.GroupBy(a => (a.Kind, a.Index)).Any(g => g.Count() > 1)) throw new InvalidDataException("Invalid timing adjustments.");
        foreach (var value in _adjustments)
        {
            if (value.Kind == TimingAdjustment.NoteCoefficient && (value.Index != -1 || value.Value is < 0 or > 1) ||
                value.Kind == TimingAdjustment.PhonemeBoundary && (value.Index < 0 || value.Index > count) ||
                value.Kind == TimingAdjustment.Transition && value.Index < 0)
                throw new InvalidDataException("Invalid timing target.");
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Topology);
            foreach (var a in _adjustments) { writer.Write(a.Kind); writer.Write(a.Index); writer.Write(a.Value == 0 ? 0f : a.Value); }
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal float? ExecutableCoefficient(string currentTopology)
    {
        if (currentTopology != Topology) throw new NotSupportedException("Timing phoneme topology changed.");
        if (_adjustments.Any(a => a.Kind != TimingAdjustment.NoteCoefficient))
            throw new NotSupportedException("This timing target is preserved but has no native executor yet.");
        return _adjustments.SingleOrDefault()?.Value;
    }

    internal float? PhonemeCoefficient(string currentTopology)
    {
        if (currentTopology != Topology || _adjustments.Any(a => a.Kind != TimingAdjustment.NoteCoefficient && a.Kind != TimingAdjustment.PhonemeBoundary))
            throw new NotSupportedException("Unsupported timing plan or changed phoneme topology.");
        return _adjustments.SingleOrDefault(a => a.Kind == TimingAdjustment.NoteCoefficient)?.Value;
    }

    internal bool HasPhonemeControls => _adjustments.Any(a => a.Kind == TimingAdjustment.PhonemeBoundary && a.Value != 0);
}

// Indices are locators, never enough on their own to attach an edit after load.
// Duration, pitch, bank and exact phonemes must all match; ambiguity is rejected.
internal sealed record TimingNoteAddress(int Track, int Part, int Note, long Position, int Duration,
    int Pitch, string Bank, string Topology)
{
    internal void Validate()
    {
        if (Track < 0 || Part < 0 || Note < 0 || Position < 0 || Duration <= 0 || Pitch is < 0 or > 127 ||
            string.IsNullOrWhiteSpace(Bank) || Bank.Length > 128 || string.IsNullOrWhiteSpace(Topology) || Topology.Length > 60)
            throw new InvalidDataException("Invalid timing note address.");
    }
}
