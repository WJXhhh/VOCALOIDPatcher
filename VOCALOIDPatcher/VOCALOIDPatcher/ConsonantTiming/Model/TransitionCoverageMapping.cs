using System;
using System.Collections.Immutable;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

// Cover the selected native material stage, then let Phase redistribute its
// interior. Even an unwarped schedule may already crop a short note. Use its
// certified material alignment, never that cropped map or the whole file.
internal static class TransitionCoverageMapping
{
    internal static ImmutableArray<TransitionFrame> Apply(TransitionSegment reference, TransitionSegment allocation, TransitionSourceSides sides)
    {
        if (sides is not (TransitionSourceSides.Left or TransitionSourceSides.Right or TransitionSourceSides.Both) ||
            reference.To == null || reference.MaterialCoverage == null || reference.MaterialCoverage != allocation.MaterialCoverage ||
            (reference.From, reference.To, reference.Stage, reference.Reverse, reference.MaterialMetadata) !=
            (allocation.From, allocation.To, allocation.Stage, allocation.Reverse, allocation.MaterialMetadata))
            throw new NotSupportedException("Source coverage requires the exact native reference material and stage.");
        Validate(reference); Validate(allocation);
        var frames = allocation.Frames.ToArray();
        foreach (var side in new[] { TransitionSourceSides.Left, TransitionSourceSides.Right })
        {
            if (!sides.HasFlag(side)) continue;
            int start = side == TransitionSourceSides.Left ? 0 : allocation.Split - allocation.Begin;
            int end = side == TransitionSourceSides.Left ? allocation.Split - allocation.Begin : frames.Length;
            var span = side == TransitionSourceSides.Left ? reference.MaterialCoverage.Left : reference.MaterialCoverage.Right;
            int count = end - start;
            if (count == 0 && span == null) continue;
            // A collapsed side cannot represent both source endpoints. Report
            // this domain limit before publication instead of moving timing.
            if (count == 0 || span == null || span.First is < 0 or > 65535 || span.Last is < 0 or > 65535 ||
                (count == 1 && span.First != span.Last))
                throw new NotSupportedException("Source coverage needs enough output frames to preserve its native endpoints.");
            for (int i = 0; i < count; i++)
            {
                float value = (float)(span.First + i * (span.Last - (double)span.First) / Math.Max(1, count - 1));
                frames[start + i] = frames[start + i] with { SourceBits = BitConverter.SingleToInt32Bits(value) };
            }
        }
        // The final source endpoint is the reference seam into the native
        // STA. Keep its allocation, source start and native overlap state;
        // do not transplant ART source indices into a different STA material.
        return frames.ToImmutableArray();
    }
    private static float Value(TransitionFrame frame) => BitConverter.Int32BitsToSingle(frame.SourceBits);
    private static void Validate(TransitionSegment row)
    {
        if (row.Frames.IsDefault || row.End < row.Begin || row.Split < row.Begin || row.Split > row.End ||
            row.Frames.Length != row.End - row.Begin || row.Frames.Where((f, i) => f.Frame != row.Begin + i ||
                !float.IsFinite(Value(f)) || Value(f) is < 0 or > 65535).Any())
            throw new NotSupportedException("Source coverage requires a complete bounded native frame map.");
    }
}
