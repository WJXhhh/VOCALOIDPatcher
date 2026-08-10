using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace VOCALOIDPatcher.Utils.Audio;

internal readonly record struct PlaybackClockSnapshot(
    double CurrentTime,
    double ProjectedTime,
    double PlaybackRate,
    double LatencySeconds,
    double PhaseErrorSeconds,
    ulong Generation,
    bool IsStale);

/// <summary>
/// Optional native playback clock. Loading and ABI resolution are deliberately
/// lazy so a missing or incompatible native DLL leaves the managed clock intact.
/// </summary>
internal static unsafe class NativePlaybackClock
{
    private const uint ExpectedAbiVersion = 1;
    private const uint SnapshotStale = 1 << 1;
    private const string LibraryName = "v6patch_clock.dll";

    private static readonly object LoadLock = new();

    private static nint _library;
    private static int _loadState;
    private static delegate* unmanaged[Cdecl]<uint> _abiVersion;
    private static delegate* unmanaged[Cdecl]<double, long, long, double, int> _reset;
    private static delegate* unmanaged[Cdecl]<double, long, double, int> _observe;
    private static delegate* unmanaged[Cdecl]<long, double, double, NativeClockSnapshot*, int> _snapshot;
    private static delegate* unmanaged[Cdecl]<float*, float*, int, int, int, int,
        NativeCorrelationResult*, int> _correlate;

    internal static bool TryReset(double engineTime, long timestamp, double latencySeconds)
    {
        if (!EnsureLoaded()) return false;

        return double.IsFinite(engineTime) && engineTime >= 0.0 &&
            double.IsFinite(latencySeconds) && latencySeconds >= 0.0 &&
            _reset(engineTime, timestamp, Stopwatch.Frequency, latencySeconds) >= 0;
    }

    internal static bool TryUpdate(double engineTime, long timestamp, double latencySeconds,
        double displayLead, double projectionHorizon, out PlaybackClockSnapshot value)
    {
        value = default;
        if (!EnsureLoaded()) return false;

        var observation = _observe(engineTime, timestamp, latencySeconds);
        if (observation == -2)
        {
            if (_reset(engineTime, timestamp, Stopwatch.Frequency, latencySeconds) < 0)
                return false;
        }
        else if (observation < 0)
        {
            return false;
        }

        return TryReadSnapshot(timestamp, displayLead, projectionHorizon, out value);
    }

    private static bool TryReadSnapshot(long timestamp, double displayLead,
        double projectionHorizon, out PlaybackClockSnapshot value)
    {
        value = default;
        NativeClockSnapshot snapshot;
        if (_snapshot(timestamp, displayLead, projectionHorizon, &snapshot) < 0 ||
            !double.IsFinite(snapshot.CurrentTime) ||
            !double.IsFinite(snapshot.ProjectedTime) ||
            !double.IsFinite(snapshot.PlaybackRate) ||
            !double.IsFinite(snapshot.LatencySeconds) ||
            !double.IsFinite(snapshot.PhaseErrorSeconds))
            return false;

        value = new PlaybackClockSnapshot(
            snapshot.CurrentTime,
            snapshot.ProjectedTime,
            snapshot.PlaybackRate,
            snapshot.LatencySeconds,
            snapshot.PhaseErrorSeconds,
            snapshot.Generation,
            (snapshot.Flags & SnapshotStale) != 0);
        return true;
    }

    internal static bool TryCorrelate(float[] source, float[] output, int count,
        int minLag, int maxLag, int exclusion, out int lag,
        out double correlation, out double prominence)
    {
        lag = -1;
        correlation = double.NegativeInfinity;
        prominence = double.NegativeInfinity;

        if (!EnsureLoaded() || count <= 0 || count > source.Length || count > output.Length ||
            minLag < 0 || maxLag < minLag || maxLag >= count || exclusion < 0)
            return false;

        NativeCorrelationResult result;
        fixed (float* sourcePointer = source)
        fixed (float* outputPointer = output)
        {
            if (_correlate(sourcePointer, outputPointer, count, minLag, maxLag,
                    exclusion, &result) < 0)
                return false;
        }

        if (result.Lag < minLag || result.Lag > maxLag ||
            !double.IsFinite(result.Correlation) || !double.IsFinite(result.Prominence))
            return false;

        lag = result.Lag;
        correlation = result.Correlation;
        prominence = result.Prominence;
        return true;
    }

    private static bool EnsureLoaded()
    {
        var state = Volatile.Read(ref _loadState);
        if (state != 0) return state == 1;

        lock (LoadLock)
        {
            if (_loadState != 0) return _loadState == 1;

            nint library = 0;
            try
            {
                var path = Path.Combine(global::VOCALOIDPatcher.Patcher.DataDir,
                    "native", LibraryName);
                if (!File.Exists(path))
                {
                    Volatile.Write(ref _loadState, 2);
                    return false;
                }

                library = NativeLibrary.Load(path);
                var abiVersion = (delegate* unmanaged[Cdecl]<uint>)
                    NativeLibrary.GetExport(library, "v6_clock_abi_version");
                if (abiVersion() != ExpectedAbiVersion)
                    throw new InvalidOperationException("Unsupported native playback clock ABI.");

                _abiVersion = abiVersion;
                _reset = (delegate* unmanaged[Cdecl]<double, long, long, double, int>)
                    NativeLibrary.GetExport(library, "v6_clock_reset");
                _observe = (delegate* unmanaged[Cdecl]<double, long, double, int>)
                    NativeLibrary.GetExport(library, "v6_clock_observe");
                _snapshot = (delegate* unmanaged[Cdecl]<long, double, double,
                    NativeClockSnapshot*, int>)
                    NativeLibrary.GetExport(library, "v6_clock_snapshot");
                _correlate = (delegate* unmanaged[Cdecl]<float*, float*, int, int, int, int,
                    NativeCorrelationResult*, int>)
                    NativeLibrary.GetExport(library, "v6_clock_correlate_f32");
                _library = library;
                Volatile.Write(ref _loadState, 1);
                return true;
            }
            catch
            {
                if (library != 0)
                    NativeLibrary.Free(library);
                Volatile.Write(ref _loadState, 2);
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeClockSnapshot
    {
        internal double CurrentTime;
        internal double ProjectedTime;
        internal double PlaybackRate;
        internal double LatencySeconds;
        internal double PhaseErrorSeconds;
        internal ulong Generation;
        internal uint Flags;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCorrelationResult
    {
        internal int Lag;
        internal double Correlation;
        internal double Prominence;
    }
}
