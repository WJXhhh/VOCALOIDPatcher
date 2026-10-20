using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.Rendering;

// One retry per input request, with a circuit breaker across subsequent edits.
internal sealed class RenderRecoveryBudget
{
    private readonly Queue<long> _attempts = new();
    private long _lastRequest = -1;

    internal bool TryAcquire(long request, long monotonicMilliseconds)
    {
        while (_attempts.TryPeek(out long time) && monotonicMilliseconds - time >= 60_000) _attempts.Dequeue();
        if (_lastRequest == request || _attempts.Count >= 3) return false;
        _lastRequest = request;
        _attempts.Enqueue(monotonicMilliseconds);
        return true;
    }

    internal static bool CanRetry(int nativeReason) => nativeReason is 8 or 14 or 15 or 16 or 17 or 18 or 19;
}
