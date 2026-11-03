using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

internal readonly record struct PhonemeWarpRequest(int From, int To, bool Edited);
internal readonly record struct PhonemeBoundaryLimit(int Index, int RequestedFrames, int AppliedFrames);

// Nearest integer, strictly increasing warp in squared frame distance.
// Unedited boundaries are fixed. Requested project values are never rewritten.
internal static class PhonemeWarpProjection
{
    internal static ImmutableArray<TransitionAnchor> Resolve(IReadOnlyList<PhonemeWarpRequest> input, int minimumOutputFrame = 0)
    {
        if (input.Count is < 2 or > 8192 || input[0].Edited || input[^1].Edited ||
            input.Zip(input.Skip(1)).Any(p => p.First.From >= p.Second.From))
            throw new NotSupportedException("Invalid bounded phoneme warp requests.");
        var result = input.Select(p => new TransitionAnchor(p.From, p.To)).ToArray();
        int left = 0;
        while (left < input.Count - 1)
        {
            int right = left + 1;
            while (right < input.Count && input[right].Edited) right++;
            int count = right - left - 1;
            long low = count == 0 ? input[left].To : Math.Max(input[left].To, (long)minimumOutputFrame - 1);
            long high = (long)input[right].To - count - 1;
            if (high < low) throw new NotSupportedException("Fixed phoneme boundaries leave no legal interval.");
            var blocks = new List<(int Begin, int Count, long Sum)>();
            for (int i = 0; i < count; i++)
            {
                blocks.Add((i, 1, (long)input[left + i + 1].To - i - 1));
                while (blocks.Count > 1)
                {
                    var a = blocks[^2]; var b = blocks[^1];
                    if (a.Sum * b.Count <= b.Sum * a.Count) break;
                    blocks.RemoveAt(blocks.Count - 1);
                    blocks[^1] = (a.Begin, a.Count + b.Count, a.Sum + b.Sum);
                }
            }
            foreach (var block in blocks)
            {
                long level = Math.Clamp((long)Math.Round(block.Sum / (double)block.Count, MidpointRounding.AwayFromZero), low, high);
                for (int i = block.Begin; i < block.Begin + block.Count; i++)
                    result[left + i + 1] = new(input[left + i + 1].From, checked((int)(level + i + 1)));
            }
            left = right;
        }
        return result.ToImmutableArray();
    }
}
