using System;
using System.Threading;

namespace VOCALOIDPatcher.Utils;

// Observation callbacks can run per render block or per audio buffer. Poll the
// external opt-in marker at a bounded cadence, rather than stat it per callback.
internal sealed class CachedDiagnosticSwitch(Func<bool> read, Func<long> timestamp, long interval)
{
    private readonly object _sync = new();
    private long _nextPoll;
    private int _enabled;

    internal bool IsEnabled
    {
        get
        {
            long now = timestamp();
            if (now < Volatile.Read(ref _nextPoll)) return Volatile.Read(ref _enabled) != 0;
            lock (_sync)
            {
                if (now >= _nextPoll)
                {
                    bool value;
                    try { value = read(); }
                    catch { value = false; }
                    Volatile.Write(ref _enabled, value ? 1 : 0);
                    Volatile.Write(ref _nextPoll, now + Math.Max(1, interval));
                }
                return _enabled != 0;
            }
        }
    }
}
