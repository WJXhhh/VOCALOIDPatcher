using System;
using System.Threading;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed record TimingRenderStamp(Guid Session, long Revision, string Controls);

// A render/cache entry belongs to both a document lifetime and one revision.
// Undo to identical values never revives a render from an earlier revision.
internal sealed class TimingRenderRevision
{
    private readonly Guid _session = Guid.NewGuid();
    private long _revision;
    private int _closed;
    internal long Revision => Volatile.Read(ref _revision);
    internal void Advance()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        Interlocked.Increment(ref _revision);
    }
    internal TimingRenderStamp Capture(string controls)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return new(_session, Revision, controls);
    }
    internal bool IsCurrent(TimingRenderStamp stamp, string controls) =>
        Volatile.Read(ref _closed) == 0 && stamp.Session == _session && stamp.Revision == Revision && stamp.Controls == controls;
    internal void Close() => Interlocked.Exchange(ref _closed, 1);
}
