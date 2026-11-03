using System;

namespace VOCALOIDPatcher.Rendering;

// Owned by one Part on the Dispatcher. Ready audio must not restart the edit
// quiet period, but it must still respect the latest actual input change.
internal sealed class PostProcessingQuietPeriod(int milliseconds)
{
    private readonly int _milliseconds = milliseconds > 0 ? milliseconds : throw new ArgumentOutOfRangeException(nameof(milliseconds));
    private long _lastEdit;
    private bool _hasEdit;

    internal int Edited(long now)
    {
        _lastEdit = now; _hasEdit = true;
        return _milliseconds;
    }

    internal int UpstreamReady(long now)
        => !_hasEdit ? 0 : _milliseconds - (int)Math.Clamp(now - _lastEdit, 0, _milliseconds);
}
