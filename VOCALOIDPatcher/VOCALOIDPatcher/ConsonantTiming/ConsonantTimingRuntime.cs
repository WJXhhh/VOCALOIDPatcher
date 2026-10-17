using System;
using System.Windows.Threading;
using System.Threading;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;
using VOCALOIDPatcher.Utils;

namespace VOCALOIDPatcher.ConsonantTiming;

// Owned by the Editor Dispatcher. No live source note crosses a thread or
// remains queued; native managers are released only after Shutdown below.
internal static class ConsonantTimingRuntime
{
    private static LocalConsonantTimingBackend? _backend;
    private static bool _queued, _unavailable, _stopping;
    private static long _revision;
    internal static long Revision => Interlocked.Read(ref _revision);

    internal static LocalConsonantTimingBackend? Backend => _backend;

    internal static void WarmUp(Dispatcher dispatcher)
    {
        if (_queued || _backend != null || _unavailable || _stopping) return;
        _queued = true;
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _queued = false;
            if (_stopping) return;
            if (App.SequenceManager == null || App.DSEManager == null || App.DatabaseManager == null) return;
            try
            {
                _backend = new LocalConsonantTimingBackend(App.SequenceManager, App.DSEManager, App.DatabaseManager);
                ConsonantTimingOverlay.BackendReady();
                Debug.Print("Local timing session ready.");
            }
            catch (Exception exception)
            {
                _unavailable = true;
                Debug.Print("Local timing unavailable: " + exception.GetType().Name);
            }
        }));
    }

    internal static void Shutdown()
    {
        _stopping = true;
        Interlocked.Increment(ref _revision);
        try { _backend?.Dispose(); }
        catch (Exception exception) { Debug.Print("Local timing close: " + exception.GetType().Name); }
        finally { _backend = null; }
    }

    internal static void SourceChanged(WIVSMSequence sequence)
    {
        if (NativeRenderSession.IsInternalSequence(sequence))
        {
            // Owned render copies can carry immutable user controls. Their
            // native edits invalidate planning, but never auto-start a render.
            if (SampledTimingHost.HasData(sequence)) SampledTimingHost.SourceChanged(sequence);
            return;
        }
        SampledTimingHost.SourceChanged(sequence);
        Interlocked.Increment(ref _revision);
        _backend?.ClearCache();
        ConsonantTimingOverlay.SourceChanged(sequence);
    }
    internal static void BeforeClose(WIVSMSequence sequence)
    {
        if (NativeRenderSession.IsInternalSequence(sequence)) return;
        Interlocked.Increment(ref _revision);
        _backend?.ClearCache();
        ConsonantTimingOverlay.SourceChanged(sequence);
        // Native Close publishes its final cache. Keep Part routes alive until
        // the managed Close postfix releases the captured native source handle.
    }
}
