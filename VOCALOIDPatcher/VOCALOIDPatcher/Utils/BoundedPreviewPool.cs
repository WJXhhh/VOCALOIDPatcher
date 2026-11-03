using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.Utils;

// Dispatcher-owned preview objects are reused between mouse updates, with a
// fixed retention limit even if an unusually long gesture creates more points.
internal sealed class BoundedPreviewPool<T>(Func<T> create, int capacity) where T : class
{
    private readonly List<T> _items = new();
    private int _next;

    internal void BeginUpdate() => _next = 0;

    internal T Rent()
    {
        int index = _next++;
        if (index < _items.Count) return _items[index];
        var item = create();
        if (_items.Count < capacity) _items.Add(item);
        return item;
    }
}
