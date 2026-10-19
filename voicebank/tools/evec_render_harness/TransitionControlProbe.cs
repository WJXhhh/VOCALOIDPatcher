using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private sealed record TransitionFixture(string Name, string Bank, string[] Phones, int Language,
        int Duration, int Edge, int? RequiredStage = null);

    // Research only: private sequences, exact-profile native calls, no Editor IPC.
    private static int ProbeTransitionControl(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        LocalTimingNative.VerifyImages();
        string library = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(library);
        NativeSampledTiming.Initialize(library);
        using var counters = new TimingDseCounters();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var planner = new NativeConsonantTimingEngine((nint)dse);
        nint planningState = (nint)typeof(NativeConsonantTimingEngine)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(planner)!;
        using var capture = new TimingCoefficientHook();
        TransitionFixture[] fixtures =
        [
            new("chinese-a-head", "BL8CEAM5N4XN3LFK", ["a"], 4, 960, 0),
            new("chinese-a-tail", "BL8CEAM5N4XN3LFK", ["a"], 4, 960, -1),
            new("chinese-la-inner", "BL8CEAM5N4XN3LFK", ["l a"], 4, 960, 1),
            new("chinese-multi", "BMBNDB8EM5222MK3", ["ts\\_h i iE_r @N"], 4, 960, 2),
            new("chinese-connected", "BL8CEAM5N4XN3LFK", ["a", "l a"], 4, 480, 1),
            new("chinese-short", "BL8CEAM5N4XN3LFK", ["l a"], 4, 120, 1),
            new("chinese-fricative", "BL8CEAM5N4XN3LFK", ["s\\ i_n n"], 4, 480, 1),
            new("miku-ka", "BCNFCY43LB2LZCD4", ["k a"], 0, 960, 1),
            new("miku-connected", "BCNFCY43LB2LZCD4", ["a", "k a"], 0, 480, 1),
            new("miku-triplet-stage0", "BCNFCY43LB2LZCD4", ["n n#2 a"], 0, 960, 1, 0),
            new("miku-triplet-stage1", "BCNFCY43LB2LZCD4", ["n n#2 a"], 0, 960, 2, 1)
        ];
        string? selection = Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_FIXTURE");
        if (selection != null) fixtures = fixtures.Where(f => f.Name == selection).ToArray();
        Require(fixtures.Length > 0, "No transition fixture selected.");
        var reports = new List<object>();
        ulong epoch = 24000;
        foreach (var test in fixtures)
        {
            using var fixture = CreateStandaloneFixture(manager, test.Phones.Length, test.Duration, 0, 4800);
            fixture.Part.SetVoiceBankID(test.Bank);
            for (int n = 0; n < fixture.Notes.Length; n++)
            {
                fixture.Part.MoveNote(new VSMRelTick(960 + n * test.Duration), fixture.Notes[n]);
                fixture.Notes[n].SetPhonemes(test.Phones[n], true, test.Language);
                fixture.Notes[n].SetNoteNumber(60);
                fixture.Notes[n].NoteVelocity = 64;
            }
            fixture.Sequence.InterpolatedMasterVolume();
            fixture.Sequence.Commit(false);
            using var prepared = backend.PrepareCoefficient(fixture.Notes[^1], 1);
            var layout = prepared.QueryPhonemes(new(test.Phones[^1], []), 1);
            var ids = new Dictionary<nint, int>();
            DurationRow[]? baselineRows = null;
            DurationFramePosition[]? baselineFrames = null;
            int[][]? baselineBoundaries = null;
            TransitionGainHook.Frame[]? neutralTrace = null;
            string? baselineHash = null;
            DurationRow? target = null;
            TransitionGainHook? gain = null;
            try
            {
                foreach (string variant in new[] { "unhooked", "neutral", "gain-minus12", "gain-plus6", "gain-mute",
                    "gain-restored", "left-long", "left-short", "right-long", "right-short", "timing-and-gain", "restored" })
                {
                    var anchors = layout.Anchors;
                    int requestedLeft = variant is "left-long" or "timing-and-gain" ? -6 : variant == "left-short" ? 6 : 0;
                    int requestedRight = variant is "right-long" or "timing-and-gain" ? 6 : variant == "right-short" ? -6 : 0;
                    bool timing = requestedLeft != 0 || requestedRight != 0;
                    string? unavailable = null;
                    int movedLeft = 0, movedRight = 0;
                    if (timing)
                    {
                        // Keep every existing phoneme anchor and the ART's split fixed.
                        // Move only an internal ART/STA junction within its neighbour anchors.
                        var points = layout.Anchors.ToDictionary(p => p.From, p => p.To);
                        points.TryAdd(target!.Begin + target.LeftLength, target.Begin + target.LeftLength);
                        foreach (var edit in new[] { (target.Begin, requestedLeft), (target.End, requestedRight) })
                        {
                            if (edit.Item2 == 0) continue;
                            if (points.ContainsKey(edit.Item1)) { unavailable = "ART endpoint is a protected phoneme/context anchor."; break; }
                            points[edit.Item1] = edit.Item1;
                        }
                        if (unavailable == null)
                        {
                            foreach (var edit in new[] { (target.Begin, requestedLeft), (target.End, requestedRight) })
                            {
                                if (edit.Item2 == 0) continue;
                                var ordered = points.Keys.Order().ToArray();
                                int index = Array.IndexOf(ordered, edit.Item1);
                                int low = index == 0 ? edit.Item1 : points[ordered[index - 1]] + 1;
                                int high = index + 1 == ordered.Length ? edit.Item1 : points[ordered[index + 1]] - 1;
                                int to = Math.Clamp(edit.Item1 + edit.Item2, low, Math.Max(low, high));
                                points[edit.Item1] = to;
                                if (edit.Item1 == target.Begin) movedLeft = to - edit.Item1;
                                else movedRight = to - edit.Item1;
                            }
                            if (movedLeft == 0 && movedRight == 0) unavailable = "No internal interval remains between protected anchors.";
                            anchors = points.OrderBy(p => p.Key).Select(p => new NativePhonemeTiming.Point(p.Key, p.Value)).ToArray();
                        }
                    }
                    if (unavailable != null)
                    {
                        reports.Add(new { fixture = test.Name, variant, unavailable });
                        Console.WriteLine($"transition={test.Name}/{variant}; unavailable={unavailable}");
                        continue;
                    }
                    float multiplier = variant switch
                    {
                        "gain-minus12" or "timing-and-gain" => MathF.Pow(10, -12f / 20),
                        "gain-plus6" => MathF.Pow(10, 6f / 20),
                        "gain-mute" => 0,
                        _ => 1
                    };
                    if (gain != null) gain.Reset(target!, movedLeft, movedRight, multiplier);
                    NativeSampledTiming.Publish(fixture.Part, ++epoch, new Dictionary<nint, float>(), anchors);
                    SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                    capture.Reset(-1, false, null);
                    int consumed = 0;
                    var rows = new Dictionary<int, DurationRow>();
                    var frames = new SortedDictionary<int, DurationFramePosition>();
                    capture.BeforeSynthesis = state => TransitionGainHook.State = state;
                    capture.AfterOutput = (_, _) => TransitionGainHook.State = 0;
                    capture.AfterStep = state =>
                    {
                        int cursor = Marshal.ReadInt32(state, 0x24158);
                        foreach (var row in ReadDurationRows(state, ids, consumed, cursor)) rows[row.Begin] = row;
                        foreach (var frame in ReadDurationFramePositions(state, ids, consumed, cursor)) frames[frame.Frame] = frame;
                        consumed = cursor;
                    };
                    string wave = Path.Combine(directory, test.Name + "-" + variant + ".wav");
                    var timer = Stopwatch.StartNew();
                    RenderTransitionWithScore(fixture.Sequence, fixture.Part, wave);
                    double renderMs = timer.Elapsed.TotalMilliseconds;
                    capture.AfterStep = null; capture.BeforeSynthesis = null; capture.AfterOutput = null;
                    capture.Check(); gain?.Check();
                    var route = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                    Require(route.Failures == 0 && rows.Count > 0, "Transition timing route failed.");
                    var actual = rows.Values.OrderBy(r => r.Begin).ToArray();
                    string hash = TimingPcmHash(wave);
                    Require(ReadWaveMetrics(wave).Peak > .00001, "Transition probe produced silent whole output.");
                    baselineHash ??= hash;
                    baselineRows ??= actual;
                    baselineFrames ??= frames.Values.ToArray();
                    baselineBoundaries ??= fixture.Notes.Select(n => n.GetPhonemePositions().ToArray()).ToArray();
                    Require(fixture.Notes.Select((n, i) => n.GetPhonemePositions().SequenceEqual(baselineBoundaries[i])).All(v => v),
                        "Transition control moved protected phoneme boundaries.");
                    // STA may use a different number of repetitions after an ART
                    // consumes more/less of the sustain. Check sources and stages,
                    // retaining the raw row counts as separate evidence.
                    Require(SourceRuns(actual).SequenceEqual(SourceRuns(baselineRows)), "Transition control changed material source runs.");
                    Require(actual.Where(r => r.To != -1).Select(SourceKey)
                        .SequenceEqual(baselineRows.Where(r => r.To != -1).Select(SourceKey)), "Transition control changed ART material/stage sequence.");
                    if (!timing) Require(frames.Values.SequenceEqual(baselineFrames), "Gain changed source-frame mapping.");
                    if (variant is "neutral" or "gain-restored" or "restored") Require(hash == baselineHash, "Neutral/restored control changed PCM.");
                    if (multiplier != 1 || timing) Require(hash != baselineHash, "Non-neutral transition control did not change PCM.");
                    if (gain != null)
                    {
                        neutralTrace ??= gain.Trace.ToArray();
                        if (!timing)
                        {
                            Require(gain.Trace.Select(f => (f.Time, f.Begin, f.End, f.Lane, f.InputHash, f.OriginalGain))
                                .SequenceEqual(neutralTrace.Select(f => (f.Time, f.Begin, f.End, f.Lane, f.InputHash, f.OriginalGain))),
                                "Gain changed pre-synthesis frame inputs or native gain.");
                            Require(gain.Trace.All(f => f.Selected || f.OriginalGain == f.AppliedGain), "Gain leaked into another native frame.");
                            if (multiplier != 1) Require(gain.Trace.Any(f => f.Selected && f.AppliedGain != f.OriginalGain), "Gain did not reach target ART frames.");
                        }
                    }
                    var before = counters.Read();
                    var planningMs = new List<double>();
                    for (int repeat = 0; repeat < 5; repeat++)
                    {
                        var watch = Stopwatch.StartNew();
                        planner.Begin();
                        bool scope = false;
                        try
                        {
                            NativePhonemeTiming.Begin(anchors); scope = true;
                            foreach (var block in capture.Inputs) planner.Plan(block.Frame, block.Bytes);
                            var planned = ReadDurationRows(planningState, ids).Where(r => r.To != -1).OrderBy(r => r.Begin).ToArray();
                            Require(planned.SequenceEqual(actual.Where(r => r.To != -1), new EdgeArtComparer()), "Private transition plan differs from consumed ART.");
                            var plannedFrames = ReadDurationFramePositions(planningState, ids, actual[0].Begin, actual[^1].End)
                                .ToDictionary(f => f.Frame);
                            Require(actual.Where(r => r.To != -1).All(r => frames.Values.Where(f => f.Frame >= r.Begin && f.Frame < r.End)
                                .All(f => plannedFrames.TryGetValue(f.Frame, out var point) && point == f)),
                                "Private transition source-frame mapping differs from consumed ART.");
                        }
                        finally { try { if (scope) NativePhonemeTiming.End(); } finally { planner.End(); } }
                        planningMs.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    var planningCounters = counters.Read().Since(before);
                    Require(planningCounters == new TimingDseSnapshot(0, 0, 0), "Transition planning synthesized audio.");
                    target ??= baselineRows.Where(r => r.To != -1).OrderBy(r => r.Begin).ToArray()[test.Edge < 0
                        ? baselineRows.Count(r => r.To != -1) - 1 : test.Edge];
                    if (test.RequiredStage is { } requiredStage)
                        Require(target.Stage == requiredStage && target.Material.Alignment.Length == 16,
                            "Expected actual three-phoneme ART stage was not consumed.");
                    reports.Add(new { fixture = test.Name, variant, multiplier, movedLeft, movedRight, hash,
                        target, rows = actual, boundaries = baselineBoundaries, anchors, planningMs, planningCounters, renderMs,
                        sameRawRowCount = actual.Length == baselineRows.Length,
                        frames = frames.Values.ToArray(), gainFrames = gain?.Trace.ToArray() });
                    File.WriteAllText(Path.Combine(directory, "transition-control.json"), JsonSerializer.Serialize(new
                    {
                        profile = LocalTimingNative.Profile.Id,
                        sdk = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(),
                        reports
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"transition={test.Name}/{variant}; rows={actual.Length}; target={target.From}>{target.To}; changedGainFrames={gain?.Trace.Count(f => f.OriginalGain != f.AppliedGain) ?? 0}; left={movedLeft}; right={movedRight}");
                    if (gain == null) gain = new TransitionGainHook();
                }
            }
            finally
            {
                capture.AfterStep = null; capture.BeforeSynthesis = null; capture.AfterOutput = null;
                TransitionGainHook.State = 0;
                gain?.Dispose();
                NativeSampledTiming.Remove((nint)fixture.Part);
            }
        }
        return 0;

        static string SourceKey(DurationRow row) => $"{row.From}/{row.To}/{row.Material.Id}/{row.Stage}/{row.Reverse}";
        static IEnumerable<string> SourceRuns(IEnumerable<DurationRow> rows)
        {
            string? previous = null;
            foreach (var row in rows)
            {
                string key = SourceKey(row);
                if (key != previous) yield return key;
                previous = key;
            }
        }
    }

    private sealed unsafe class TransitionGainHook : IDisposable
    {
        [ThreadStatic] internal static nint State;
        private static TransitionGainHook? _active;
        private readonly ResearchDurationDetour _detour;
        private DurationRow? _target;
        private int _left, _right;
        private float _multiplier = 1;
        private Exception? _error;
        internal sealed record Frame(int Time, int Begin, int End, string Lane, bool Selected, string InputHash,
            float OriginalGain, float AppliedGain);
        internal List<Frame> Trace { get; } = new();
        // Disposable fixed-range experiments only. This transforms a native
        // ART contribution before overlap-add, never a rendered PCM buffer.
        internal Func<int, string, float, float>? GainTransform { get; set; }
        internal (int Begin, int End)? GainFrameRange { get; set; }
        internal Action<nint, nint, int, string, float>? BeforeSynthesisFrame { get; set; }

        internal TransitionGainHook()
        {
            if (_active != null) throw new InvalidOperationException("Transition gain probe already active.");
            _detour = new(0x141f50, [0x48, 0x89, 0x6c, 0x24, 0x18, 0x57, 0x48, 0x83, 0xec, 0x30,
                0x48, 0x63, 0x41, 0x60, 0x48, 0x8b, 0xf9],
                (nint)(delegate* unmanaged<nint, nint, nint, nint, float, long>)&Synthesize);
            _active = this;
        }

        internal void Reset(DurationRow target, int left, int right, float multiplier)
        { _target = target; _left = left; _right = right; _multiplier = multiplier; _error = null; Trace.Clear(); }
        internal void Check() { if (_error != null) throw new InvalidOperationException("Transition gain instrumentation failed.", _error); }

        [UnmanagedCallersOnly]
        private static long Synthesize(nint analysis, nint frame, nint arg3, nint arg4, float gain)
        {
            var owner = _active!;
            float applied = gain;
            try
            {
                if (State == 0 || owner.Trace.Count >= 4096) throw new InvalidOperationException("Missing/bounded transition synthesis context.");
                int time = Marshal.ReadInt32(State, 0x24158);
                nint schedule = Marshal.ReadIntPtr(State, 0x1d568);
                nint entry = ((delegate* unmanaged<nint, int, nint>)(LocalTimingNative.Dse + 0x18b260))(schedule, time);
                nint row = Marshal.ReadIntPtr(entry);
                int begin = row == 0 ? -1 : Marshal.ReadInt32(row);
                int end = row == 0 ? -1 : Marshal.ReadInt32(row, 4);
                bool selected = owner._target is { } target && row != 0 && begin == target.Begin + owner._left &&
                    end == target.End + owner._right && Marshal.ReadInt32(row, 0x10) == target.From &&
                    Marshal.ReadInt32(row, 0x14) == target.To && Marshal.ReadInt32(row, 0x28) == target.Stage;
                if (selected && (owner.GainFrameRange is not { } range || time >= range.Begin && time < range.End))
                    applied *= owner._multiplier;
                string lane = analysis == Marshal.ReadIntPtr(State, 0x1fc8) ? "primary" :
                    analysis == Marshal.ReadIntPtr(State, 0x2b128) ? "auxiliary" : "unknown";
                if (lane == "unknown") throw new InvalidOperationException("Unidentified native synthesis lane.");
                owner.BeforeSynthesisFrame?.Invoke(analysis, frame, time, lane, gain);
                if (selected && owner.GainTransform is { } transform)
                {
                    applied = transform(time, lane, applied);
                    if (!float.IsFinite(applied) || applied < 0 || applied > 4)
                        throw new InvalidOperationException("Invalid research frame gain.");
                }
                using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var vector in new[] { (0x158, 0x160), (0x158, 0x168), (0x1a8, 0x1b0), (0x1a8, 0x1b8) })
                {
                    int count = Marshal.ReadInt32(frame, vector.Item1);
                    nint pointer = Marshal.ReadIntPtr(frame, vector.Item2);
                    if (count is < 0 or > 4097 || (count > 0 && pointer == 0)) throw new InvalidOperationException("Unbounded synthesis-frame vector.");
                    if (count > 0) fingerprint.AppendData(new ReadOnlySpan<byte>((void*)pointer, count * 4));
                }
                owner.Trace.Add(new(time, begin, end, lane, selected, Convert.ToHexString(fingerprint.GetHashAndReset()), gain, applied));
            }
            catch (Exception error) { owner._error ??= error; applied = gain; }
            return ((delegate* unmanaged<nint, nint, nint, nint, float, long>)owner._detour.Trampoline)(analysis, frame, arg3, arg4, applied);
        }

        public void Dispose() { _detour.Dispose(); _active = null; }
    }
}
