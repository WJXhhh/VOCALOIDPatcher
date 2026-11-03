using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace VOCALOIDPatcher.Rendering;

// A render replaces one Part's PCM, not every pending load in the editor.
// Tickets also prevent an old completion from removing a newer request.
internal sealed class PartLoadTickets
{
    private readonly ConcurrentDictionary<nint, long> _pending = new();
    private long _next;

    internal bool TryBegin(nint part, out long ticket)
    {
        ticket = Interlocked.Increment(ref _next);
        return _pending.TryAdd(part, ticket);
    }

    internal bool IsCurrent(nint part, long ticket)
        => _pending.TryGetValue(part, out long current) && current == ticket;

    internal bool Complete(nint part, long ticket)
        => ((ICollection<KeyValuePair<nint, long>>)_pending).Remove(new(part, ticket));

    internal void Invalidate(nint part) => _pending.TryRemove(part, out _);
    internal void Clear() => _pending.Clear();
}
