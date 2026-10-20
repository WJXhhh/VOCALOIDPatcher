namespace VOCALOIDPatcher.ConsonantTiming;

// A phoneme boundary can be a split inside one sample or the exact junction
// between two samples. The latter requires evidence from both neighboring rows.
internal readonly record struct TimingBoundaryEvidence(int Begin, int End, int Split, string? From, string? To)
{
    internal bool MatchesSplit(int frame, string? left, string? right) =>
        Split == frame && (left == null || From == left) && (right == null || To == right);

    // When DSE has no direct diphone, VSM assigns the outgoing silence split
    // to the logical boundary. Both adjoining work items must prove that path;
    // a left/Sil split alone cannot identify the following phoneme.
    internal static bool MatchesSilenceBridge(int frame, string? left, string? right,
        TimingBoundaryEvidence outgoing, TimingBoundaryEvidence incoming,
        TimingBoundaryEvidence? continuation = null) =>
        left != null && right != null && left != "Sil" && right != "Sil" &&
        outgoing.From == left && outgoing.To == "Sil" && outgoing.Split == frame &&
        outgoing.Begin < frame && frame < outgoing.End && outgoing.End == incoming.Begin &&
        incoming.From == "Sil" && incoming.To == right && incoming.Begin <= incoming.Split &&
        incoming.Begin < incoming.End &&
        (incoming.Split < incoming.End ||
            // Compression can remove the incoming unit's right half while
            // the next unit still contains the same phoneme on its left.
            // Prove that actual continuation rather than accepting a label
            // whose sound has no allocated frames.
            incoming.Split == incoming.End && continuation is { } next &&
            next.Begin == incoming.End && next.From == right && next.End > next.Begin &&
            (next.To == null || next.Split > next.Begin && next.Split <= next.End));

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
