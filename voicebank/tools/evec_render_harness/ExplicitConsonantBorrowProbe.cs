using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Disposable native experiment, deliberately outside the production ABI,
    // project model and UI. The donor's private planner remains alive, with its
    // schedule uncleared, until all consumers have finished. Source views are
    // non-owning copies used synchronously by the frame reader only.
    private static unsafe int ProbeExplicitConsonantBorrow(WIVSMSequenceManager manager, DSEManager dse, string directory, bool audibility = false, bool fullProcess = false, bool sourceAudit = false, bool seamAudit = false, bool contextBorrow = false)
    {
        string library = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        if (audibility || fullProcess || sourceAudit || seamAudit || contextBorrow) { NativePhonemeTiming.Initialize(library); NativeSampledTiming.Initialize(library); }
        else NativeTransitionGain.Initialize(library);
        using var capture = new TimingCoefficientHook();
        var results = new List<object>();
        void Record(object row)
        {
            results.Add(row);
            File.WriteAllText(Path.Combine(directory, "explicit-borrow.json"), JsonSerializer.Serialize(results,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        Record(new { kind = "metadata", sdk = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(),
            profile = LocalTimingNative.Profile.Id, bank = "BY98KLLZTDYH7YEE", pitch = 62, duration = 480,
            velocity = 64, target = "s ei", scope = "independent-process-only" });
        var donors = new List<BorrowDonor>();
        try
        {
            (string From, string Vowel)[] donorPlans = contextBorrow ? [("s", "7"), ("s", "a"), ("ts", "ei"), ("s`", "ei"), ("f", "ei")] : [("s", "7"), ("s", "a")];
            foreach (var (from, vowel) in donorPlans)
            {
                using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 4800);
                Configure(fixture.Part, fixture.Notes[0], fixture.Sequence, from + " " + vowel);
                capture.Reset(-1, false, null);
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, "donor-" + from.Replace('`', 'r') + "-" + vowel + ".wav"));
                capture.Check();
                var native = new NativeConsonantTimingEngine((nint)dse);
                try
                {
                    nint state = (nint)typeof(NativeConsonantTimingEngine)
                        .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(native)!;
                    native.Begin();
                    try { foreach (var block in capture.Inputs) native.Plan(block.Frame, block.Bytes); }
                    finally { native.End(); }
                    var segment = NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, 3087, 4000)
                        .Single(s => s.From == from && s.To == vowel);
                    nint schedule = Marshal.ReadIntPtr(state, LocalTimingNative.Profile.StateSchedule);
                    nint entry = ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + LocalTimingNative.Profile.DseScheduleRow))(schedule, segment.Begin);
                    nint row = Marshal.ReadIntPtr(entry);
                    byte[] view = new ReadOnlySpan<byte>((void*)row, 0x140).ToArray();
                    var span = segment.MaterialCoverage?.Left ?? throw new InvalidOperationException("Missing donor coverage.");
                    Require(!segment.Reverse && span.First >= 0 && span.Last > span.First, "Unsupported donor direction/stage.");
                    var rightSpan = segment.MaterialCoverage!.Right ?? throw new InvalidOperationException("Missing donor right coverage.");
                    donors.Add(new(native, vowel, view, span.First, span.Last, from, rightSpan.First, rightSpan.Last));
                    Record(new { kind = "donor", vowel, segment.From, segment.To, segment.Stage,
                        frames = Marshal.ReadInt32(Marshal.ReadIntPtr(row, 0x20), 0x1b0), first = span.First, last = span.Last,
                        plannerCurrentFrame = native.CurrentFrame });
                }
                catch { native.Dispose(); throw; }
            }
            if (sourceAudit) return ProbeBorrowSourceAudit(manager, capture, donors, directory);
            if (seamAudit) return ProbeBorrowSeamAudit(manager, capture, donors, directory);
            if (contextBorrow) return ProbeBorrowEiContext(manager, capture, donors, directory);
            using var target = CreateStandaloneFixture(manager, 1, 480, 0, 4800);
            Configure(target.Part, target.Notes[0], target.Sequence, "s ei");
            using var borrow = new ExplicitBorrowReader();
            using var gain = audibility || fullProcess ? new TransitionGainHook() : null;
            string activeMode = "";
            var grainExperiment = fullProcess ? new BorrowSynthesisExperiment(directory) : null;
            if (fullProcess && gain != null) gain.BeforeSynthesisFrame = (synth, analysis, time, lane, nativeGain) =>
            {
                if (time is < 3341 or > 3342 || lane != "primary") return;
                grainExperiment!.Visit(activeMode, synth, analysis, time, nativeGain);
                if (time != 3341) return;
                int hop = Marshal.ReadInt32(synth, 0x60), fft = Marshal.ReadInt32(synth, 0x64);
                Require(hop == 256 && fft is >= 512 and <= 8192, "Unreviewed native synthesis geometry.");
                float[] window = new ReadOnlySpan<float>((void*)Marshal.ReadIntPtr(synth, 0x70), hop * 2).ToArray();
                File.WriteAllText(Path.Combine(directory, "native-synthesis-geometry.json"), JsonSerializer.Serialize(new {
                    hop, fft, mode = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(synth, 0x5c)),
                    overlapMode = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(Marshal.ReadIntPtr(synth, 0x330), 0x1a23c)),
                    bins = Marshal.ReadInt32(analysis, 0x1a8), flags = Marshal.ReadInt64(analysis, 0x150), window }));
            };
            if (fullProcess && gain != null)
            {
                foreach (var source in donors)
                {
                    using var donorFixture = CreateStandaloneFixture(manager, 1, 480, 0, 4800);
                    Configure(donorFixture.Part, donorFixture.Notes[0], donorFixture.Sequence, "s " + source.Vowel);
                    int begin = BitConverter.ToInt32(source.View, 0), end = BitConverter.ToInt32(source.View, 4),
                        split = begin + BitConverter.ToInt32(source.View, 8);
                    var sourceMap = new SortedDictionary<int, TransitionFrame>();
                    var hashes = new Dictionary<string, string>();
                    foreach (string pass in new[] { "warmup", "reference", "muted", "restored" })
                    {
                        donorFixture.Notes[0].NoteVelocity = 63; donorFixture.Sequence.Commit(false);
                        donorFixture.Notes[0].NoteVelocity = 64; donorFixture.Sequence.Commit(false);
                        gain.Reset(new(begin, end, BitConverter.ToInt32(source.View, 0x10), BitConverter.ToInt32(source.View, 0x14),
                            BitConverter.ToInt32(source.View, 0x28), 0, 4, 6, new(0, 0, 0, 0, 0, [])), 0, 0, pass == "muted" ? 0 : 1);
                        gain.GainFrameRange = (begin, split);
                        capture.Reset(-1, false, null); int consumed = 3087;
                        capture.BeforeSynthesis = state => TransitionGainHook.State = state;
                        capture.AfterOutput = (_, _) => TransitionGainHook.State = 0;
                        capture.AfterStep = state =>
                        {
                            int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                            if (cursor <= consumed) return;
                            if (pass == "reference")
                                foreach (var segment in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor)
                                    .Where(s => s.From == "s" && s.To == source.Vowel))
                                    foreach (var point in segment.Frames.Where(f => f.Frame < split)) sourceMap[point.Frame] = point;
                            consumed = cursor;
                        };
                        string file = "complete-source-" + source.Vowel + "-" + pass + ".wav";
                        try { RenderTransitionWithScore(donorFixture.Sequence, donorFixture.Part, Path.Combine(directory, file)); capture.Check(); gain.Check(); }
                        finally { capture.BeforeSynthesis = null; capture.AfterOutput = null; capture.AfterStep = null; TransitionGainHook.State = 0; }
                        hashes[pass] = TimingPcmHash(Path.Combine(directory, file));
                    }
                    Require(hashes["reference"] == hashes["restored"], "Complete donor source did not restore.");
                    Require(sourceMap.Count == split - begin, "Incomplete donor native source audit.");
                    Record(new { kind = "complete-source", source.Vowel, begin, split, end,
                        first = source.First, last = source.Last, sourceFrames = sourceMap.Values.ToArray(),
                        hashes, supportFirstSample = (begin - 3087) * 256, supportLastSample = (split - 3087) * 256 + 256 });
                }
            }
            var baselineSchedules = new Dictionary<int, string>();
            var baselineAllocations = new Dictionary<int, string>();
            var baselineBoundaries = new Dictionary<int, int[]>();
            ulong epoch = 86000;
            string[] modes = audibility ? ["warmup", "baseline", "7-mid", "7-mid-g4", "7-mid-g16", "7-mid-g64",
                "a-mid", "a-mid-g4", "a-mid-g16", "a-mid-g64", "7-mid-g16-repeat", "restore-final"] :
                ["warmup", "baseline", "7-full", "restore-7", "a-full", "restore-a", "7-tail", "a-tail", "7-mid", "a-mid", "7-full-repeat", "restore-final"];
            if (fullProcess) modes = ["warmup", "baseline", "7-mid", "7-reread", "7-pool", "a-mid", "a-reread", "a-pool", "7-pool-repeat", "restore-final"];
            if (fullProcess) modes = modes.Concat(["feed-control"]).ToArray();
            if (grainExperiment?.Modes is { Length: > 0 } grainModes)
                modes = modes.Concat(grainModes).Concat(["7-grain-identity-repeat", "restore-grains"]).ToArray();
            if (audibility) modes = modes.Concat(new[] { 6, 12, 22 }.SelectMany(frames => new[]
                { "baseline-s" + frames, "7-full-s" + frames, "a-full-s" + frames })).ToArray();
            foreach (var mode in modes)
            {
                activeMode = mode;
                bool seamAdvance = mode.EndsWith("-seam", StringComparison.Ordinal);
                int seamWrites = 0;
                int sFrames = mode.EndsWith("-s6", StringComparison.Ordinal) ? 6 : mode.EndsWith("-s12", StringComparison.Ordinal) ? 12 :
                    mode.EndsWith("-s22", StringComparison.Ordinal) ? 22 : 2;
                target.Notes[0].NoteVelocity = 63; target.Sequence.Commit(false);
                target.Notes[0].NoteVelocity = 64; target.Sequence.Commit(false);
                if (audibility)
                {
                    if (sFrames == 2) NativeSampledTiming.Remove((nint)target.Part);
                    else NativeSampledTiming.Publish(target.Part, ++epoch, new Dictionary<nint, float>(),
                        [new(0, 0), new(3341, 3343 - sFrames), new(3343, 3343), new(3433, 3433)]);
                }
                var donor = mode.StartsWith("7-", StringComparison.Ordinal) ? donors[0] : mode.StartsWith("a-", StringComparison.Ordinal) ? donors[1] : null;
                // A separate experiment keeps the original silent carrier and
                // adds the reconstructed donor contribution in the same FFT
                // window. Its seam preparation therefore stays native.
                if (mode.Contains("-layer", StringComparison.Ordinal)) donor = null;
                borrow.Reset(donor, mode.Contains("-mid", StringComparison.Ordinal) || mode.Contains("-grain", StringComparison.Ordinal) ? "mid" : mode.Contains("-pool", StringComparison.Ordinal) ? "pool" : mode.Split('-').Last());
                float multiplier = mode.Contains("-g4", StringComparison.Ordinal) ? 4 : mode.Contains("-g16", StringComparison.Ordinal) ? 16 :
                    mode.Contains("-g64", StringComparison.Ordinal) ? 64 : 1;
                if (gain != null)
                {
                    gain.Reset(new(3341 - sFrames, 3343, 60, 51, 0, 0, 4, 6, new(0, 4, 0, 0, 0, [])), 0, 0, multiplier);
                    gain.GainFrameRange = (3343 - sFrames, 3343);
                    capture.BeforeSynthesis = state => { TransitionGainHook.State = state; if (seamAdvance) seamWrites += AdvanceBorrowEiEntry(state); };
                    capture.AfterOutput = (_, _) => TransitionGainHook.State = 0;
                }
                capture.Reset(-1, seamAdvance || mode == "feed-control", null);
                int consumed = 3087; var rows = new Dictionary<int, DurationRow>(); var ids = new Dictionary<nint, int>();
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
                            Require((previous with { Frames = [] }) == (segment with { Frames = [] }), "Native work item changed during consumption.");
                            scheduled[segment.Begin] = segment with { Frames = previous.Frames.AddRange(segment.Frames) };
                        }
                        else scheduled[segment.Begin] = segment;
                    }
                    consumed = cursor;
                };
                string file = mode + ".wav"; var watch = Stopwatch.StartNew();
                try { RenderTransitionWithScore(target.Sequence, target.Part, Path.Combine(directory, file)); capture.Check(); borrow.Check(); gain?.Check(); }
                finally { capture.AfterStep = null; capture.BeforeSynthesis = null; capture.AfterOutput = null; TransitionGainHook.State = 0; }
                var boundaries = NativeTimingPositions.Read(target.Notes[0], false);
                Require(rows.Values.Count(r => r.From == 60 && r.To == 51) == 1, "Target consonant identity is ambiguous.");
                if (sFrames != 2)
                {
                    var route = NativeSampledTiming.ReadStatus((nint)target.Part);
                    Require(route.Scopes > 0 && route.Failures == 0, "Duration diagnostic timing route failed.");
                }
                Require(target.Notes[0].Phonemes == "s ei", "Target phonemes changed.");
                if (sFrames == 2) Require(boundaries.SequenceEqual(new[] { -25, -13, 477 }), "Target timing changed.");
                if (donor != null) Require(borrow.Calls.Count > 0, "Borrowing did not execute.");
                if (fullProcess && (mode.StartsWith("7-grain-", StringComparison.Ordinal) || mode.StartsWith("a-grain-", StringComparison.Ordinal)))
                    Require(grainExperiment!.Applied(mode) == 2, "Native grain contribution was not consumed twice.");
                string scheduleFingerprint = JsonSerializer.Serialize(scheduled.Values.OrderBy(s => s.Begin));
                string allocationFingerprint = JsonSerializer.Serialize(scheduled.Values.OrderBy(s => s.Begin)
                    .Select(s => s with { Frames = s.Frames.Select(f => f with { SourceBits = 0 }).ToImmutableArray() }));
                Require(scheduled.Values.All(s => s.Frames.Length == s.End - s.Begin), "Incomplete consumed-frame audit.");
                if (mode.StartsWith("baseline", StringComparison.Ordinal))
                { baselineSchedules[sFrames] = scheduleFingerprint; baselineAllocations[sFrames] = allocationFingerprint; baselineBoundaries[sFrames] = boundaries; }
                bool scheduleUnchanged = baselineSchedules.TryGetValue(sFrames, out var baselineSchedule) && baselineSchedule == scheduleFingerprint;
                bool allocationUnchanged = baselineAllocations.TryGetValue(sFrames, out var baselineAllocation) && baselineAllocation == allocationFingerprint;
                if (mode != "warmup") Require((seamAdvance ? allocationUnchanged : scheduleUnchanged) && boundaries.SequenceEqual(baselineBoundaries[sFrames]), "Target source map or schedule changed.");
                if (seamAdvance)
                {
                    Require(seamWrites > 0 && scheduled[3343].Frames.Length == 45 && scheduled[3343].Frames.All(f => f.SourceBits ==
                        BitConverter.SingleToInt32Bits(2 + (f.Frame - 3343) * 37f / 44)), "EI entry source advance was not consumed.");
                }
                Record(new { kind = "render", mode, file, milliseconds = watch.Elapsed.TotalMilliseconds,
                    sFrames, durationDiagnosticOnly = sFrames != 2,
                    phones = target.Notes[0].Phonemes, boundaries, actual = rows.Values.OrderBy(r => r.Begin).ToArray(),
                    scheduleUnchanged, allocationUnchanged, seamAdvance, seamWrites,
                    scheduledFrameCount = scheduled.Values.Sum(s => s.Frames.Length),
                    nativePacketsApplied = grainExperiment?.Applied(mode) ?? 0,
                    reads = borrow.Calls.ToArray(), analyses = borrow.Analyses.ToArray(), fullSource = borrow.FullSource.ToArray(), pcm = TimingPcmHash(Path.Combine(directory, file)) });
                if (gain != null) File.WriteAllText(Path.Combine(directory, mode + "-gain.json"), JsonSerializer.Serialize(gain.Trace));
                Console.WriteLine("borrow.render=" + mode);
            }
            if (audibility) NativeSampledTiming.Remove((nint)target.Part);
            Record(new { kind = "complete" }); return 0;
        }
        finally { foreach (var donor in donors) donor.Engine.Dispose(); }

        static void Configure(WIVSMMidiPart part, WIVSMNote note, WIVSMSequence sequence, string phones)
        {
            Require(part.SetVoiceBankID("BY98KLLZTDYH7YEE"), "Missing borrowing voicebank.");
            part.MoveNote(new VSMRelTick(960), note); note.SetNoteNumber(62); note.NoteVelocity = 64;
            note.SetPhonemes(phones, true, 4); sequence.InterpolatedMasterVolume(); sequence.Commit(false);
        }
    }

    private sealed record BorrowDonor(NativeConsonantTimingEngine Engine, string Vowel, byte[] View, int First, int Last,
        string From = "s", int RightFirst = -1, int RightLast = -1);

    private sealed unsafe partial class ExplicitBorrowReader : IDisposable
    {
        private static ExplicitBorrowReader? _active;
        private readonly DurationPostCallHook _hook;
        private readonly nint _view = Marshal.AllocHGlobal(0x140);
        private BorrowDonor? _donor;
        private string _mode = "full";
        private Exception? _error;
        internal sealed record Read(int Frame, int Begin, int End, int Source, int OriginalSource, int From, int To);
        // Bit 9 is retained as a raw format flag. It is not a reliable voiced /
        // unvoiced classifier (native analysis can retain harmonic storage for s).
        internal sealed record Analysis(int Frame, int Source, bool PitchFlag, int PitchBits, int Harmonics, int Bins, double SpectrumRms);
        internal List<Read> Calls { get; } = new();
        internal List<Analysis> Analyses { get; } = new();
        internal ExplicitBorrowReader()
        {
            if (_active != null) throw new InvalidOperationException("Borrow reader already active.");
            _active = this;
            _hook = new(0x1757a0, [0x40, 0x53, 0x55, 0x57, 0x41, 0x57, 0x48, 0x81, 0xec, 0xa8, 0, 0, 0, 0x49, 0x8b, 0xd9],
                (nint)(delegate* unmanaged<nint, void>)&After, (nint)(delegate* unmanaged<nint, void>)&Before);
        }
        internal void Reset(BorrowDonor? donor, string mode) { _donor = donor; _mode = mode; _error = null; Calls.Clear(); Analyses.Clear(); FullSource.Clear(); }
        internal void Check() { if (_error != null) throw new InvalidOperationException("Borrow reader failed.", _error); }
        [UnmanagedCallersOnly]
        private static void After(nint context)
        {
            var owner = _active!;
            try
            {
                if (Marshal.ReadIntPtr(context, 8) != owner._view) return;
                if (owner._mode is "pool" or "reread") owner.PoolSource(context);
                nint analysis = Marshal.ReadIntPtr(context, 24);
                int bins = Marshal.ReadInt32(analysis, 0x1a8);
                Require(bins is > 0 and <= 4097, "Borrow spectrum bound exceeded.");
                nint spectrum = Marshal.ReadIntPtr(analysis, 0x1b0); double energy = 0;
                for (int i = 0; i < bins; i++) { double value = ((float*)spectrum)[i]; energy += value * value; }
                owner.Analyses.Add(new(Marshal.ReadInt32(context, 48), Marshal.ReadInt32(context, 16),
                    ((ulong)Marshal.ReadInt64(analysis, 0x150) & 0x200) != 0, Marshal.ReadInt32(analysis, 0x1d0),
                    Marshal.ReadInt32(analysis, 0x158), bins, Math.Sqrt(energy / bins)));
            }
            catch (Exception error) { owner._error ??= error; }
        }
        [UnmanagedCallersOnly]
        private static void Before(nint context)
        {
            var owner = _active!;
            try
            {
                if (owner._donor is not { } donor) return;
                nint row = Marshal.ReadIntPtr(context, 8);
                int frame = Marshal.ReadInt32(context, 48), begin = Marshal.ReadInt32(row), end = Marshal.ReadInt32(row, 4),
                    split = begin + Marshal.ReadInt32(row, 8);
                // Exact target identity and original s-side output interval.
                // This also substitutes the reader calls used to prepare the
                // existing seam caches; no cached silent boundary is retained.
                if (Marshal.ReadInt32(row, 0x10) != 60 || Marshal.ReadInt32(row, 0x14) != 51 ||
                    frame < split || frame >= end || Marshal.ReadInt32(Marshal.ReadIntPtr(row, 0x20), 0x1b0) != 4) return;
                Require(end - split is >= 2 and <= 32, "Unexpected target consonant span.");
                int source = owner._mode switch
                {
                    "tail" => donor.Last - (end - 1 - frame),
                    "mid" => (donor.First + donor.Last) / 2 + (frame - split),
                    "pool" or "reread" => (donor.First + donor.Last) / 2 + (frame - split),
                    _ => donor.First + (int)Math.Round((frame - split) * (donor.Last - donor.First) / (double)(end - split - 1))
                };
                Require(source >= donor.First && source <= donor.Last && donor.Engine.CurrentFrame == 0, "Borrow left the donor consonant.");
                Marshal.Copy(donor.View, 0, owner._view, donor.View.Length);
                Marshal.WriteIntPtr(context, 8, owner._view);
                int original = Marshal.ReadInt32(context, 16); Marshal.WriteInt32(context, 16, source);
                owner.Calls.Add(new(frame, begin, end, source, original, Marshal.ReadInt32(row, 0x10), Marshal.ReadInt32(row, 0x14)));
                Require(owner.Calls.Count <= 128, "Borrow read bound exceeded.");
            }
            catch (Exception error) { owner._error ??= error; }
        }
        public void Dispose() { _hook.Dispose(); Marshal.FreeHGlobal(_view); _active = null; }
    }
}
