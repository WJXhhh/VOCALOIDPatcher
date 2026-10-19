using System;
using System.Collections.Immutable;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

// UI policy shared by the diamond, precise panel and isolated interaction
// tests. Selecting/painting never edits data; actual progress edits opt in.
internal static class TransitionExpressionControl
{
    internal readonly record struct Interval(TransitionSourceSides Side, int Begin, int End);
    internal static Interval? Range(TransitionSegment row, TransitionIdentity edge, TransitionControlPlan controls, bool requireProgress = true)
        => Range(row, edge, controls.Coverages, requireProgress);
    internal static Interval? Range(TransitionSegment row, TransitionIdentity edge, ImmutableArray<TransitionCoverageEdit> coverages, bool requireProgress = true)
    {
        if (row.MaterialCoverage == null || row.Frames.IsDefault || row.Frames.Length != row.End - row.Begin ||
            row.Split < row.Begin || row.Split > row.End) return null;
        var active = coverages.FirstOrDefault(c => c.Edge == edge)?.Sides ?? TransitionSourceSides.None;
        bool CanCover(TransitionSourceSides side)
        {
            var span = side == TransitionSourceSides.Left ? row.MaterialCoverage.Left : row.MaterialCoverage.Right;
            int count = side == TransitionSourceSides.Left ? row.Split - row.Begin : row.End - row.Split;
            if (span == null) return count == 0;
            return span.First is >= 0 and <= 65535 && span.Last is >= 0 and <= 65535 && count >= (span.First == span.Last ? 1 : 2);
        }
        // Do not silently drop an already enabled side when timing changes.
        if ((active.HasFlag(TransitionSourceSides.Left) && !CanCover(TransitionSourceSides.Left)) ||
            (active.HasFlag(TransitionSourceSides.Right) && !CanCover(TransitionSourceSides.Right))) return null;
        var side = row.To == "Sil" ? TransitionSourceSides.Left : TransitionSourceSides.Right;
        bool Allowed(TransitionSourceSides value) => active == TransitionSourceSides.None || active.HasFlag(value);
        bool Absent(TransitionSourceSides value) => value == TransitionSourceSides.Left ? row.Begin == row.Split : row.Split == row.End;
        if (!Allowed(side) || Absent(side)) side = side == TransitionSourceSides.Left ? TransitionSourceSides.Right : TransitionSourceSides.Left;
        if (!Allowed(side)) return null;
        var selected = side == TransitionSourceSides.Left ? row.MaterialCoverage.Left : row.MaterialCoverage.Right;
        int begin = side == TransitionSourceSides.Left ? row.Begin : row.Split;
        int end = (side == TransitionSourceSides.Left ? row.Split : row.End) - 1;
        if (CanCover(side) && selected != null && selected.First != selected.Last && end - begin >= (requireProgress ? 2 : 1))
            return new(side, begin, end);
        return null;
    }
    internal static double HandleFrame(Interval range, float amount)
        => TransitionPhaseMapping.HandleFrame(range.Begin, range.End, amount);
    internal static float AmountAt(Interval range, double frame)
        => TransitionPhaseMapping.AmountAt(range.Begin, range.End, frame);
    internal static TransitionControlPlan Enable(TransitionControlPlan controls, TransitionSegment row, TransitionIdentity edge)
    {
        var range = Range(row, edge, controls, requireProgress: false) ?? throw new NotSupportedException("No editable complete-source interval.");
        var active = controls.Coverages.FirstOrDefault(c => c.Edge == edge)?.Sides ?? TransitionSourceSides.None;
        return controls.WithCoverage(edge, active | range.Side);
    }
    internal static TransitionControlPlan Progress(TransitionControlPlan controls, TransitionSegment row, TransitionIdentity edge, float amount)
        => Enable(controls, row, edge).WithPhase(edge, amount);
    internal static TransitionControlPlan Source(TransitionControlPlan controls, TransitionSegment row, TransitionIdentity edge, bool enabled)
        => enabled ? Enable(controls, row, edge) : controls.WithCoverage(edge, TransitionSourceSides.None);
    internal static TransitionControlPlan Reset(TransitionControlPlan controls, TransitionIdentity edge)
        => controls.WithPhase(edge, 0).WithCoverage(edge, TransitionSourceSides.None);
}
