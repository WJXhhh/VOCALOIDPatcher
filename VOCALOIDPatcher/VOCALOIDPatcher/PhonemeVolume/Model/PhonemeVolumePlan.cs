using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VOCALOIDPatcher.PhonemeVolume;

internal sealed class PhonemeVolumePlan
{
    internal const float Minimum = -60, Maximum = 12;
    private readonly float[] _values;
    internal string Topology { get; }
    internal IReadOnlyList<float> Values { get; }
    internal bool IsDefault => _values.All(v => v == 0);

    internal PhonemeVolumePlan(string topology, IEnumerable<float>? values = null)
    {
        if (string.IsNullOrWhiteSpace(topology) || topology.Length > 60)
            throw new InvalidDataException("Invalid phoneme volume topology.");
        Topology = topology;
        int count = topology.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        _values = values?.Take(61).ToArray() ?? new float[count];
        if (_values.Length != count || _values.Any(v => !float.IsFinite(v) || v < Minimum || v > Maximum))
            throw new InvalidDataException("Invalid phoneme volume values.");
        for (int i = 0; i < _values.Length; i++) _values[i] = Normalize(_values[i]);
        Values = Array.AsReadOnly(_values);
    }

    internal static float Normalize(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return (float)Math.Round(Math.Clamp(value, Minimum, Maximum), 1, MidpointRounding.AwayFromZero);
    }

    internal PhonemeVolumePlan With(int index, double decibels)
    {
        if ((uint)index >= _values.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var result = (float[])_values.Clone();
        result[index] = Normalize(decibels);
        return new(Topology, result);
    }

    internal bool SameAs(PhonemeVolumePlan other) => Topology == other.Topology && _values.SequenceEqual(other._values);
    internal static double LinearGain(double decibels) => decibels <= Minimum ? 0 : Math.Pow(10, decibels / 20);
}

internal readonly record struct PhonemeVolumeKey(nint Handle, long Generation);
internal readonly record struct PhonemeGainRegion(double BeginSeconds, double EndSeconds, float Decibels);
