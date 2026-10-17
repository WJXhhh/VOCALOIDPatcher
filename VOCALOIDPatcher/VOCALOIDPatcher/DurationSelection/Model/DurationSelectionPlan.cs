using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VOCALOIDPatcher.DurationSelection;

internal enum DurationCandidatePolicy { Native = 0, ExpandedExperimental = 1 }

internal sealed record DurationSelectionPlan
{
    internal const int AlgorithmVersion = 1;
    internal string Topology { get; }
    internal float Ratio { get; }
    internal DurationCandidatePolicy Policy { get; }
    internal bool IsDefault => Ratio == 1 && Policy == DurationCandidatePolicy.Native;
    internal string Fingerprint { get; }
    internal DurationSelectionPlan(string topology, double ratio = 1, DurationCandidatePolicy policy = DurationCandidatePolicy.Native)
    {
        if (string.IsNullOrWhiteSpace(topology) || topology.Length > 60 || !double.IsFinite(ratio) || ratio is < .001 or > 32 || !Enum.IsDefined(policy))
            throw new InvalidDataException("Invalid independent duration preference.");
        Topology = topology;
        Ratio = (float)(Math.Round(ratio * 1000000, MidpointRounding.AwayFromZero) / 1000000);
        Policy = policy;
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            AlgorithmVersion + ":" + topology + ":" + Ratio.ToString("R", CultureInfo.InvariantCulture) + ":" + (int)policy)));
    }
}

internal readonly record struct DurationNoteKey(nint Handle, long Generation);
