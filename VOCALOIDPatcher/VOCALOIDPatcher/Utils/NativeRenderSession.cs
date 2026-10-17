using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
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
            Internal.Remove((nint)sequence);
            // Close clears the wrapper's native pointer. Never leave that old
            // address classified as an internal source when VSM reuses it.
            foreach (var pair in Internal.Where(p => !p.Value.TryGetTarget(out var target) || ReferenceEquals(target, sequence)).ToArray())
                Internal.Remove(pair.Key);
        }
    }

    internal static void UnregisterInternalSequence(nint pointer)
    { lock (Sync) Internal.Remove(pointer); }

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
