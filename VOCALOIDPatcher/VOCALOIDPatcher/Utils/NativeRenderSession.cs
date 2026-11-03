using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Utils;

/// <summary>
/// Shared state for patched features that render their own duplicate sequence
/// (the extended Chinese pinyin carrier renderer and the EVEC render copy).
/// A duplicate sequence must never be mistaken for a user document by another
/// feature's trigger patches. The gate coordinates cooperating copy renderers;
/// ordinary VSM Part renderers can still run concurrently outside that gate.
/// </summary>
internal static class NativeRenderSession
{
    private static readonly object Sync = new();
    private static readonly Dictionary<nint, WeakReference<WIVSMSequence>> Internal = new();
    private static readonly Dictionary<nint, long> Foreground = new();
    private static long _foregroundTicket;
    internal static bool HasForegroundRendering { get { lock (Sync) return Foreground.Count != 0; } }
    internal static void EndForeground(WIVSMSequence sequence) { lock (Sync) Foreground.Remove((nint)sequence); }
    internal static void BeginForeground(WIVSMSequence sequence)
    {
        if (IsInternalSequence(sequence)) return;
        nint id = (nint)sequence; long ticket;
        lock (Sync) Foreground[id] = ticket = ++_foregroundTicket;
        _ = Watch();
        async Task Watch()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    lock (Sync) if (Foreground.GetValueOrDefault(id) != ticket) return;
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted) return;
                    bool done = await dispatcher.InvokeAsync(() =>
                    {
                        lock (Sync) if (Foreground.GetValueOrDefault(id) != ticket) return true;
                        return !sequence.IsOpen || sequence.IsFinishedRendering;
                    });
                    if (done) return;
                }
            }
            catch (OperationCanceledException) { }
            finally { lock (Sync) if (Foreground.GetValueOrDefault(id) == ticket) Foreground.Remove(id); }
        }
    }

    /// <summary>Coordinates copy renderers that explicitly acquire this gate.</summary>
    internal static SemaphoreSlim Gate { get; } = new(1, 1);

    internal static void RegisterInternalSequence(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return;
        lock (Sync)
            Internal[(nint)sequence] = new(sequence);
    }

    internal static void UnregisterInternalSequence(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return;
        lock (Sync)
        {
            // Close clears the wrapper's native pointer. Never leave that old
            // address classified as an internal source when VSM reuses it.
            foreach (var pair in Internal.Where(p => !p.Value.TryGetTarget(out var target) || ReferenceEquals(target, sequence)).ToArray())
                Internal.Remove(pair.Key);
        }
    }

    internal static void UnregisterInternalSequence(nint pointer)
    { lock (Sync) Internal.Remove(pointer); }

    // Registration receives the owning wrapper returned by Create/Duplicate.
    // Another registered owner at the same address proves that an older private
    // wrapper has outlived its native object. HasSequence alone cannot detect reuse.
    internal static bool HasReplacementOwner(WIVSMSequence sequence)
    {
        lock (Sync)
            return Internal.TryGetValue((nint)sequence, out var owner) && owner.TryGetTarget(out var current) &&
                !ReferenceEquals(current, sequence) && current.IsOpen && (nint)current == (nint)sequence;
    }

    internal static bool IsInternalSequence(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return false;
        lock (Sync)
        {
            nint pointer = (nint)sequence;
            if (!Internal.TryGetValue(pointer, out var owner)) return false;
            if (owner.TryGetTarget(out var target) && (nint)target == pointer && target.IsOpen) return true;
            Internal.Remove(pointer);
            return false;
        }
    }
}
