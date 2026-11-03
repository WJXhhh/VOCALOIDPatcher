using System;

namespace VOCALOIDPatcher.ConsonantTiming;

// Transient contention, distinct from invalid controls or native verification failures.
internal sealed class TimingPlannerBusyException(string message) : TimeoutException(message);

internal sealed class TimingPublicationRetryBudget
{
    private long _revision = -1, _started;
    private int _attempts;

    internal bool TrySchedule(long revision, long now, out int delayMs)
    {
        if (_revision != revision) { _revision = revision; _started = now; _attempts = 0; }
        delayMs = Math.Min(25 << Math.Min(_attempts, 4), 250);
        if (now - _started >= 5000) return false;
        _attempts++;
        return true;
    }

    internal void Reset() { _revision = -1; _attempts = 0; }
}
