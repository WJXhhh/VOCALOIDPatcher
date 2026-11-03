using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.ConsonantTiming;

// Only synchronous, patch-owned cache mutations on this source are internal.
// Commit(false) alone is not sufficient: real edits also use it.
internal sealed class TimingCacheInvalidationScope : IDisposable
{
    [ThreadStatic] private static Dictionary<nint, int>? _depths;
    private readonly nint _source;
    private bool _disposed;

    private TimingCacheInvalidationScope(nint source)
    {
        _source = source;
        var depths = _depths ??= new();
        depths.TryGetValue(source, out int depth);
        depths[source] = depth + 1;
    }

    internal static IDisposable Enter(nint source)
    {
        if (source == 0) throw new ArgumentException("Missing source.", nameof(source));
        return new TimingCacheInvalidationScope(source);
    }

    internal static bool IsActive(nint source) => _depths?.ContainsKey(source) == true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (--_depths![_source] == 0) _depths.Remove(_source);
    }
}
