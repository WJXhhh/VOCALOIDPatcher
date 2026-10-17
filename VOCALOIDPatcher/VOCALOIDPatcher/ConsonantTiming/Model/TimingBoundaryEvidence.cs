namespace VOCALOIDPatcher.ConsonantTiming;

// A phoneme boundary can be a split inside one sample or the exact junction
// between two samples. The latter requires evidence from both neighboring rows.
internal readonly record struct TimingBoundaryEvidence(int Begin, int End, int Split, string? From, string? To)
{
    internal bool MatchesSplit(int frame, string? left, string? right) =>
        Split == frame && (left == null || From == left) && (right == null || To == right);

    internal static bool MatchesJoin(int frame, string? left, string? right,
        TimingBoundaryEvidence before, TimingBoundaryEvidence after) =>
        left != null && right != null && before.Begin < frame && before.End == frame &&
        after.Begin == frame && after.End > frame &&
        (before.To ?? before.From) == left &&
        (after.From == right ||
            // DSE may concatenate independent incoming units (Sil/f, Sil/7)
            // instead of selecting an f/7 diphone. The score boundary is the
            // exact sample junction, before the second unit's internal split.
            after.From == "Sil" && after.To == right && after.Split >= frame && after.Split < after.End);
}
