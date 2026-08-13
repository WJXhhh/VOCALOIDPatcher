using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.RegisterShift;

internal static unsafe class NativeRegisterShift
{
    private const uint ExpectedAbiVersion = 14;
    private const int ExpectedNativeStatusSize = 368;
    private const int ModuleNotLoaded = -6;
    private const string LibraryName = "v6patch_clock.dll";
    private static readonly object LoadLock = new();
    private static nint _library;
    private static int _loadState;
    private static int _installResult = int.MinValue;
    private static delegate* unmanaged[Cdecl]<int> _install;
    private static delegate* unmanaged[Cdecl]<ulong, ulong, NativeNote*, int, int> _setPart;
    private static delegate* unmanaged[Cdecl]<ulong, void> _removePart;
    private static delegate* unmanaged[Cdecl]<void> _clear;
    private static delegate* unmanaged[Cdecl]<NativeStatus*, int> _status;

    internal static RegisterShiftStatus Status
    {
        get
        {
            var state = Volatile.Read(ref _loadState);
            if (state == 2) return RegisterShiftStatus.Unsupported;
            if (state == 1) return RegisterShiftStatus.Installed;
            return state == 3 ? RegisterShiftStatus.Loading : RegisterShiftStatus.Unavailable;
        }
    }

    internal static int SetPart(WIVSMSequence sequence, WIVSMMidiPart part, ulong epoch,
        IReadOnlyDictionary<IntPtr, int> values)
    {
        if (!EnsureLoaded() || part == null || part.IsAi || epoch == 0 || sequence.NumSampleInFrame <= 0)
            return -1;
        var notes = new List<NativeNote>();
        var samplingRate = (double)sequence.GetSamplingRate();
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            if (part.GetNote(index) is not { } note || !values.TryGetValue(note.CppObjPtr, out var value))
                continue;
            value = Math.Clamp(value, RegisterShiftService.MinValue, RegisterShiftService.MaxValue);
            var beginSeconds = sequence.PresendTimeSec +
                               sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsPosTick);
            var endSeconds = sequence.PresendTimeSec +
                             sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsEndTick);
            notes.Add(new NativeNote
            {
                BeginFrame = Math.Max(0, (long)Math.Round(beginSeconds * samplingRate / sequence.NumSampleInFrame)),
                EndFrame = Math.Max(0, (long)Math.Round(endSeconds * samplingRate / sequence.NumSampleInFrame)),
                PitchCents = checked((note.NoteNumber - 69) * 100),
                Semitones = value,
                Ordinal = checked((int)index)
            });
        }
        var array = notes.ToArray();
        fixed (NativeNote* pointer = array)
            return _setPart(unchecked((ulong)((IntPtr)part).ToInt64()), epoch, pointer, array.Length);
    }

    internal static void RemovePart(ulong partHandle)
    {
        if (_loadState == 1 && _removePart != null) _removePart(partHandle);
    }

    internal static void Clear()
    {
        if (_loadState == 1 && _clear != null) _clear();
    }

    internal static NativeSnapshot GetSnapshot()
    {
        var value = new NativeStatus { InstallResult = Volatile.Read(ref _installResult) };
        if (_loadState == 1 && _status != null)
        {
            try { _status(&value); }
            catch { }
        }
        return new NativeSnapshot(value.InstallResult, value.InstallBitmap, value.PartCount,
            value.StateCount, value.Prepare1ACalls, value.Prepare1BCalls,
            value.ResolvedPartCalls, value.CurrentMatches, value.TargetMatches,
            value.MatchMisses, value.Selector1BCalls, value.Prune1ACalls,
            value.Score1ACalls, value.Scratch1ACalls, value.Applied1ACalls,
            value.Applied1BCalls, value.CallsiteMisses, value.LastPart,
            value.LastEpoch, value.LastOuter, value.LastParser, value.LastSynthesis,
            value.LastThread, value.LastBeginFrame, value.LastDurationFrames,
            value.LastPitchBits, value.LastMode, value.LastSlot, value.LastVsmMode,
            value.OneAInstallResult, value.LastCurrentSelection, value.LastTargetSelection,
            value.LastCurrentSelectionCount, value.LastTargetSelectionCount,
            value.LastCurrentShift, value.LastTargetShift,
            value.LastPoolPitchMinBits, value.LastPoolPitchMaxBits,
            value.LastPoolCount, value.LastPoolShift, value.LastPoolSignature,
            value.RenderOutputSignature, value.RenderInputSignature, value.RenderScopeCalls,
            value.Scope0Context, value.Scope0Output, value.Scope0Input,
            value.Scope1Context, value.Scope1Output, value.Scope1Input,
            value.Scope2Context, value.Scope2Output, value.Scope2Input);
    }

    private static bool EnsureLoaded()
    {
        var state = Volatile.Read(ref _loadState);
        if (state == 1) return true;
        if (state == 2) return false;
        lock (LoadLock)
        {
            state = _loadState;
            if (state == 1) return true;
            if (state == 2) return false;
            nint library = 0;
            try
            {
                if (state == 0)
                {
                    var path = Path.Combine(Patcher.DataDir, "native", LibraryName);
                    library = NativeLibrary.Load(path);
                    var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library,
                        "v6_clock_abi_version");
                    if (abi() != ExpectedAbiVersion)
                        throw new InvalidOperationException("Unsupported native register-shift ABI.");
                    if (sizeof(NativeStatus) != ExpectedNativeStatusSize)
                        throw new InvalidOperationException("Unexpected native register-shift status layout.");
                    _install = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library,
                        "v6_register_shift_install");
                    _setPart = (delegate* unmanaged[Cdecl]<ulong, ulong, NativeNote*, int, int>)
                        NativeLibrary.GetExport(library, "v6_register_shift_set_part");
                    _removePart = (delegate* unmanaged[Cdecl]<ulong, void>)
                        NativeLibrary.GetExport(library, "v6_register_shift_remove_part");
                    _clear = (delegate* unmanaged[Cdecl]<void>)
                        NativeLibrary.GetExport(library, "v6_register_shift_clear");
                    _status = (delegate* unmanaged[Cdecl]<NativeStatus*, int>)
                        NativeLibrary.GetExport(library, "v6_register_shift_status");
                    _library = library;
                    library = 0;
                }
                var result = _install();
                Volatile.Write(ref _installResult, result);
                Volatile.Write(ref _loadState, result >= 0 ? 1 : result == ModuleNotLoaded ? 3 : 2);
                RegisterShiftDiagnosticsLog.Write($"native install result={result} state={Status}");
                return result >= 0;
            }
            catch (Exception exception)
            {
                if (library != 0) NativeLibrary.Free(library);
                Volatile.Write(ref _loadState, 2);
                RegisterShiftDiagnosticsLog.Write($"native load failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeNote
    {
        internal long BeginFrame;
        internal long EndFrame;
        internal int PitchCents;
        internal int Semitones;
        internal int Ordinal;
        internal int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        internal int InstallResult;
        internal uint InstallBitmap;
        internal ulong PartCount;
        internal ulong StateCount;
        internal ulong Prepare1ACalls;
        internal ulong Prepare1BCalls;
        internal ulong ResolvedPartCalls;
        internal ulong CurrentMatches;
        internal ulong TargetMatches;
        internal ulong MatchMisses;
        internal ulong Selector1BCalls;
        internal ulong Prune1ACalls;
        internal ulong Score1ACalls;
        internal ulong Scratch1ACalls;
        internal ulong Applied1ACalls;
        internal ulong Applied1BCalls;
        internal ulong CallsiteMisses;
        internal ulong LastPart;
        internal ulong LastEpoch;
        internal ulong LastOuter;
        internal ulong LastParser;
        internal ulong LastSynthesis;
        internal ulong LastThread;
        internal long LastBeginFrame;
        internal long LastDurationFrames;
        internal ulong LastPitchBits;
        internal ulong LastCurrentSelection;
        internal ulong LastTargetSelection;
        internal int LastMode;
        internal int LastSlot;
        internal int LastVsmMode;
        internal int OneAInstallResult;
        internal int LastCurrentSelectionCount;
        internal int LastTargetSelectionCount;
        internal int LastCurrentShift;
        internal int LastTargetShift;
        internal uint LastPoolPitchMinBits;
        internal uint LastPoolPitchMaxBits;
        internal int LastPoolCount;
        internal int LastPoolShift;
        internal ulong LastPoolSignature;
        internal ulong RenderOutputSignature;
        internal ulong RenderInputSignature;
        internal ulong RenderScopeCalls;
        internal ulong Scope0Context;
        internal ulong Scope0Output;
        internal ulong Scope0Input;
        internal ulong Scope1Context;
        internal ulong Scope1Output;
        internal ulong Scope1Input;
        internal ulong Scope2Context;
        internal ulong Scope2Output;
        internal ulong Scope2Input;
    }

    internal readonly record struct NativeSnapshot(
        int InstallResult, uint InstallBitmap, ulong PartCount, ulong StateCount,
        ulong Prepare1ACalls, ulong Prepare1BCalls, ulong ResolvedPartCalls,
        ulong CurrentMatches, ulong TargetMatches, ulong MatchMisses,
        ulong Selector1BCalls, ulong Prune1ACalls, ulong Score1ACalls,
        ulong Scratch1ACalls, ulong Applied1ACalls, ulong Applied1BCalls,
        ulong CallsiteMisses, ulong LastPart, ulong LastEpoch, ulong LastOuter,
        ulong LastParser, ulong LastSynthesis, ulong LastThread,
        long LastBeginFrame, long LastDurationFrames, ulong LastPitchBits,
        int LastMode, int LastSlot, int LastVsmMode, int OneAInstallResult,
        ulong LastCurrentSelection, ulong LastTargetSelection,
        int LastCurrentSelectionCount, int LastTargetSelectionCount,
        int LastCurrentShift, int LastTargetShift,
        uint LastPoolPitchMinBits, uint LastPoolPitchMaxBits,
        int LastPoolCount, int LastPoolShift, ulong LastPoolSignature,
        ulong RenderOutputSignature, ulong RenderInputSignature, ulong RenderScopeCalls,
        ulong Scope0Context, ulong Scope0Output, ulong Scope0Input,
        ulong Scope1Context, ulong Scope1Output, ulong Scope1Input,
        ulong Scope2Context, ulong Scope2Output, ulong Scope2Input);
}
