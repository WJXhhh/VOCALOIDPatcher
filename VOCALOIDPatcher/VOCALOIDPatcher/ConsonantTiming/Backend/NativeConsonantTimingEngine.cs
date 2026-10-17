using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace VOCALOIDPatcher.ConsonantTiming;

// Private planning engine session. No renderer, PCM buffer or global
// vtable replacement. The exact image and entry points are pinned below.
internal sealed unsafe partial class NativeConsonantTimingEngine : IDisposable
{
    private readonly nint _library;
    private readonly nint _nativeMutex;
    private nint _engine;
    private nint _state;
    private nint _queryMutex;
    private bool _started;
    private bool _nativeRunning;
    internal double InitializationMs { get; }
    internal int CurrentFrame => Marshal.ReadInt32(_state, LocalTimingNative.Profile.StateCurrentFrame);

    internal NativeConsonantTimingEngine(nint manager)
    {
        LocalTimingNative.VerifyImages();
        _library = LocalTimingNative.Dse;
        var watch = Stopwatch.StartNew();
        _engine = ((delegate* unmanaged<nint, nint>)(_library + LocalTimingNative.Profile.DseCreateEngine))(manager);
        if (_engine == 0) throw new InvalidOperationException("No private engine slot.");
        try
        {
            if (Marshal.ReadIntPtr(_engine) != _library + LocalTimingNative.Profile.DseEngineVtable)
                throw new InvalidOperationException("Private engine vtable mismatch.");
            byte* configuration = stackalloc byte[40];
            new Span<byte>(configuration, 40).Clear();
            *(int*)configuration = 44100;
            *(int*)(configuration + 4) = 256;
            int result = ((delegate* unmanaged<nint, nint, int>)Slot(LocalTimingNative.Profile.EngineConfigureSlot))(_engine, (nint)configuration);
            if (result != 0) throw new InvalidOperationException($"Private engine configuration failed: {result}.");
            int slot = Marshal.ReadInt32(_engine, LocalTimingNative.Profile.EngineSlot);
            if (slot is < 0 or >= 32) throw new InvalidOperationException("Invalid private engine slot.");
            _state = Marshal.ReadIntPtr(_library + LocalTimingNative.Profile.DseStates, slot * 8);
            if (_state == 0) throw new InvalidOperationException("Missing private engine state.");
            _nativeMutex = Marshal.ReadIntPtr(_state, LocalTimingNative.Profile.StateMutex);
            if (_nativeMutex == 0 || _nativeMutex != Marshal.ReadIntPtr(_library + LocalTimingNative.Profile.DseStartMutex))
                throw new NotSupportedException("Native start and planning mutex identity is unverified.");
            // Cold initialization belongs to the lifetime of this private
            // session, never to each candidate/window. It may allocate synth
            // state, but no PCM/frame advancement is requested.
            result = ((delegate* unmanaged<nint, int>)Slot(LocalTimingNative.Profile.EngineStartSlot))(_engine);
            if (result != 0) throw new InvalidOperationException($"Private engine start failed: {result}.");
            _nativeRunning = true;
            InitializationMs = watch.Elapsed.TotalMilliseconds;
        }
        catch { Dispose(); throw; }
    }

    private nint Slot(int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(_engine), index * 8);

