using System;
using System.Collections.Generic;
using System.Linq;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed record ConsonantTimingCandidate(int Velocity, long[] Boundaries);

// One missing VEL per Step. Pointer updates only replace Goal; cached measured
// candidates are reused, and the displayed/committed value is always measured.
internal sealed class ConsonantTimingDragSearch
{
    private readonly SortedDictionary<int, ConsonantTimingCandidate> _samples = new();
    internal ConsonantTimingCandidate Best { get; private set; }
    internal long Goal { get; private set; }
    internal bool Complete { get; private set; }

    internal ConsonantTimingDragSearch(int velocity, long[] boundaries)
    {
        if (velocity is < 0 or > 127 || boundaries.Length < 2) throw new ArgumentException("Missing initial timing.");
        Best = new ConsonantTimingCandidate(velocity, boundaries.ToArray());
        _samples[velocity] = Best;
        Goal = boundaries[0];
        Complete = true;
    }

    internal void SetGoal(long goal)
    {
        if (goal == Goal) return;
        Goal = goal;
        Complete = false;
        ChooseBest();
    }

    internal bool Step(Func<int, long[]?> query)
    {
        if (Complete) return false;
        int low = 0, high = 127;
        foreach (var pair in _samples)
        {
            long onset = pair.Value.Boundaries[0];
            if (onset == Goal) { Best = pair.Value; Complete = true; return false; }
            if (onset < Goal) low = Math.Max(low, pair.Key + 1);
            else high = Math.Min(high, pair.Key - 1);
        }
        if (low > high) { ChooseBest(); Complete = true; return false; }
        int velocity = (low + high) / 2;
        var boundaries = query(velocity);
        if (boundaries == null) return false; // Busy: retry latest goal next frame.
        if (boundaries.Length != Best.Boundaries.Length || boundaries.Zip(boundaries.Skip(1), (a, b) => a > b).Any(x => x))
            throw new NotSupportedException("Candidate phoneme topology changed.");
        _samples[velocity] = new ConsonantTimingCandidate(velocity, boundaries.ToArray());
        if (_samples.Values.Zip(_samples.Values.Skip(1), (a, b) => a.Boundaries[0] > b.Boundaries[0]).Any(x => x))
            throw new NotSupportedException("VEL response is not monotone in this context.");
        ChooseBest();
        if (Best.Boundaries[0] == Goal) Complete = true;
        return true;
    }

    private void ChooseBest()
    {
        decimal distance = Math.Abs((decimal)Best.Boundaries[0] - Goal);
        foreach (var sample in _samples.Values)
        {
            decimal next = Math.Abs((decimal)sample.Boundaries[0] - Goal);
            if (next >= distance) continue;
            distance = next;
            Best = sample;
        }
    }
}
