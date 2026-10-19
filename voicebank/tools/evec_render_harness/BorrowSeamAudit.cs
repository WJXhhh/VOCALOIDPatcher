using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Observe native phase concatenation, without changing its gate, phase,
    // gain, schedule or caches. A successful native call is not a CV morph.
    private static int ProbeBorrowSeamAudit(WIVSMSequenceManager manager, TimingCoefficientHook capture,
        List<BorrowDonor> donors, string directory)
    {
        using var seam = new BorrowSeamAudit();
        using var borrow = new ExplicitBorrowReader();
        var results = new List<object>();
        foreach (string phones in new[] { "s ei", "s 7", "s a" })
        {
            using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 4800);
            Require(fixture.Part.SetVoiceBankID("BY98KLLZTDYH7YEE"), "Missing seam audit bank.");
            var note = fixture.Notes[0]; fixture.Part.MoveNote(new VSMRelTick(960), note);
            note.SetNoteNumber(62); note.SetPhonemes(phones, true, 4);
            fixture.Sequence.InterpolatedMasterVolume();
            string? baseline = null; int[]? bounds = null;
            foreach (string mode in phones == "s ei" ? new[] { "warmup", "baseline", "7-mid", "a-mid", "restore" } : new[] { "warmup", "baseline", "restore" })
            {
                note.NoteVelocity = 63; fixture.Sequence.Commit(false);
                note.NoteVelocity = 64; fixture.Sequence.Commit(false);
                seam.Reset(); borrow.Reset(mode == "7-mid" ? donors[0] : mode == "a-mid" ? donors[1] : null, "mid");
                capture.Reset(-1, false, null);
                capture.BeforeSynthesis = state => BorrowSeamAudit.State = state;
                capture.AfterOutput = (_, _) => BorrowSeamAudit.State = 0;
                var rows = new Dictionary<int, DurationRow>(); var ids = new Dictionary<nint, int>(); int consumed = 3087;
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    if (cursor <= consumed) return;
                    foreach (var row in ReadDurationRows(state, ids, consumed, cursor)) rows[row.Begin] = row;
                    consumed = cursor;
                };
                string name = phones.Replace(' ', '-') + "-" + mode;
                try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, name + ".wav")); capture.Check(); seam.Check(); borrow.Check(); }
                finally { capture.BeforeSynthesis = null; capture.AfterOutput = null; capture.AfterStep = null; BorrowSeamAudit.State = 0; }
                string pcm = TimingPcmHash(Path.Combine(directory, name + ".wav"));
                int[] actualBounds = NativeTimingPositions.Read(note, false);
                if (mode == "baseline") { baseline = pcm; bounds = actualBounds; }
                if (mode != "warmup") Require(actualBounds.SequenceEqual(bounds!), "Seam audit moved timing.");
                if (mode == "restore") Require(pcm == baseline, "Seam audit failed to restore.");
                File.WriteAllText(Path.Combine(directory, name + "-phase.json"), JsonSerializer.Serialize(seam.Calls));
                File.WriteAllText(Path.Combine(directory, name + "-prepared.json"), JsonSerializer.Serialize(seam.Prepared));
                results.Add(new { phones, mode, name, pcm, bounds = actualBounds, calls = seam.Calls.Count,
                    seam.PrepareCalls, seam.OuterCalls, seam.OuterWithDictionary,
                    reads = borrow.Calls, rows = rows.Values.OrderBy(r => r.Begin).ToArray() });
                File.WriteAllText(Path.Combine(directory, "seam-audit.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("borrow.seam-audit=" + name);
            }
        }
        return 0;
    }

    private sealed unsafe class BorrowSeamAudit : IDisposable
    {
        [ThreadStatic] internal static nint State;
        private static BorrowSeamAudit? _active;
        private readonly DurationPostCallHook _hook;
        private readonly DurationPostCallHook _prepare;
        private readonly DurationPostCallHook _outer;
        private readonly DurationPostCallHook _join;
        private readonly HashSet<string> _seen = new();
        private Exception? _error;
        private Call? _pending;
        internal sealed record CacheFrame(ulong Flags, int PitchBits, int Harmonics, bool HasPhaseEnvelope);
        internal sealed record Call(int Time, int Join, int From, int To, int PreviousIdentity, int CurrentIdentity,
            int PreviousRight, int CurrentLeft, CacheFrame Left, CacheFrame Right, string PhaseBefore,
            bool Succeeded = false, string PhaseAfter = "", string Function = "phase",
            int GainBeforeBits = 0, int GainAfterBits = 0);
        internal List<Call> Calls { get; } = new();
        internal sealed record Snapshot(int ObservedAt, int Join, int From, int To, byte Enabled, byte Prepared,
            bool HasPhaseDictionary, CacheFrame? Left, CacheFrame? Right);
        internal List<Snapshot> Prepared { get; } = new();
        internal int PrepareCalls { get; private set; }
        internal int OuterCalls { get; private set; }
        internal int OuterWithDictionary { get; private set; }
        internal BorrowSeamAudit()
        {
            if (_active != null) throw new InvalidOperationException("Seam audit already active.");
            _active = this;
            _hook = new(0x1cb830, [0x48, 0x89, 0x5c, 0x24, 0x20, 0x56, 0x57, 0x41, 0x56,
                0x48, 0x81, 0xec, 0xc0, 0, 0, 0],
                (nint)(delegate* unmanaged<nint, void>)&After, (nint)(delegate* unmanaged<nint, void>)&Before);
            _prepare = new(0x16af70, [0x40, 0x55, 0x56, 0x48, 0x8d, 0x6c, 0x24, 0xb1,
                0x48, 0x81, 0xec, 0xb8, 0, 0, 0], (nint)(delegate* unmanaged<nint, void>)&AfterPrepare);
            _outer = new(0x168600, [0x48, 0x89, 0x5c, 0x24, 8, 0x48, 0x89, 0x7c, 0x24, 0x10,
                0x55, 0x48, 0x8b, 0xec, 0x48, 0x81, 0xec, 0x80, 0, 0, 0], (nint)(delegate* unmanaged<nint, void>)&AfterOuter);
            _join = new(0x1ca5d0, [0x48, 0x8b, 0xc4, 0x48, 0x89, 0x58, 0x18, 0x55,
                0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56],
                (nint)(delegate* unmanaged<nint, void>)&After, (nint)(delegate* unmanaged<nint, void>)&BeforeJoin);
        }
        internal void Reset() { Calls.Clear(); Prepared.Clear(); _seen.Clear(); PrepareCalls = OuterCalls = OuterWithDictionary = 0; _pending = null; _error = null; }
        internal void Check() { if (_error != null || _pending != null) throw new InvalidOperationException("Seam observation failed.", _error); }
        private static CacheFrame Read(nint frame)
        {
            Require(frame != 0, "Missing native seam cache.");
            return new(unchecked((ulong)Marshal.ReadInt64(frame, 0x150)), Marshal.ReadInt32(frame, 0x1d0),
                Marshal.ReadInt32(frame, 0x158), Marshal.ReadIntPtr(frame, 0x218) != 0);
        }
        private static string Phase(nint frame)
        {
            int count = Marshal.ReadInt32(frame, 0x158);
            Require(count is >= 0 and <= 4097, "Unbounded seam phase vector.");
            nint data = Marshal.ReadIntPtr(frame, 0x170);
            Require(count == 0 || data != 0, "Missing seam phase vector.");
            return Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)data, count * 4)));
        }
        private static string Analysis(nint frame)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(new ReadOnlySpan<byte>((void*)(frame + 0x150), 8));
            hash.AppendData(new ReadOnlySpan<byte>((void*)(frame + 0x1d0), 4));
            hash.AppendData(new ReadOnlySpan<byte>((void*)(frame + 0x270), 12));
            int count = Marshal.ReadInt32(frame, 0x158);
            Require(count is >= 0 and <= 4097, "Unbounded seam harmonic vector.");
            foreach (int offset in new[] { 0x160, 0x168, 0x170 })
            {
                nint data = Marshal.ReadIntPtr(frame, offset);
                Require(count == 0 || data != 0, "Missing seam harmonic vector.");
                hash.AppendData(new ReadOnlySpan<byte>((void*)data, count * 4));
            }
            int formants = Marshal.ReadInt32(frame, 0x260);
            Require(formants is >= 0 and <= 256, "Unbounded seam formants.");
            for (int i = 0; i < formants; i++)
            {
                nint node = Marshal.ReadIntPtr(Marshal.ReadIntPtr(frame, 0x268), i * 8);
                hash.AppendData(new ReadOnlySpan<byte>((void*)(node + 0x140), 32));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        [UnmanagedCallersOnly]
        private static void AfterOuter(nint context)
        {
            var owner = _active!;
            try
            {
                owner.OuterCalls++;
                if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(context), 0x7f8) != 0) owner.OuterWithDictionary++;
            }
            catch (Exception error) { owner._error ??= error; }
        }
        [UnmanagedCallersOnly]
        private static void AfterPrepare(nint context)
        {
            var owner = _active!;
            try
            {
                owner.PrepareCalls++;
                nint state = Marshal.ReadIntPtr(context);
                int current = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                if (current is < 3300 or > 3450) return;
                nint schedule = Marshal.ReadIntPtr(state, LocalTimingNative.Profile.StateSchedule);
                int end = ((delegate* unmanaged<nint, int>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseScheduleEnd))(schedule);
                foreach (int at in new[] { 3322, 3323, 3339, 3343, 3373, 3376, 3388 })
                {
                    if (at >= end) continue;
                    nint entry = ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseScheduleRow))(schedule, at);
                    nint row = Marshal.ReadIntPtr(entry);
                    if (row == 0 || Marshal.ReadInt32(row) != at) continue;
                    nint left = Marshal.ReadIntPtr(row, 0xa8), right = Marshal.ReadIntPtr(row, 0xb0);
                    var snapshot = new Snapshot(current, at, Marshal.ReadInt32(row, 0x10), Marshal.ReadInt32(row, 0x14),
                        Marshal.ReadByte(row, 0x98), Marshal.ReadByte(row, 0xe4), Marshal.ReadIntPtr(state, 0x7f8) != 0,
                        left == 0 ? null : Read(left), right == 0 ? null : Read(right));
                    string identity = JsonSerializer.Serialize(snapshot with { ObservedAt = 0 });
                    if (owner._seen.Add(identity)) owner.Prepared.Add(snapshot);
                    Require(owner.Prepared.Count < 128, "Too many seam cache changes.");
                }
            }
            catch (Exception error) { owner._error ??= error; }
        }
        [UnmanagedCallersOnly]
        private static void Before(nint context)
            => Observe(context, "phase");
        [UnmanagedCallersOnly]
        private static void BeforeJoin(nint context)
            => Observe(context, "spectral");
        private static void Observe(nint context, string function)
        {
            var owner = _active!;
            try
            {
                Require(owner._pending == null, "Reentrant phase observation.");
                int time = Marshal.ReadInt32(context, 16), join = Marshal.ReadInt32(context, 24);
                if (join is < 3300 or > 3450 || Math.Abs(time - join) > 18) return;
                Require(State != 0 && owner.Calls.Count < 4096, "Missing/bounded phase observation state.");
                nint seam = Marshal.ReadIntPtr(context), row = seam - 0xa0;
                owner._pending = new(time, join, Marshal.ReadInt32(row, 0x10), Marshal.ReadInt32(row, 0x14),
                    Marshal.ReadInt32(seam, 0x48), Marshal.ReadInt32(seam, 0x4c),
                    Marshal.ReadInt32(seam, 0x70), Marshal.ReadInt32(seam, 0x74),
                    Read(Marshal.ReadIntPtr(seam, 8)), Read(Marshal.ReadIntPtr(seam, 0x10)),
                    function == "phase" ? Phase(Marshal.ReadIntPtr(context, 8)) : Analysis(Marshal.ReadIntPtr(context, 8)),
                    Function: function, GainBeforeBits: function == "spectral" ? Marshal.ReadInt32(Marshal.ReadIntPtr(context, 72)) : 0);
            }
            catch (Exception error) { owner._error ??= error; }
        }
        [UnmanagedCallersOnly]
        private static void After(nint context)
        {
            var owner = _active!;
            try
            {
                if (owner._pending is not { } pending) return;
                owner.Calls.Add(pending with { Succeeded = Marshal.ReadByte(context, 0x88) != 0,
                    PhaseAfter = pending.Function == "phase" ? Phase(Marshal.ReadIntPtr(context, 8)) : Analysis(Marshal.ReadIntPtr(context, 8)),
                    GainAfterBits = pending.Function == "spectral" ? Marshal.ReadInt32(Marshal.ReadIntPtr(context, 72)) : 0 });
                owner._pending = null;
            }
            catch (Exception error) { owner._error ??= error; owner._pending = null; }
        }
        public void Dispose() { _join.Dispose(); _outer.Dispose(); _prepare.Dispose(); _hook.Dispose(); _active = null; }
    }
}
