using System.Collections.Generic;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming.Smoothing;

// Keep timing-only seams eligible for click repair even with equal nominal
// gains. Shared boundaries are processed once, with the actual adjacent gains.
internal static class TransitionJunctionPlanner
{
    internal static List<WaveJunctionProcessor.JunctionInfo> Collect(TransitionExecutionPlan plan,
        TransitionSmootherConfig config)
    {
        if (config.Mode == TransitionSmoothingMode.Disabled || plan.Layout.Edges.IsEmpty || plan.Segments.IsEmpty) return [];
        var edges = plan.Layout.Edges;
        var rows = plan.Segments.Where(s => s.To != null).ToArray();
        if (edges.Length != rows.Length) return [];
        var gains = plan.Controls.Gains.ToDictionary(g => g.Edge, g => (double)g.Decibels);
        var intervals = edges.Zip(rows, (edge, row) => (Row: row, Gain: gains.GetValueOrDefault(edge.Identity)))
            .OrderBy(item => item.Row.Begin).ToArray();
        var junctions = new SortedDictionary<long, WaveJunctionProcessor.JunctionInfo>();
        void Add(int frame, double left, double right)
        {
            long sample = (frame - 3087L) * 256;
            if (sample > 0) junctions[sample] = new(sample, left, right);
        }
        for (int i = 0; i < intervals.Length; i++)
        {
            var current = intervals[i];
            if (current.Row.End <= current.Row.Begin) continue;
            double left = i > 0 && intervals[i - 1].Row.End == current.Row.Begin ? intervals[i - 1].Gain : 0;
            Add(current.Row.Begin, left, current.Gain);
            if (current.Row.Split > current.Row.Begin && current.Row.Split < current.Row.End)
                Add(current.Row.Split, current.Gain, current.Gain);
            if (i == intervals.Length - 1 || intervals[i + 1].Row.Begin != current.Row.End)
                Add(current.Row.End, current.Gain, 0);
        }
        return junctions.Values.ToList();
    }
}
