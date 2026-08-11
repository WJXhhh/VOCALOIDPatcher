using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace VOCALOIDPatcher.BreathVolume;

internal readonly record struct NativeBreathMarker(
    ulong Sequence,
    IntPtr PartHandle,
    long BeginFrame,
    long EndFrame);

internal readonly record struct NativeBreathDiagnostics(
    int LoadState,
    int InstallResult,
    string? Error,
    ulong TargetRva,
    ulong CoreTargetRva,
    ulong CoreCalls,
    ulong MappedContexts,
    ulong ContextMisses,
    ulong HookCalls,
    ulong SuccessfulBlocks,
    ulong OutputSamples,
    ulong OutputPeak,
    ulong QueuedEvents,
    ulong DroppedEvents,
    ulong InvalidCalls,
    IntPtr LastPartHandle,
    long LastBeginFrame,
    long LastEndFrame,
    int LastResult);

/// <summary>
/// Exact-frame bridge for VSM's traditional automatic-breath PCM mixer.
/// Signature or ABI mismatches leave VSM untouched and fall back to detection.
/// </summary>
internal static unsafe class NativeBreathCapture
{
    private const uint ExpectedAbiVersion = 8;
    private const int InstallModuleNotLoaded = -6;
    private const int ReadBatchSize = 64;
    private const string LibraryName = "v6patch_clock.dll";

    private static readonly object LoadLock = new();

    private static nint _library;
    private static int _loadState;
    private static int _installResult = int.MinValue;
    private static string? _lastError;
    private static delegate* unmanaged[Cdecl]<int> _install;
    private static delegate* unmanaged[Cdecl]<void> _clear;
    private static delegate* unmanaged[Cdecl]<NativeEvent*, int, int> _read;
    private static delegate* unmanaged[Cdecl]<NativeStatus*, int> _status;
    internal static bool TryInitialize() => EnsureLoaded();

    internal static NativeBreathDiagnostics GetDiagnostics()
    {
        var loaded = EnsureLoaded();
        NativeStatus status = default;
        if (!loaded || _status == null || _status(&status) < 0)
            return new NativeBreathDiagnostics(
                Volatile.Read(ref _loadState), Volatile.Read(ref _installResult),
                Volatile.Read(ref _lastError),
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                IntPtr.Zero, -1, -1, int.MinValue);

        return new NativeBreathDiagnostics(
            Volatile.Read(ref _loadState), status.InstallResult,
            Volatile.Read(ref _lastError), status.TargetRva,
            status.CoreTargetRva, status.CoreCalls, status.MappedContexts,
            status.ContextMisses, status.HookCalls,
            status.SuccessfulBlocks, status.OutputSamples, status.OutputPeak,
            status.QueuedEvents, status.DroppedEvents, status.InvalidCalls,
            unchecked((IntPtr)(long)status.LastPartHandle),
            status.LastBeginFrame, status.LastEndFrame, status.LastResult);
    }

    internal static void ClearPending()
    {
        if (EnsureLoaded())
            _clear();
    }

    internal static IReadOnlyList<NativeBreathMarker> ReadPending()
    {
        if (!EnsureLoaded())
            return Array.Empty<NativeBreathMarker>();

        var result = new List<NativeBreathMarker>();
        var buffer = stackalloc NativeEvent[ReadBatchSize];
        while (result.Count < 256)
        {
            var count = _read(buffer, ReadBatchSize);
            if (count <= 0 || count > ReadBatchSize)
                break;
            for (var index = 0; index < count; index++)
            {
                var item = buffer[index];
                if (item.BeginFrame >= 0 && item.EndFrame > item.BeginFrame &&
                    item.EndFrame <= BreathProjectArchive.MaxNativeFrame)
                    result.Add(new NativeBreathMarker(
                        item.Sequence, unchecked((IntPtr)(long)item.PartHandle),
                        item.BeginFrame, item.EndFrame));
            }
            if (count < ReadBatchSize)
                break;
        }
        return result;
    }

    private static bool EnsureLoaded()
    {
        var state = Volatile.Read(ref _loadState);
        if (state == 1)
            return true;
        if (state == 2)
            return false;

        lock (LoadLock)
        {
            state = _loadState;
            if (state == 1)
                return true;
            if (state == 2)
                return false;

            nint library = 0;
            try
            {
                if (state == 0)
                {
                    var path = Path.Combine(global::VOCALOIDPatcher.Patcher.DataDir,
                        "native", LibraryName);
                    if (!File.Exists(path))
                    {
                        Volatile.Write(ref _lastError, "Native DLL is missing.");
                        Volatile.Write(ref _loadState, 2);
                        return false;
                    }

                    library = NativeLibrary.Load(path);
                    var abiVersion = (delegate* unmanaged[Cdecl]<uint>)
                        NativeLibrary.GetExport(library, "v6_clock_abi_version");
                    var actualAbiVersion = abiVersion();
                    if (actualAbiVersion != ExpectedAbiVersion)
                        throw new InvalidOperationException(
                            $"Unsupported native breath capture ABI {actualAbiVersion}; expected {ExpectedAbiVersion}.");

                    _install = (delegate* unmanaged[Cdecl]<int>)
                        NativeLibrary.GetExport(library, "v6_breath_install");
                    _clear = (delegate* unmanaged[Cdecl]<void>)
                        NativeLibrary.GetExport(library, "v6_breath_clear");
                    _read = (delegate* unmanaged[Cdecl]<NativeEvent*, int, int>)
                        NativeLibrary.GetExport(library, "v6_breath_read");
                    _status = (delegate* unmanaged[Cdecl]<NativeStatus*, int>)
                        NativeLibrary.GetExport(library, "v6_breath_status");
                    _library = library;
                    library = 0;
                }

                var installResult = _install();
                Volatile.Write(ref _installResult, installResult);
                if (installResult >= 0)
                {
                    Volatile.Write(ref _lastError, null);
                    Volatile.Write(ref _loadState, 1);
                    return true;
                }

                // Module loading can lag behind the managed patch bootstrap.
                // Preserve the resolved DLL and retry on the first render/UI use.
                Volatile.Write(ref _loadState,
                    installResult == InstallModuleNotLoaded ? 3 : 2);
                Volatile.Write(ref _lastError, $"Native hook installation returned {installResult}.");
                return false;
            }
            catch (Exception e)
            {
                if (library != 0)
                    NativeLibrary.Free(library);
                Volatile.Write(ref _lastError, $"{e.GetType().Name}: {e.Message}");
                Volatile.Write(ref _loadState, 2);
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEvent
    {
        internal ulong Sequence;
        internal ulong PartHandle;
        internal long BeginFrame;
        internal long EndFrame;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        internal int InstallResult;
        internal uint Reserved;
        internal ulong TargetRva;
        internal ulong CoreTargetRva;
        internal ulong CoreCalls;
        internal ulong MappedContexts;
        internal ulong ContextMisses;
        internal ulong HookCalls;
        internal ulong SuccessfulBlocks;
        internal ulong OutputSamples;
        internal ulong OutputPeak;
        internal ulong QueuedEvents;
        internal ulong DroppedEvents;
        internal ulong InvalidCalls;
        internal ulong LastPartHandle;
        internal long LastBeginFrame;
        internal long LastEndFrame;
        internal int LastResult;
        internal uint Reserved2;
    }
}
