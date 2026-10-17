using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed record ConsonantTimingSample(int Velocity, long[] Boundaries);

// Offline validation only. Never include this solver or its rendering backend in the Editor.
internal static partial class ConsonantTimingMath
{
    // The engine response is discrete, context dependent and may have plateaus.
    // Search measured positions; never infer a time from Emotion's VEL sensitivity.
    internal static async Task<ConsonantTimingSample> FindNearestAsync(long target, int preferred,
        Func<int, Task<ConsonantTimingSample>> measure, Func<bool> isCurrent)
    {
        var samples = new SortedDictionary<int, ConsonantTimingSample>();
        ConsonantTimingSample? best = null;

        async Task<ConsonantTimingSample> Read(int velocity)
        {
            if (!isCurrent()) throw new OperationCanceledException();
            if (samples.TryGetValue(velocity, out var cached)) return cached;
            var sample = await measure(velocity);
            if (!isCurrent()) throw new OperationCanceledException();
            if (sample.Velocity != velocity || sample.Boundaries.Length < 2)
                throw new InvalidOperationException("Invalid engine timing response.");
            foreach (var pair in samples)
                if ((pair.Key < velocity && pair.Value.Boundaries[0] > sample.Boundaries[0]) ||
                    (pair.Key > velocity && pair.Value.Boundaries[0] < sample.Boundaries[0]))
                    throw new InvalidOperationException("Non-monotonic engine timing response.");
            samples.Add(velocity, sample);
            if (best == null || Distance(sample) < Distance(best) ||
                (Distance(sample) == Distance(best) &&
                 Math.Abs(velocity - preferred) < Math.Abs(best.Velocity - preferred)))
                best = sample;
            return sample;
        }

        decimal Distance(ConsonantTimingSample sample) => Math.Abs((decimal)sample.Boundaries[0] - target);

        var initial = await Read(Math.Clamp(preferred, 0, 127));
        if (initial.Boundaries[0] == target) return initial;
        var first = await Read(0);
        var last = await Read(127);
        if (target <= first.Boundaries[0] || target >= last.Boundaries[0]) return best!;
        int low = 0;
        int high = 127;
        while (high - low > 1)
        {
            int middle = low + (high - low) / 2;
            var sample = await Read(middle);
            if (sample.Boundaries[0] == target) return sample;
            if (sample.Boundaries[0] < target) low = middle;
            else high = middle;
        }
        return best!;
    }
}