    internal void Begin(CancellationToken cancellation = default, int waitBudgetMs = 50)
    {
        ObjectDisposedException.ThrowIf(_engine == 0, this);
        if (_started) throw new InvalidOperationException("Private timing session is already active.");
        // Reset only metadata. Re-running Start here would add a one-second
        // native lock wait, or starve the renderer if held inside this lease.
        var timer = Stopwatch.StartNew();
        uint wait;
        do
        {
            cancellation.ThrowIfCancellationRequested();
            wait = WaitForSingleObject(_nativeMutex, (uint)Math.Min(5, waitBudgetMs));
            if (wait == 0x102 && timer.ElapsedMilliseconds >= waitBudgetMs)
                throw new TimeoutException("Native timing planner is busy or unavailable.");
        } while (wait == 0x102);
        if (wait == 0x80)
        {
            ReleaseMutex(_nativeMutex);
            throw new InvalidOperationException("Native timing mutex was abandoned.");
        }
        if (wait != 0) throw new InvalidOperationException("Native timing mutex wait failed.");
        _queryMutex = _nativeMutex;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!_nativeRunning || CurrentFrame != 0)
                throw new InvalidOperationException("Private timing session advanced synthesis frames.");
            ((delegate* unmanaged<nint, void>)(_library + LocalTimingNative.Profile.DseScheduleClear))(Marshal.ReadIntPtr(_state, LocalTimingNative.Profile.StateSchedule));
            ((delegate* unmanaged<nint, void>)(_library + LocalTimingNative.Profile.DseParserReset))(_state + LocalTimingNative.Profile.StateParser);
            _started = true;
        }
        catch
        {
            ReleaseMutex(_queryMutex);
            _queryMutex = 0;
            throw;
        }
    }

    internal void End()
    {
        if (!_started) return;
        try
        {
            if (CurrentFrame != 0) throw new InvalidOperationException("Private timing session advanced synthesis frames.");
        }
        finally
        {
            _started = false;
            if (_queryMutex != 0) ReleaseMutex(_queryMutex);
            _queryMutex = 0;
        }
    }

    internal void Plan(int frame, byte[] bytes)
    {
        if (!_started || _queryMutex == 0) throw new InvalidOperationException("Timing planner is not locked.");
        if (frame < 0 || bytes.Length % 8 != 0 || bytes.Length > 32768)
            throw new ArgumentOutOfRangeException(nameof(frame));
        fixed (byte* payload = bytes)
        {
            long* input = stackalloc long[2];
            input[0] = bytes.Length / 8;
            input[1] = (long)payload;
            ((delegate* unmanaged<nint, int, nint, void>)(_library + LocalTimingNative.Profile.DseParse))(
                _state + LocalTimingNative.Profile.StateParser, frame, (nint)input);
        }
    }

    internal byte[] CreateBoundaryScore()
    {
        if (!_started || _queryMutex == 0) throw new InvalidOperationException("Timing planner is not locked.");
        nint schedule = Marshal.ReadIntPtr(_state, LocalTimingNative.Profile.StateSchedule);
        int end = ((delegate* unmanaged<nint, int>)(_library + LocalTimingNative.Profile.DseScheduleEnd))(schedule);
        if (end > Marshal.ReadInt32(schedule, LocalTimingNative.Profile.ScheduleCapacity)) throw new InvalidOperationException("Invalid schedule capacity.");
        int count = end - 3087 + 2;
        if (count is < 1 or > 4096) throw new InvalidOperationException("Boundary score exceeds local bound.");
        byte[] data = new byte[count * LocalTimingNative.Profile.ScoreStride];
        fixed (byte* buffer = data)
        for (int frame = 0; frame < count; frame++)
        {
            int nativeFrame = frame + 3087;
            if (nativeFrame >= end) continue;
            nint entry = ((delegate* unmanaged<nint, int, nint>)(_library + LocalTimingNative.Profile.DseScheduleRow))(schedule, nativeFrame);
            nint row = Marshal.ReadIntPtr(entry);
            if (row == 0 || Marshal.ReadInt32(row, LocalTimingNative.Profile.RowDictionary) < 0) continue;
            int begin = Marshal.ReadInt32(row, LocalTimingNative.Profile.RowBegin), finish = Marshal.ReadInt32(row, LocalTimingNative.Profile.RowEnd);
            int from = Marshal.ReadInt32(row, LocalTimingNative.Profile.RowFrom), to = Marshal.ReadInt32(row, LocalTimingNative.Profile.RowTo);
            if (from is < 0 or > 255 || to is < -1 or > 255)
                throw new InvalidOperationException("Invalid planned phoneme index.");
            byte* score = buffer + frame * LocalTimingNative.Profile.ScoreStride;
            *(int*)(score + LocalTimingNative.Profile.ScoreLeftDistance) = nativeFrame - begin;
            *(int*)(score + LocalTimingNative.Profile.ScoreRightDistance) = finish - nativeFrame;
            *(int*)(score + LocalTimingNative.Profile.ScoreLeftTransition) = to == -1 ? Marshal.ReadInt32(row, LocalTimingNative.Profile.RowSustainSplit) : Marshal.ReadInt32(row, LocalTimingNative.Profile.RowSplit);
            *(int*)(score + LocalTimingNative.Profile.ScoreRightTransition) = to == -1 ? 0 : finish - begin - Marshal.ReadInt32(row, LocalTimingNative.Profile.RowSplit);
            nint unit = ((delegate* unmanaged<nint, int, int, nint>)(_library + LocalTimingNative.Profile.DseDictionary))(
                Marshal.ReadIntPtr(_state), Marshal.ReadInt32(row, LocalTimingNative.Profile.RowDictionary), Marshal.ReadInt32(row, LocalTimingNative.Profile.RowSinger));
            if (unit == 0) throw new InvalidOperationException("Missing phoneme dictionary.");
            nint table = Marshal.ReadIntPtr(unit, LocalTimingNative.Profile.DictionaryPhones);
            if (table == 0) throw new InvalidOperationException("Missing phoneme table.");
            *(nint*)(score + LocalTimingNative.Profile.ScoreFromPhone) = ((delegate* unmanaged<nint, byte, nint>)(_library + LocalTimingNative.Profile.DsePhoneName))(table, unchecked((byte)from));
            if (to != -1)
            {
                *(nint*)(score + LocalTimingNative.Profile.ScoreToPhone) = ((delegate* unmanaged<nint, byte, nint>)(_library + LocalTimingNative.Profile.DsePhoneName))(table, unchecked((byte)to));
                byte left = Marshal.ReadByte(row, LocalTimingNative.Profile.RowLeftFlag), right = Marshal.ReadByte(row, LocalTimingNative.Profile.RowRightFlag);
                score[LocalTimingNative.Profile.ScoreFromFlag] = (byte)(left != 0 && right == 0 ? 1 : 0);
                score[LocalTimingNative.Profile.ScoreToFlag] = (byte)(left == 0 && right != 0 ? 1 : 0);
            }
        }
        return data;
    }

    internal void PlanNote(int frame, byte[] bytes, float coefficient)
    {
        if (!_started || _queryMutex == 0) throw new InvalidOperationException("Timing planner is not locked.");
        NativeTimingNoteInput.Feed(_state, frame, bytes, coefficient);
    }

    internal void VerifyPhonemeBoundary(int frame, string? left, string? right)
    {
        if (!_started || _queryMutex == 0) throw new InvalidOperationException("Timing planner is not locked.");
        nint schedule = Marshal.ReadIntPtr(_state, LocalTimingNative.Profile.StateSchedule);
        int end = ((delegate* unmanaged<nint, int>)(_library + LocalTimingNative.Profile.DseScheduleEnd))(schedule);
        TimingBoundaryEvidence? before = null, after = null;
        for (int at = Math.Max(3087, frame - 1); at <= frame && at < end; at++)
        {
            nint entry = ((delegate* unmanaged<nint, int, nint>)(_library + LocalTimingNative.Profile.DseScheduleRow))(schedule, at);
            nint row = Marshal.ReadIntPtr(entry);
            if (row == 0 || Marshal.ReadInt32(row, LocalTimingNative.Profile.RowDictionary) < 0) continue;
            int from = Marshal.ReadInt32(row,LocalTimingNative.Profile.RowFrom), to = Marshal.ReadInt32(row,LocalTimingNative.Profile.RowTo);
            if (from is < 0 or > 255 || to is < -1 or > 255) continue;
            int split = checked(Marshal.ReadInt32(row, LocalTimingNative.Profile.RowBegin) + Marshal.ReadInt32(row, to == -1 ? LocalTimingNative.Profile.RowSustainSplit : LocalTimingNative.Profile.RowSplit));
            nint unit = ((delegate* unmanaged<nint,int,int,nint>)(_library+LocalTimingNative.Profile.DseDictionary))(
                Marshal.ReadIntPtr(_state),Marshal.ReadInt32(row,LocalTimingNative.Profile.RowDictionary),Marshal.ReadInt32(row,LocalTimingNative.Profile.RowSinger));
            if (unit == 0) continue;
            nint table = Marshal.ReadIntPtr(unit,LocalTimingNative.Profile.DictionaryPhones);
            string? Phone(int index) => index < 0 ? null : Marshal.PtrToStringAnsi(
                ((delegate* unmanaged<nint,byte,nint>)(_library+LocalTimingNative.Profile.DsePhoneName))(table,checked((byte)index)));
            string? fromPhone = Phone(from), toPhone = Phone(to);
            var evidence = new TimingBoundaryEvidence(Marshal.ReadInt32(row, LocalTimingNative.Profile.RowBegin),Marshal.ReadInt32(row,LocalTimingNative.Profile.RowEnd),split,fromPhone,toPhone);
            if (at < frame) before = evidence;
            else after = evidence;
            if (evidence.MatchesSplit(frame,left,right)) return;
        }
        if (before is { } previous && after is { } next &&
            TimingBoundaryEvidence.MatchesJoin(frame,left,right,previous,next)) return;
        string Describe(TimingBoundaryEvidence? value) => value is { } e ? $"{e.Begin}..{e.End}:{e.From}/{e.To}@{e.Split}" : "unavailable";
        throw new NotSupportedException($"Native sample mapping did not produce the requested phoneme boundary at {frame} ({left}/{right}); observed {Describe(before)};{Describe(after)}.");
    }

    public void Dispose()
    {
        if (_engine == 0) return;
        try
        {
            End();
            if (_nativeRunning) ((delegate* unmanaged<nint, int>)Slot(LocalTimingNative.Profile.EngineStopSlot))(_engine);
        }
        finally
        {
            _nativeRunning = false;
            ((delegate* unmanaged<nint, int>)Slot(LocalTimingNative.Profile.EngineDestroySlot))(_engine);
            _engine = 0;
            _state = 0;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(nint handle);
}
