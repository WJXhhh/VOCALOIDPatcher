using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace VOCALOIDPatcher.BreathVolume;

internal readonly record struct NativeDseDiagnostics(
    int LoadState,
    int InstallResult,
    string? Error,
    ulong VtableRva,
    ulong CreateBufferCalls,
    ulong AddEventCalls,
    ulong SetPrerollCalls,
    ulong StartCalls,
    ulong StopCalls,
    ulong StepCalls,
    ulong StepSuccesses,
    long LastEventCount,
    int LastEventCode,
    int LastStartResult,
    int LastStepResult,
    int LastEventValueCount,
    ulong LastEventSequence,
    ulong LastEventField01,
    ulong LastEventField23,
    ulong LastEventValueHash,
    ulong LastEventSecondaryValueHash,
    ulong LastEventSecondaryValueCount,
    long LastInputFrame,
    ulong RenderOutputSamples,
    ulong RenderOutputHash,
    ulong RenderOutputPeak,
    ulong RenderOutputEnergy,
    ulong MetadataSteps,
    ulong PointerlessSteps,
    ulong PointerlessActiveSteps,
    ulong PointerlessLoudSteps,
    long PointerlessFirstFrame,
    long PointerlessLastFrame,
    ulong LastMetadataField01,
    ulong LastMetadataField23,
    ulong LastMetadataFlags,
    ulong LastMetadataPointerMask);

/// <summary>
/// Exact-sample diagnostic bridge for the DSE5::EngineImpl vtable probe. Any
/// module, vtable, or target mismatch leaves DSE untouched and reports failure.
/// </summary>
internal static unsafe class NativeDseCapture
{
    private const uint ExpectedAbiVersion = 14;
    private const int InstallModuleNotLoaded = -6;
    private const string LibraryName = "v6patch_clock.dll";

    private static readonly object LoadLock = new();
    private static nint _library;
    private static int _loadState;
    private static int _installResult = int.MinValue;
    private static string? _lastError;
    private static delegate* unmanaged[Cdecl]<int> _install;
    private static delegate* unmanaged[Cdecl]<NativeStatus*, int> _status;
    private static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("VOCALOIDPATCHER_DSE_PROBE"),
        "1",
        StringComparison.Ordinal);

    internal static NativeDseDiagnostics GetDiagnostics()
    {
        if (!Enabled)
            return new NativeDseDiagnostics(
                0, int.MinValue, null, 0, 0, 0, 0, 0, 0, 0, 0,
                -1, 0, int.MinValue, int.MinValue, 0, 0, 0, 0, 0, 0, 0,
                -1, 0, 0, 0, 0, 0, 0, 0, 0, -1, -1, 0, 0, 0, 0);

        var loaded = EnsureLoaded();
        NativeStatus status = default;
        if (!loaded || _status == null || _status(&status) < 0)
            return new NativeDseDiagnostics(
                Volatile.Read(ref _loadState), Volatile.Read(ref _installResult),
                Volatile.Read(ref _lastError), 0, 0, 0, 0, 0, 0, 0, 0,
                -1, 0, int.MinValue, int.MinValue, 0, 0, 0, 0, 0, 0, 0,
                -1, 0, 0, 0, 0, 0, 0, 0, 0, -1, -1, 0, 0, 0, 0);

        return new NativeDseDiagnostics(
            Volatile.Read(ref _loadState), status.InstallResult,
            Volatile.Read(ref _lastError), status.VtableRva,
            status.CreateBufferCalls, status.AddEventCalls, status.SetPrerollCalls,
            status.StartCalls, status.StopCalls, status.StepCalls, status.StepSuccesses,
            status.LastEventCount, status.LastEventCode, status.LastStartResult,
            status.LastStepResult, status.LastEventValueCount, status.LastEventSequence,
            status.LastEventField01, status.LastEventField23, status.LastEventValueHash,
            status.LastEventSecondaryValueHash, status.LastEventSecondaryValueCount,
            status.LastInputFrame, status.RenderOutputSamples, status.RenderOutputHash,
            status.RenderOutputPeak, status.RenderOutputEnergy, status.MetadataSteps,
            status.PointerlessSteps, status.PointerlessActiveSteps,
            status.PointerlessLoudSteps, status.PointerlessFirstFrame,
            status.PointerlessLastFrame, status.LastMetadataField01,
            status.LastMetadataField23, status.LastMetadataFlags,
            status.LastMetadataPointerMask);
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
                    library = NativeLibrary.Load(path);
                    var abiVersion = (delegate* unmanaged[Cdecl]<uint>)
                        NativeLibrary.GetExport(library, "v6_clock_abi_version");
                    if (abiVersion() != ExpectedAbiVersion)
                        throw new InvalidOperationException("Unsupported native DSE probe ABI.");
                    _install = (delegate* unmanaged[Cdecl]<int>)
                        NativeLibrary.GetExport(library, "v6_dse_install");
                    _status = (delegate* unmanaged[Cdecl]<NativeStatus*, int>)
                        NativeLibrary.GetExport(library, "v6_dse_status");
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

                Volatile.Write(ref _loadState,
                    installResult == InstallModuleNotLoaded ? 3 : 2);
                Volatile.Write(ref _lastError, $"DSE probe installation returned {installResult}.");
                return false;
            }
            catch (Exception exception)
            {
                if (library != 0)
                    NativeLibrary.Free(library);
                Volatile.Write(ref _lastError, $"{exception.GetType().Name}: {exception.Message}");
                Volatile.Write(ref _loadState, 2);
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        internal int InstallResult;
        internal uint Reserved;
        internal ulong VtableRva;
        internal ulong CreateBufferCalls;
        internal ulong AddEventCalls;
        internal ulong SetPrerollCalls;
        internal ulong StartCalls;
        internal ulong StopCalls;
        internal ulong StepCalls;
        internal ulong StepSuccesses;
        internal long LastEventCount;
        internal int LastEventCode;
        internal int LastStartResult;
        internal int LastStepResult;
        internal int LastEventValueCount;
        internal ulong LastEventSequence;
        internal ulong LastEventField01;
        internal ulong LastEventField23;
        internal ulong LastEventValueHash;
        internal ulong LastEventSecondaryValueHash;
        internal ulong LastEventSecondaryValueCount;
        internal long LastInputFrame;
        internal ulong RenderOutputSamples;
        internal ulong RenderOutputHash;
        internal ulong RenderOutputPeak;
        internal ulong RenderOutputEnergy;
        internal ulong MetadataSteps;
        internal ulong PointerlessSteps;
        internal ulong PointerlessActiveSteps;
        internal ulong PointerlessLoudSteps;
        internal long PointerlessFirstFrame;
        internal long PointerlessLastFrame;
        internal ulong LastMetadataField01;
        internal ulong LastMetadataField23;
        internal ulong LastMetadataFlags;
        internal ulong LastMetadataPointerMask;
    }
}
