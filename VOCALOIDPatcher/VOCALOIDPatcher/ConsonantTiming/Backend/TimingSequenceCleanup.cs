using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Close may fail without throwing. Keep the owning wrapper alive until native
// Close succeeds; the native wrapper's finalizer deliberately throws if open.
// All native calls, including retries, remain on the original owner thread.
internal static class TimingSequenceCleanup
{
    private sealed class Pending(WIVSMSequenceManager manager, WIVSMSequence sequence, int ownerThread)
    {
        internal readonly WIVSMSequenceManager Manager = manager;
        internal readonly WIVSMSequence Sequence = sequence;
        internal readonly nint Pointer = (nint)sequence;
        internal readonly int Thread = ownerThread;
        internal int Attempts;
    }
    private static readonly object Gate = new();
    // An address may be reused while an older wrapper is still pending. Each
    // owning wrapper must retain its own strong reference until acknowledged.
    private static readonly Dictionary<WIVSMSequence, Pending> Retained = new(ReferenceEqualityComparer.Instance);
    private static DispatcherTimer? _timer;
    private static bool _stopping;
    private static readonly PropertyInfo? NativePointer = typeof(WIVSMSequence).GetProperty("CppObjPtr", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static int PendingCount { get { lock (Gate) return Retained.Count; } }
    internal static long Failures { get; private set; }
    internal static long AlreadyReleased { get; private set; }

    internal static void Close(WIVSMSequenceManager manager, WIVSMSequence? sequence, int ownerThread, Dispatcher? dispatcher)
    {
        if (sequence == null || !sequence.IsOpen) return;
        Pending pending;
        lock (Gate)
        {
            if (!Retained.TryGetValue(sequence, out pending!)) Retained.Add(sequence, pending = new(manager, sequence, ownerThread));
        }
        if (_stopping) return;
        if (pending.Thread != Environment.CurrentManagedThreadId)
        {
            if (!_stopping && dispatcher is { HasShutdownStarted: false })
                dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { if (!_stopping) { TryClose(pending); Schedule(); } }));
            return;
        }
        TryClose(pending);
        Schedule();
    }

    private static void TryClose(Pending pending)
    {
        if (pending.Thread != Environment.CurrentManagedThreadId) return;
        try
        {
            var sequence = pending.Sequence;
            nint pointer = pending.Pointer;
            pending.Attempts++;
            // Some native Close failures occur after removal from the manager.
            // A second close could then hit a reused pointer. Verify ownership
            // before dereferencing, and acknowledge an already released owner.
            if (sequence.IsOpen && (NativeRenderSession.HasReplacementOwner(sequence) || !pending.Manager.HasSequence(pointer))) DetachReleased(sequence);
            if (sequence.IsOpen && (sequence.IsStaged && !sequence.Rollback() || !sequence.Close()) && pending.Manager.HasSequence(pointer))
                throw new InvalidOperationException("Private timing sequence Close failed: " + sequence.LastError);
            if (sequence.IsOpen && (NativeRenderSession.HasReplacementOwner(sequence) || !pending.Manager.HasSequence(pointer))) DetachReleased(sequence);
            if (sequence.IsOpen) throw new InvalidOperationException("Private timing sequence remained open after Close.");
            NativeRenderSession.UnregisterInternalSequence(sequence);
            lock (Gate) Retained.Remove(sequence);
        }
        catch (Exception error)
        {
            Failures++;
            Trace.TraceWarning("Timing sequence cleanup retained native ownership (attempt {0}): {1}", pending.Attempts, error);
        }
    }

    private static void DetachReleased(WIVSMSequence sequence)
    {
        if (NativePointer?.SetMethod == null) throw new NotSupportedException("Cannot acknowledge a released native sequence wrapper.");
        NativePointer.SetValue(sequence, IntPtr.Zero);
        AlreadyReleased++;
    }

    internal static void RetryPending()
    {
        Pending[] snapshot;
        lock (Gate) snapshot = Retained.Values.ToArray();
        foreach (var pending in snapshot) TryClose(pending);
    }

    internal static void Shutdown()
    {
        _stopping = true; _timer?.Stop(); RetryPending();
    }

    internal static object Diagnostics() => new { PendingCount, Failures, AlreadyReleased };

    private static void Schedule()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (_stopping || PendingCount == 0 || dispatcher == null || dispatcher.HasShutdownStarted || !dispatcher.CheckAccess()) return;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.ContextIdle, (_, _) =>
        {
            Pending[] snapshot;
            lock (Gate) snapshot = Retained.Values.Where(p => p.Attempts < 8).ToArray();
            foreach (var pending in snapshot) TryClose(pending);
            lock (Gate) if (!Retained.Values.Any(p => p.Attempts < 8)) _timer!.Stop();
        }, dispatcher);
        _timer.Start();
    }
}
