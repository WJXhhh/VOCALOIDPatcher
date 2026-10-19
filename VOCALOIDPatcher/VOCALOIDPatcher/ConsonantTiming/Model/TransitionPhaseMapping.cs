using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

// A dimensionless phase control. It never generates timing anchors or moves
// allocation, split, flags, or either side's first/last source position.
internal static class TransitionPhaseMapping
{
    internal static bool CanEdit(TransitionSegment row)
    {
        if (row.Frames.IsDefault || row.Frames.Length != row.End - row.Begin || row.Split < row.Begin || row.Split > row.End) return false;
        int split = row.Split - row.Begin;
        for (int side = 0; side < 2; side++)
        {
            int begin = side == 0 ? 0 : split, end = side == 0 ? split : row.Frames.Length;
            if (end - begin < 3) continue;
            for (int i = begin + 1; i < end; i++)
                if (row.Frames[i].SourceBits != row.Frames[begin].SourceBits) return true;
        }
        return false;
    }
    internal static (int Begin, int End) HandleRange(TransitionSegment row)
        => row.Split - row.Begin >= row.End - row.Split ? (row.Begin, row.Split - 1) : (row.Split, row.End - 1);
    internal static double HandleFrame(TransitionSegment row, float amount)
    {
        ValidateAmount(amount);
        var (begin, end) = HandleRange(row);
        return HandleFrame(begin, end, amount);
    }
    internal static double HandleFrame(int begin, int end, float amount)
    {
        ValidateAmount(amount);
        if (end <= begin) throw new NotSupportedException("Transition has no editable phase interval.");
        return begin + (end - (double)begin) * Math.Pow(.5, 1 / Math.Pow(2, 2 * (double)amount));
    }
    internal static float AmountAt(TransitionSegment row, double frame)
    {
        if (!CanEdit(row) || !double.IsFinite(frame)) throw new NotSupportedException("Transition has no editable phase interval.");
        var (begin, end) = HandleRange(row);
        return AmountAt(begin, end, frame);
    }
    internal static float AmountAt(int begin, int end, double frame)
    {
        if (end <= begin || !double.IsFinite(frame)) throw new NotSupportedException("Transition has no editable phase interval.");
        double u = Math.Clamp((frame - begin) / (end - (double)begin), Math.Pow(.5, 4), Math.Pow(.5, .25));
        return (float)Math.Clamp(Math.Log2(Math.Log(.5) / Math.Log(u)) / 2, -1, 1);
    }
    internal static ImmutableArray<TransitionFrame> Apply(TransitionSegment row, float amount)
    {
        ValidateAmount(amount);
        if (amount == 0) return row.Frames;
        if (row.Frames.IsDefault || row.Frames.Length != row.End - row.Begin || row.Split < row.Begin || row.Split > row.End ||
            row.Frames.Where((f, i) => f.Frame != row.Begin + i || !float.IsFinite(BitConverter.Int32BitsToSingle(f.SourceBits)) ||
                BitConverter.Int32BitsToSingle(f.SourceBits) is < 0 or > 65535).Any())
            throw new NotSupportedException("Transition phase requires a complete bounded native ART map.");
        double power = Math.Pow(2, 2 * (double)amount);
        return row.Frames.Select(f =>
        {
            int begin = f.Frame < row.Split ? row.Begin : row.Split;
            int end = (f.Frame < row.Split ? row.Split : row.End) - 1;
            if (f.Frame == begin || f.Frame == end || end <= begin) return f;
            double sourceTime = (begin - row.Begin) + Math.Pow((f.Frame - (double)begin) / (end - begin), power) * (end - begin);
            int a = Math.Clamp((int)Math.Floor(sourceTime), begin - row.Begin, end - row.Begin), b = Math.Min(a + 1, end - row.Begin);
            double mix = sourceTime - a;
            float source = (float)(BitConverter.Int32BitsToSingle(row.Frames[a].SourceBits) * (1 - mix) +
                BitConverter.Int32BitsToSingle(row.Frames[b].SourceBits) * mix);
            return f with { SourceBits = BitConverter.SingleToInt32Bits(source) };
        }).ToImmutableArray();
    }
    private static void ValidateAmount(float amount)
    {
        if (!float.IsFinite(amount) || amount is < -1 or > 1) throw new InvalidDataException("Invalid transition phase amount.");
    }
}
