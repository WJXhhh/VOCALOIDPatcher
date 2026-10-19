using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Same-source onset experiment. Never connects to the Editor. Audio shape
    // is assessed separately; a changed hash is not an acceptance condition.
    private static int ProbeOnsetProcess(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string native = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(native); NativeSampledTiming.Initialize(native);
        bool production = Environment.GetEnvironmentVariable("VOCALOID_ONSET_PRODUCTION") == "1";
        if (production) NativeTransitionGain.Initialize(native);
        int pitch = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_ONSET_PITCH"), out int p) ? p : 60;
        int duration = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_ONSET_DURATION"), out int d) ? d : 480;
        string bank = Environment.GetEnvironmentVariable("VOCALOID_ONSET_BANK") ?? "BL8CEAM5N4XN3LFK";
        Require(pitch is >= 24 and <= 96 && duration is >= 240 and <= 1920, "Onset fixture exceeds its bound.");
        using var fixture = CreateStandaloneFixture(manager, 1, duration, 0, 3840);
        fixture.Part.SetVoiceBankID(bank);
        var note = fixture.Notes[0]; fixture.Part.MoveNote(new VSMRelTick(960), note);
        note.SetNoteNumber(pitch); note.NoteVelocity = 64; note.SetPhonemes("a", true, 4);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        using var counters = new TimingDseCounters();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var prepared = backend.PrepareCoefficient(note, 1);
        using var capture = new TimingCoefficientHook();
        var ids = new Dictionary<nint, int>(); var reports = new List<object>();
        ulong epoch = 44000; int? originalMaterial = null;
        var before = counters.Read();
        var baseline = prepared.QueryTransitions(new("a", []), 1);
        var neutral = prepared.QueryTransitionControls(baseline, new(baseline.Fingerprint), 1);
        Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Onset preparation synthesized audio.");
        var onset = baseline.Edges.Single(e => e.Identity.From == "Sil" && e.Identity.To == "a");
        var left = new TransitionEndpointRef(onset.Identity, TransitionEndpoint.Begin);
        var right = new TransitionEndpointRef(onset.Identity, TransitionEndpoint.End);
        before = counters.Read();
        var leftRange = prepared.QueryTransitionRange(baseline, left, 1);
        var rightRange = prepared.QueryTransitionRange(baseline, right, 1);
        Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Onset range query synthesized audio.");
        if (production)
        {
            before = counters.Read(); var watch = Stopwatch.StartNew();
            var offsets = prepared.QueryOnsetRange(baseline, new(baseline.Fingerprint), onset.Identity, 1);
            double rangeMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
            var cached = prepared.QueryOnsetRange(baseline, new(baseline.Fingerprint), onset.Identity, 1);
            Require(offsets.SequenceEqual(cached) && new[] { 0, -8, -24, -48 }.All(offsets.Contains), "Production onset range lost a certified position.");
            Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Production onset range synthesized audio.");
            File.WriteAllText(Path.Combine(directory, "onset-range.json"), JsonSerializer.Serialize(new { rangeMs, cachedMs = watch.Elapsed.TotalMilliseconds,
                minimum = offsets.Min(), maximum = offsets.Max(), count = offsets.Length, noAudio = true }));
        }
        before = counters.Read();
        float prefixSourceMaximum = float.MinValue;
        foreach (var (endpoint, range) in production ? [] : new[] { (left, leftRange), (right, rightRange) })
        foreach (int frame in range.ReachableFrames)
        {
            var candidate = prepared.QueryTransitionControls(baseline, new(baseline.Fingerprint, [new(endpoint, frame)]), 1);
            var head = candidate.Segments.First(s => s.To != null);
            foreach (var point in head.Frames.Where(f => f.Frame < head.Split))
                prefixSourceMaximum = Math.Max(prefixSourceMaximum, BitConverter.Int32BitsToSingle(point.SourceBits));
        }
        Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Exhaustive onset prefix proof synthesized audio.");
        Require(prefixSourceMaximum <= 1, "The fixed-split prefix reaches more source material than the reviewed fixture.");
        File.WriteAllText(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new
        {
            bank, pitch, duration, velocity = 64, position = 960, bpm = 120,
            samplingRate = 44100, hop = 256, nativeFrameOrigin = 3087, presendSeconds = .5,
            sdk = typeof(WIVSMSequence).Assembly.GetName().Version!.ToString(), compatibility = LocalTimingNative.Profile.Id,
            onset, baseline.Notes, baseline.Anchors,
            prefixSourceMaximum, prefixCandidates = leftRange.ReachableFrames.Length + rightRange.ReachableFrames.Length,
            leftRange = new { leftRange.ReachableFrames, leftRange.NativePlans, leftRange.Milliseconds },
            rightRange = new { rightRange.ReachableFrames, rightRange.NativePlans, rightRange.Milliseconds }
        }, new JsonSerializerOptions { WriteIndented = true }));
        TransitionGainHook? gain = null;
        using var seam = new OnsetSeamProbe();
        string Render(string name, string family, int requested, TransitionExecutionPlan execution, bool record = true,
            ImmutableArray<TransitionFrame>? sourceProgress = null, bool copyFlags = true, int? seamLeft = null)
        {
            seam.Reset(execution.Segments.First(s => s.To != null), seamLeft);
            NativeSampledTiming.Publish(fixture.Part, ++epoch, new Dictionary<nint, float>(),
                execution.RenderAnchors.Select(a => new NativePhonemeTiming.Point(a.From, a.To)).ToArray(),
                production ? NativeTransitionGain.Gains(execution) : null, production ? NativeTransitionGain.Progress(execution) : null);
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
            capture.Reset(-1, false, null);
            var observed = new Dictionary<int, TransitionSegment>(); var frames = new Dictionary<int, TransitionFrame>();
            var materials = new Dictionary<int, DurationRow>(); int consumed = 0;
            var rewritten = new HashSet<int>();
            var firstArt = execution.Segments.First(s => s.To != null);
            capture.BeforeSynthesis = state =>
            {
                TransitionGainHook.State = state;
                if (sourceProgress is { } progress) RewriteOnsetProgress(state, firstArt, progress, rewritten, copyFlags);
            };
            capture.AfterOutput = (_, _) => TransitionGainHook.State = 0;
            capture.AfterStep = state =>
            {
                int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                foreach (var segment in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                {
                    observed[segment.Begin] = segment;
                    foreach (var point in segment.Frames) frames[point.Frame] = point;
                }
                foreach (var row in ReadDurationRows(state, ids, consumed, cursor)) materials[row.Begin] = row;
                consumed = cursor;
            };
            var expected = execution.Segments.Where(s => s.To != null).ToArray();
            if (gain != null)
            {
                var first = expected[0];
                gain.Reset(new(first.Begin, first.End, first.FromId, first.ToId, first.Stage, first.Reverse ? 1 : 0,
                    0, 0, new(-1, 0, 0, 0, 0, [])), 0, 0, 1);
            }
            string path = Path.Combine(directory, name + ".wav"); var watch = Stopwatch.StartNew();
            try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, path); capture.Check(); gain?.Check(); }
            finally { capture.AfterStep = null; capture.BeforeSynthesis = null; capture.AfterOutput = null; }
            seam.Check();
            var status = NativeSampledTiming.ReadStatus((nint)fixture.Part);
            Require(status.Failures == 0 && status.Scopes > 0, "Onset render did not consume its numerical route.");
            var actual = observed.Values.Where(s => s.To != null).OrderBy(s => s.Begin).ToArray();
            Require(actual.Length == expected.Length, "Onset render changed ART topology.");
            int onsetMaterial = materials[actual[0].Begin].Material.Id;
            originalMaterial ??= onsetMaterial;
            Require(onsetMaterial == originalMaterial, "Onset comparison changed the actual selected material object.");
            for (int i = 0; i < actual.Length; i++)
            {
                var a = actual[i]; var b = expected[i];
                Require((a.Begin, a.Split, a.End, a.From, a.To, a.Stage, a.Reverse, a.MaterialMetadata) ==
                    (b.Begin, b.Split, b.End, b.From, b.To, b.Stage, b.Reverse, b.MaterialMetadata), "Actual onset allocation differs from planning.");
                var expectedFrames = sourceProgress.HasValue && i == 0 ? sourceProgress.Value : b.Frames;
                Require(expectedFrames.All(f => frames.TryGetValue(f.Frame, out var point) &&
                    (sourceProgress.HasValue && !copyFlags && i == 0 ? f.SourceBits == point.SourceBits : f == point)), "Actual onset source-frame map differs from the declared experiment.");
            }
            if (sourceProgress.HasValue) Require(rewritten.Count == sourceProgress.Value.Length, "Source-progress experiment did not reach every target frame.");
            Require(note.RelPosTick.Value == 960 && note.DurationTick.Value == duration && note.NoteVelocity == 64 && note.NoteNumber == pitch,
                "Onset comparison altered its musical note.");
            string hash = TimingPcmHash(path);
            if (record)
            {
                reports.Add(new
                {
                    name, family, requested, hash, onsetMaterial, rewrittenFrames = rewritten.Count, copyFlags, seamLeft, seamChanges = seam.Changed,
                    seamCalls = seam.Calls.ToArray(), seamControlConsumed = seamLeft.HasValue && seam.Changed > 0,
                    renderMilliseconds = watch.Elapsed.TotalMilliseconds,
                    anchors = execution.Controls.Anchors, segments = observed.Values.OrderBy(s => s.Begin).Select(s => s with
                    { Frames = frames.Values.Where(f => f.Frame >= s.Begin && f.Frame < s.End).OrderBy(f => f.Frame).ToImmutableArray() }),
                    materials = materials.Values.OrderBy(r => r.Begin), gainTrace = gain?.Trace.ToArray(),
                    nativePhonePositions = note.GetPhonemePositions(), noteFrameBoundaries = execution.Layout.Notes,
                    status.Scopes, status.Failures
                });
                File.WriteAllText(Path.Combine(directory, "onset-process.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"onset={name}; L/S/R={actual[0].Begin}/{actual[0].Split}/{actual[0].End}; frames={frames.Count}");
            }
            return hash;
        }
        int Nearest(TransitionReachableRange range, int frame) => range.ReachableFrames.OrderBy(f => Math.Abs(f - frame)).First();
        try
        {
            Render("warmup", "warmup", 0, neutral, false);
            if (!production) gain = new TransitionGainHook();
            string original = Render("baseline", "baseline", 0, neutral);
            foreach (int amount in new[] { 8, 24, 48 })
            {
                before = counters.Read();
                var phoneme = prepared.QueryTransitions(new("a", [new(TimingAdjustment.PhonemeBoundary, 0, (float)(-amount * 256d / 44100))]), 1);
                var phonePlan = prepared.QueryTransitionControls(phoneme, new(phoneme.Fingerprint), 1);
                if (production)
                {
                    var watch = Stopwatch.StartNew();
                    var complete = prepared.QueryTransitionControls(baseline, new TransitionControlPlan(baseline.Fingerprint).WithOnset(onset.Identity, -amount), 1);
                    Console.WriteLine($"production.plan={amount}; ms={watch.Elapsed.TotalMilliseconds:F3}; native={complete.NativePlans}; progress={complete.SourceProgress.Sum(p=>p.Frames.Length)}");
                    Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Production onset planning synthesized audio.");
                    Render("phoneme-early-" + amount, "phoneme", amount, phonePlan);
                    Render("source-progress-" + amount, "source-progress", amount, complete);
                    Require(Render("restore-" + amount, "restore", 0, neutral) == original, "Production onset restore changed PCM.");
                    continue;
                }
                var leftPlan = prepared.QueryTransitionControls(baseline, new(baseline.Fingerprint,
                    [new(left, Nearest(leftRange, onset.Current.Begin - amount))]), 1);
                var rightPlan = prepared.QueryTransitionControls(baseline, new(baseline.Fingerprint,
                    [new(right, Nearest(rightRange, onset.Current.End + amount))]), 1);
                var bothPlan = prepared.QueryTransitionControls(baseline, new(baseline.Fingerprint,
                    [new(left, Nearest(leftRange, onset.Current.Begin - amount)), new(right, Nearest(rightRange, onset.Current.End + amount))]), 1);
                Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Candidate query synthesized audio.");
                Render("phoneme-early-" + amount, "phoneme", amount, phonePlan);
                Render("transition-left-" + amount, "left", amount, leftPlan);
                Render("transition-right-" + amount, "right", amount, rightPlan);
                Render("transition-both-" + amount, "both", amount, bothPlan);
                {
                    // Research only: copy this SAME ART's verified source
                    // progress from the phoneme reference, keeping Split and
                    // musical/phoneme timing fixed. No PCM processing or gain.
                    var reference = phonePlan.Segments.First(s => s.To != null);
                    Require(reference.MaterialMetadata == onset.Current.MaterialMetadata, "Reference changed the onset source.");
                    before = counters.Read();
                    var allocation = prepared.QueryTransitionControls(baseline, new(baseline.Fingerprint,
                        [new(left, reference.Begin), new(right, reference.End)]), 1);
                    Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Matched-allocation query synthesized audio.");
                    Render("matched-allocation-" + amount, "matched-allocation", amount, allocation);
                    Render("source-progress-" + amount, "source-progress", amount, allocation, sourceProgress: reference.Frames);
                    if (amount == 24)
                    {
                        Render("position-only-24", "position-only", amount, allocation, sourceProgress: reference.Frames, copyFlags: false);
                        int seamLeftBudget = reference.Split - reference.Begin;
                        Render("seam-only-24", "seam-only", amount, allocation, seamLeft: seamLeftBudget);
                        Render("source-progress-seam-24", "source-progress-seam", amount, allocation,
                            sourceProgress: reference.Frames, copyFlags: false, seamLeft: seamLeftBudget);
                    }
                }
                Require(Render("restore-" + amount, "restore", 0, neutral) == original, "Onset comparison did not restore the exact baseline PCM.");
            }
        }
        finally { gain?.Dispose(); NativeSampledTiming.Remove((nint)fixture.Part); }
        return 0;
    }

    private static unsafe void RewriteOnsetProgress(nint state, TransitionSegment target,
        ImmutableArray<TransitionFrame> progress, HashSet<int> rewritten, bool copyFlags)
    {
        var profile = LocalTimingNative.Profile;
        nint schedule = Marshal.ReadIntPtr(state, profile.StateSchedule);
        int end = ((delegate* unmanaged<nint, int>)(LocalTimingNative.Dse + profile.DseScheduleEnd))(schedule);
        foreach (var point in progress)
        {
            if (point.Frame < target.Begin || point.Frame >= target.End || point.Frame >= end) continue;
            nint entry = ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + profile.DseScheduleRow))(schedule, point.Frame);
            nint row = entry == 0 ? 0 : Marshal.ReadIntPtr(entry);
            if (row == 0 || Marshal.ReadInt32(row, profile.RowBegin) != target.Begin || Marshal.ReadInt32(row, profile.RowEnd) != target.End) continue;
            Require(Marshal.ReadInt32(row, profile.RowFrom) == target.FromId && Marshal.ReadInt32(row, profile.RowTo) == target.ToId &&
                Marshal.ReadInt32(row, profile.RowStage) == target.Stage, "Source-progress row identity changed.");
            Marshal.WriteInt32(entry, 8, point.SourceBits);
            if (copyFlags) { Marshal.WriteByte(entry, 12, point.Flag0); Marshal.WriteByte(entry, 13, point.Flag1); }
            rewritten.Add(point.Frame);
        }
    }

    private sealed unsafe class OnsetSeamProbe : IDisposable
    {
        private static OnsetSeamProbe? _active;
        private readonly ResearchDurationDetour _detour;
        private TransitionSegment? _target;
        private int? _left;
        private Exception? _error;
        internal int Changed { get; private set; }
        internal sealed record Call(int Begin, int End, int From, int To, int OriginalLeft, bool Matched);
        internal List<Call> Calls { get; } = new();
        internal OnsetSeamProbe()
        {
            if (_active != null) throw new InvalidOperationException("Seam probe already active.");
            // Five complete non-relative instructions (16 bytes), checked in
            // both certified DSE images. Integer IDs/budgets and float window
            // ABI were verified against the native callsite and callee.
            _detour = new(0x1751f0, [0x48, 0x89, 0x5c, 0x24, 0x10, 0x57, 0x48, 0x83,
                0xec, 0x20, 0x48, 0x8b, 0xd9, 0x89, 0x51, 0x48],
                (nint)(delegate* unmanaged<nint, int, int, byte, byte, int, int, int, int, float, void>)&Prepare);
            _active = this;
        }
        internal void Reset(TransitionSegment target, int? left) { _target = target; _left = left; Changed = 0; _error = null; Calls.Clear(); }
        internal void Check() { if (_error != null) throw new InvalidOperationException("Seam experiment failed.", _error); }
        [UnmanagedCallersOnly]
        private static void Prepare(nint seam, int from, int to, byte previousSta, byte currentSta,
            int previousLength, int currentLength, int previousRight, int currentLeft, float window)
        {
            var owner = _active!;
            try
            {
                nint row = seam - 0xa0;
                // This disposable process renders one Part. Match its exact
                // row without relying on the output callback's TLS lifetime.
                bool matched = owner._target is { } target &&
                    Marshal.ReadInt32(row) == target.Begin && Marshal.ReadInt32(row, 4) == target.End &&
                    Marshal.ReadInt32(row, 0x10) == target.FromId && Marshal.ReadInt32(row, 0x14) == target.ToId;
                if (owner.Calls.Count < 32) owner.Calls.Add(new(Marshal.ReadInt32(row), Marshal.ReadInt32(row, 4),
                    Marshal.ReadInt32(row, 0x10), Marshal.ReadInt32(row, 0x14), currentLeft, matched));
                if (owner._left.HasValue && matched)
                { currentLeft = owner._left.Value; owner.Changed++; }
            }
            catch (Exception error) { owner._error ??= error; }
            ((delegate* unmanaged<nint, int, int, byte, byte, int, int, int, int, float, void>)owner._detour.Trampoline)(
                seam, from, to, previousSta, currentSta, previousLength, currentLength, previousRight, currentLeft, window);
        }
        public void Dispose() { _detour.Dispose(); _active = null; }
    }
}
