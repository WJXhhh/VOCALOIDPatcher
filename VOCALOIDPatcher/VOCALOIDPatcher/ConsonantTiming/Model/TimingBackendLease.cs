using System;

namespace VOCALOIDPatcher.ConsonantTiming;

// Project state owns this lease, never the reverse. Loading, saving, history
// and invalidation must not invoke the factory or cache a failed construction.
internal sealed class TimingBackendLease<T>(Func<T> factory, bool ownsInstance = true, T? instance = null) : IDisposable where T : class, IDisposable
{
    private T? _instance = instance;
    private bool _disposed;
    internal bool IsCreated => _instance != null;
    internal Exception? Fault { get; private set; }

    internal T Require()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_instance != null) return _instance;
        try
        {
            var instance = factory() ?? throw new InvalidOperationException("Timing backend factory returned no instance.");
            _instance = instance; Fault = null;
            return instance;
        }
        catch (Exception error) { Fault = error; throw; }
    }

    internal void IfCreated(Action<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_instance != null) action(_instance);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var instance = _instance; _instance = null;
        if (ownsInstance) instance?.Dispose();
    }
}
