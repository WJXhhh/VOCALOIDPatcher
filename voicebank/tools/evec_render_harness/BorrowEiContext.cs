using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Research-only source views. Preserve the target score/schedule while
    // comparing its silence-start ei with ei recorded after a consonant.
    private static int ProbeBorrowEiContext(WIVSMSequenceManager manager, TimingCoefficientHook capture,
        List<BorrowDonor> donors, string directory)
    {
        using var reader = new BorrowEiContextReader();
        using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 4800);
        Require(fixture.Part.SetVoiceBankID("BY98KLLZTDYH7YEE"), "Missing ei context bank.");
        var note = fixture.Notes[0]; fixture.Part.MoveNote(new VSMRelTick(960), note);
        note.SetNoteNumber(62); note.SetPhonemes("s ei", true, 4);
        fixture.Sequence.InterpolatedMasterVolume();
        var cases = new List<(string Name, BorrowDonor? S, BorrowDonor? Ei, bool Tail)>
        { ("warmup", null, null, false), ("baseline", null, null, false), ("7-mid", donors[0], null, false) };
        foreach (var source in donors.Where(d => d.Vowel == "ei"))
        {
            string label = source.From.Replace('`', 'r');
            cases.Add(("7-mid-ei-" + label, donors[0], source, false));
            // This control may retain the donor consonant's phonetic character.
            // A preserved score label is not proof of an s pronunciation.
            cases.Add(("continuous-tail-" + label, source, source, true));
        }
        cases.Add(("7-mid-ei-ts-repeat", donors[0], donors.Single(d => d.From == "ts"), false));
        cases.Add(("restore", null, null, false));
        var results = new List<object>();
        string? baselineSchedule = null, baselineSources = null, baselinePcm = null, repeatedPcm = null;
        foreach (var test in cases)
        {
            note.NoteVelocity = 63; fixture.Sequence.Commit(false);
            note.NoteVelocity = 64; fixture.Sequence.Commit(false);
            reader.Reset(test.S, test.Ei, test.Tail);
            capture.Reset(-1, false, null);
            var rows = new Dictionary<int, DurationRow>(); var ids = new Dictionary<nint, int>(); int consumed = 3087;
            var scheduled = new Dictionary<int, TransitionSegment>();
            capture.AfterStep = state =>
            {
                int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                if (cursor <= consumed) return;
                foreach (var row in ReadDurationRows(state, ids, consumed, cursor)) rows[row.Begin] = row;
                foreach (var segment in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                {
                    if (scheduled.TryGetValue(segment.Begin, out var previous))
                    {
                        Require((previous with { Frames = [] }) == (segment with { Frames = [] }), "Context work item changed during consumption.");
                        scheduled[segment.Begin] = segment with { Frames = previous.Frames.AddRange(segment.Frames) };
                    }
                    else scheduled[segment.Begin] = segment;
                }
                consumed = cursor;
            };
            try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, test.Name + ".wav")); capture.Check(); reader.Check(); }
            finally { capture.AfterStep = null; }
            string schedule = JsonSerializer.Serialize(rows.Values.OrderBy(r => r.Begin));
            string sources = JsonSerializer.Serialize(scheduled.Values.OrderBy(s => s.Begin));
            Require(scheduled.Values.All(s => s.Frames.Length == s.End - s.Begin) &&
                scheduled.Values.Sum(s => s.Frames.Length) == 94, "Incomplete context schedule audit.");
            string pcm = TimingPcmHash(Path.Combine(directory, test.Name + ".wav"));
            int[] bounds = NativeTimingPositions.Read(note, false);
            Require(note.Phonemes == "s ei" && bounds.SequenceEqual(new[] { -25, -13, 477 }), "Context probe changed phonemes/timing.");
            if (test.Name == "baseline") { baselineSchedule = schedule; baselineSources = sources; baselinePcm = pcm; }
            if (test.Name == "7-mid-ei-ts") repeatedPcm = pcm;
            if (test.Name != "warmup") Require(schedule == baselineSchedule && sources == baselineSources, "Context probe changed schedule/materials/source map.");
            if (test.Name == "restore") Require(pcm == baselinePcm, "Context probe did not restore.");
            if (test.Name == "7-mid-ei-ts-repeat") Require(pcm == repeatedPcm, "Context probe did not repeat.");
            if (test.S != null) Require(reader.Calls.Any(r => r.Side == "s"), "Consonant source was not read.");
            if (test.Ei != null) Require(reader.Calls.Where(r => r.Side == "ei").Select(r => r.OutputFrame).ToHashSet()
                .SetEquals(Enumerable.Range(3343, 45)), "Context ei source was not fully consumed.");
            results.Add(new { test.Name, phones = note.Phonemes, bounds, pcm, scheduleUnchanged = schedule == baselineSchedule,
                originalSourceMapUnchanged = sources == baselineSources, scheduledFrameCount = 94,
                donorS = test.S is { } s ? s.From + "->" + s.Vowel : null,
                donorEi = test.Ei is { } ei ? ei.From + "->" + ei.Vowel : null,
                reads = reader.Calls.ToArray(), rows = rows.Values.OrderBy(r => r.Begin).ToArray(),
                humanPronunciationAccepted = false });
            File.WriteAllText(Path.Combine(directory, "ei-context.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("borrow.ei-context=" + test.Name);
        }
        return 0;
    }

    private sealed unsafe class BorrowEiContextReader : IDisposable
    {
        private static BorrowEiContextReader? _active;
        private readonly DurationPostCallHook _hook;
        private readonly nint _view = Marshal.AllocHGlobal(0x140);
        private BorrowDonor? _s, _ei;
        private bool _tail;
        private Exception? _error;
        internal sealed record Read(string Side, int OutputFrame, int OriginalSource, int DonorSource,
            string DonorFrom, string DonorTo, int First, int Last);
        internal List<Read> Calls { get; } = new();
        internal BorrowEiContextReader()
        {
            if (_active != null) throw new InvalidOperationException("Context reader already active.");
            _active = this;
            _hook = new(0x1757a0, [0x40, 0x53, 0x55, 0x57, 0x41, 0x57, 0x48, 0x81, 0xec, 0xa8, 0, 0, 0, 0x49, 0x8b, 0xd9],
                (nint)(delegate* unmanaged<nint, void>)&After, (nint)(delegate* unmanaged<nint, void>)&Before);
        }
        internal void Reset(BorrowDonor? s, BorrowDonor? ei, bool tail)
        { _s = s; _ei = ei; _tail = tail; Calls.Clear(); _error = null; }
        internal void Check() { if (_error != null) throw new InvalidOperationException("Context source substitution failed.", _error); }
        [UnmanagedCallersOnly] private static void After(nint context) { }
        [UnmanagedCallersOnly]
        private static void Before(nint context)
        {
            var owner = _active!;
            try
            {
                nint row = Marshal.ReadIntPtr(context, 8);
                if (row == 0 || Marshal.ReadInt32(row, 0x10) != 60) return;
                int frame = Marshal.ReadInt32(context, 48), original = Marshal.ReadInt32(context, 16);
                BorrowDonor? donor; int first, last, source; string side;
                if (Marshal.ReadInt32(row) == 3339 && Marshal.ReadInt32(row, 4) == 3343 &&
                    Marshal.ReadInt32(row, 0x14) == 51 && frame is >= 3341 and < 3343 && owner._s is { } consonant)
                {
                    donor = consonant; first = donor.First; last = donor.Last; side = "s";
                    source = owner._tail ? last - (3342 - frame) : (first + last) / 2 + frame - 3341;
                }
                else if (Marshal.ReadInt32(row) == 3343 && Marshal.ReadInt32(row, 4) == 3388 &&
                    Marshal.ReadInt32(row, 0x14) == 4 && frame is >= 3343 and < 3388 && owner._ei is { } vowel)
                {
                    donor = vowel; first = donor.RightFirst; last = donor.RightLast; side = "ei";
                    Require(original is >= 0 and <= 39, "Unreviewed target ei source.");
                    source = first + (int)Math.Round(original * (last - first) / 39d);
                }
                else return;
                Require(donor.Engine.CurrentFrame == 0 && first >= 0 && source >= first && source <= last &&
                    owner.Calls.Count < 512, "Context borrowing left the reviewed donor range.");
                Marshal.Copy(donor.View, 0, owner._view, donor.View.Length);
                Marshal.WriteIntPtr(context, 8, owner._view); Marshal.WriteInt32(context, 16, source);
                owner.Calls.Add(new(side, frame, original, source, donor.From, donor.Vowel, first, last));
            }
            catch (Exception error) { owner._error ??= error; }
        }
        public void Dispose() { _hook.Dispose(); Marshal.FreeHGlobal(_view); _active = null; }
    }
}
