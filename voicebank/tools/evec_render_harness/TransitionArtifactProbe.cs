using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Diagnostic renders only: no limiter, normalization, trimming or host edits.
    private static int ProbeTransitionArtifacts(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory, bool extreme = false, bool pairsOnly = false, bool yuanOnly = false)
    {
        string native = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativeTransitionGain.Initialize(native);
        string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        File.WriteAllText(Path.Combine(directory, "artifact-environment.json"), JsonSerializer.Serialize(new
        {
            sdk = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(WIVSMSequenceManager).Assembly.Location).FileVersion,
            nativeLibrarySha256 = Hash(native), dseSha256 = Hash(Path.Combine(EditorDirectory, "DSE.dll")),
            sampleRate = 44100, hop = 256, extreme, pairsOnly, yuanOnly
        }, new JsonSerializerOptions { WriteIndented = true }));
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        using var capture = new TimingCoefficientHook();
        var report = new List<object>();
        ulong epoch = 97000;
        int rendered = 0, rejected = 0, failed = 0;
        var banks = Enumerable.Range(0, checked((int)database.NumVoiceBanks))
            .Select(i => database.GetVoiceBankByIndex((ulong)i)!.CompID).ToHashSet();
        var tests = new[]
        {
            (Name: "v5-la", Bank: "BL8CEAM5N4XN3LFK", Phones: "l a", Language: 4, Duration: 960),
            (Name: "v5-short", Bank: "BL8CEAM5N4XN3LFK", Phones: "l a", Language: 4, Duration: 120),
            (Name: "v6-la", Bank: "BETDB8W6KWZPYEB9", Phones: "l a", Language: 4, Duration: 960),
            (Name: "v6-short", Bank: "BETDB8W6KWZPYEB9", Phones: "l a", Language: 4, Duration: 120),
            (Name: "miku-ka", Bank: "BCNFCY43LB2LZCD4", Phones: "k a", Language: 0, Duration: 960)
        }.Concat(extreme ? new[]
        {
            (Name: "v6-low", Bank: "BETDB8W6KWZPYEB9", Phones: "l a", Language: 4, Duration: 120),
            (Name: "v6-high", Bank: "BETDB8W6KWZPYEB9", Phones: "l a", Language: 4, Duration: 120),
            (Name: "v6-nasal", Bank: "BETDB8W6KWZPYEB9", Phones: "ts` UN", Language: 4, Duration: 240),
            (Name: "v5-plosive", Bank: "BL8CEAM5N4XN3LFK", Phones: "t a_n", Language: 4, Duration: 120),
            (Name: "miku-fricative", Bank: "BCNFCY43LB2LZCD4", Phones: "s a", Language: 0, Duration: 120)
        } : []).ToArray();
        if (yuanOnly)
            tests = new[] { (Name: "v5", Bank: "BL8CEAM5N4XN3LFK"), (Name: "v6", Bank: "BETDB8W6KWZPYEB9") }
                .SelectMany(bank => new[] { 36, 48, 60, 72, 84 }.Select(pitch =>
                    (Name: $"{bank.Name}-yuan-p{pitch}", bank.Bank, Phones: "y{_n", Language: 4, Duration: 960))).ToArray();
        void Record(object value)
        {
            report.Add(value);
            File.WriteAllText(Path.Combine(directory, "transition-artifacts.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var test in tests)
        {
            if (!banks.Contains(test.Bank)) { Record(new { fixture = test.Name, skipped = "bank-unavailable" }); continue; }
            using var fixture = CreateStandaloneFixture(manager, 1, test.Duration, 0, 4800);
            Require(fixture.Part.SetVoiceBankID(test.Bank), "Cannot select artifact probe bank.");
            var note = fixture.Notes[0];
            fixture.Part.MoveNote(new VSMRelTick(960), note);
            note.SetNoteNumber(yuanOnly ? int.Parse(test.Name[(test.Name.LastIndexOf('p') + 1)..]) :
                test.Name.EndsWith("-low") ? 36 : test.Name.EndsWith("-high") ? 84 : 60);
            note.NoteVelocity = extreme ? 127 : 64;
            note.SetPhonemes(test.Phones, true, test.Language);
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            using var prepared = backend.PrepareCoefficient(note, 1);
            var layout = prepared.QueryTransitions(new(test.Phones, []), 1);
            var baseline = new TransitionControlPlan(layout.Fingerprint);
            var variants = new List<(string Name, Func<TransitionControlPlan> Build)> { ("baseline", () => baseline) };
            foreach (var edge in layout.Edges.Where(e => !yuanOnly || e.Identity.From == "Sil"))
            {
                string key = $"edge{layout.Edges.IndexOf(edge)}";
                foreach (float db in new[] { -6f, 6f })
                {
                    float decibels = db;
                    variants.Add(($"{key}-gain{decibels}", () => new(layout.Fingerprint, gains: [new(edge.Identity, decibels)])));
                }
                foreach (float phase in new[] { -1f, -.5f, 0f, .5f, 1f })
                {
                    float amount = phase;
                    variants.Add(($"{key}-coverage-phase{amount}", () =>
                        TransitionExpressionControl.Progress(baseline, edge.Current, edge.Identity, amount)));
                    if (phase != 0) variants.Add(($"{key}-phase{amount}", () => baseline.WithPhase(edge.Identity, amount)));
                }
                foreach (var side in new[] { TransitionEndpoint.Begin, TransitionEndpoint.End })
                {
                    var endpoint = new TransitionEndpointRef(edge.Identity, side);
                    var range = prepared.QueryTransitionRange(layout, endpoint, 1);
                    foreach (int frame in new[] { range.Minimum, range.Maximum }.Distinct()
                        .Where(f => f != range.Junction.CurrentFrame))
                    {
                        int target = frame;
                        variants.Add(($"{key}-{side}{target}", () => baseline.WithJunction(layout, endpoint, target)));
                        variants.Add(($"{key}-{side}{target}-coverage", () =>
                            TransitionExpressionControl.Enable(baseline.WithJunction(layout, endpoint, target), edge.Current, edge.Identity)));
                        if (extreme)
                            foreach (float phase in new[] { -1f, 1f })
                            foreach (float db in new[] { -60f, 12f })
                            {
                                float amount = phase, decibels = db;
                                variants.Add(($"{key}-{side}{target}-phase{amount}-gain{decibels}", () =>
                                    TransitionExpressionControl.Progress(new TransitionControlPlan(layout.Fingerprint, gains: [new(edge.Identity, decibels)])
                                        .WithJunction(layout, endpoint, target), edge.Current, edge.Identity, amount)));
                            }
                    }
                }
                if (extreme)
                {
                    var beginRef = new TransitionEndpointRef(edge.Identity, TransitionEndpoint.Begin);
                    var endRef = new TransitionEndpointRef(edge.Identity, TransitionEndpoint.End);
                    var beginRange = prepared.QueryTransitionRange(layout, beginRef, 1);
                    var endRange = prepared.QueryTransitionRange(layout, endRef, 1);
                    foreach (bool compress in new[] { false, true })
                    foreach (float phase in new[] { -1f, 1f })
                    foreach (float db in new[] { 0f, 12f })
                    {
                        int beginFrame = compress ? beginRange.Maximum : beginRange.Minimum;
                        int endFrame = compress ? endRange.Minimum : endRange.Maximum;
                        float amount = phase, decibels = db;
                        variants.Add(($"pair-{key}-{(compress ? "compressed" : "expanded")}-phase{amount}-gain{decibels}", () =>
                            TransitionExpressionControl.Progress(new TransitionControlPlan(layout.Fingerprint,
                                gains: decibels == 0 ? [] : [new(edge.Identity, decibels)])
                                .WithJunction(layout, beginRef, beginFrame).WithJunction(layout, endRef, endFrame),
                                edge.Current, edge.Identity, amount)));
                    }
                    foreach (float phase in new[] { -1f, 0f, 1f })
                    foreach (float db in new[] { -60f, -24f, 12f })
                    {
                        float amount = phase, decibels = db;
                        variants.Add(($"{key}-phase{amount}-gain{decibels}", () =>
                            TransitionExpressionControl.Progress(new(layout.Fingerprint, gains: [new(edge.Identity, decibels)]),
                                edge.Current, edge.Identity, amount)));
                    }
                }
            }
            if (yuanOnly)
            {
                variants.Clear();
                variants.Add(("warm", () => baseline));
                variants.Add(("baseline", () => baseline));
                var edge = layout.Edges.First(e => e.Identity.From == "Sil");
                var endpoint = new TransitionEndpointRef(edge.Identity, TransitionEndpoint.End);
                var range = prepared.QueryTransitionRange(layout, endpoint, 1);
                foreach (int target in new[] { range.Minimum, range.Junction.CurrentFrame }
                    .Concat(new[] { .25, .5, .75, .9, .99, 1 }.Select(f => range.ReachableFrames[
                        (int)Math.Round((range.ReachableFrames.Length - 1) * f)])).Distinct())
                {
                    int frame = target;
                    variants.Add(($"left-End{frame}", () => baseline.WithJunction(layout, endpoint, frame)));
                    variants.Add(($"left-End{frame}-coverage", () => TransitionExpressionControl.Enable(
                        baseline.WithJunction(layout, endpoint, frame), edge.Current, edge.Identity)));
                }
                foreach (int target in new[] { range.Minimum, range.Maximum })
                foreach (float phase in new[] { -1f, 1f })
                {
                    int frame = target; float amount = phase;
                    variants.Add(($"left-End{frame}-phase{amount}", () => TransitionExpressionControl.Progress(
                        baseline.WithJunction(layout, endpoint, frame), edge.Current, edge.Identity, amount)));
                }
                Record(new { fixture = test.Name, kind = "yuan-domain", test.Phones, test.Duration,
                    pitch = note.NoteNumber, velocity = note.NoteVelocity, edge.Current,
                    range.Minimum, range.Maximum, current = range.Junction.CurrentFrame,
                    reachable = range.ReachableFrames.ToArray(), notePosition = note.AbsPosTick.Value,
                    tempo = 120, quarterTicks = 480 });
            }
            if (pairsOnly) variants.RemoveAll(v => v.Name != "baseline" && !v.Name.StartsWith("pair-", StringComparison.Ordinal));
            variants.Add(("restored", () => baseline));
            foreach (var variant in variants)
            {
                TransitionExecutionPlan execution;
                var before = counters.Read();
                try { execution = prepared.QueryTransitionControls(layout, variant.Build(), 1); }
                catch (NotSupportedException e)
                {
                    rejected++;
                    Record(new { fixture = test.Name, variant = variant.Name, rejected = e.Message });
                    continue;
                }
                var planningCounters = counters.Read().Since(before);
                Require(planningCounters == new TimingDseSnapshot(0, 0, 0), "Artifact query synthesized audio.");
                prepared.PublishTransitions(fixture.Part, ++epoch, execution, 1);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                var observed = new Dictionary<int, TransitionFrame>();
                int consumed = 0;
                capture.Reset(-1, false, null);
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                        foreach (var frame in row.Frames) observed[frame.Frame] = frame;
                    consumed = cursor;
                };
                string file = test.Name + "-" + variant.Name + ".wav";
                try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, file)); capture.Check(); }
                finally { capture.AfterStep = null; }
                var timing = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                var gain = NativeTransitionGain.ReadStatus((nint)fixture.Part);
                var planned = execution.Segments.Where(r => r.To != null).SelectMany(r => r.Frames).ToArray();
                int unconsumedFrames = planned.Count(f => f.Frame >= consumed);
                int comparedFrames = planned.Length - unconsumedFrames;
                bool exact = planned.Where(f => f.Frame < consumed)
                    .All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
                bool passed = exact && timing.Failures == 0 && gain.Failures == 0;
                if (!passed) failed++;
                rendered++;
                object? smoothing = null;
                if (execution.Controls.Gains.Length == 1)
                {
                    var edit = execution.Controls.Gains[0];
                    int edgeIndex = layout.Edges.IndexOf(layout.Edges.Single(e => e.Identity == edit.Edge));
                    var row = execution.Segments.Where(r => r.To != null).ElementAt(edgeIndex);
                    // Single edited ART: its two neighbouring gains are zero,
                    // matching the host's nominal-gain boundary selection.
                    long begin = (row.Begin - 3087L) * 256, end = (row.End - 3087L) * 256;
                    string smoothed = Path.Combine(directory, file + ".smoothed.wav");
                    var results = WaveJunctionProcessor.ProcessWaveFile(Path.Combine(directory, file), smoothed,
                        [new(begin, 0, edit.Decibels), new(end, edit.Decibels, 0)]);
                    smoothing = new { file = Path.GetFileName(smoothed), begin, end, edit.Decibels, results };
                }
                Record(new { fixture = test.Name, variant = variant.Name, file, test.Bank, test.Duration,
                    pitch = note.NoteNumber, velocity = note.NoteVelocity, test.Phones,
                    passed, exactConsumedSourceMap = exact, comparedFrames, unconsumedFrames,
                    timingFailures = timing.Failures, gainFailures = gain.Failures, planningCounters,
                    hash = TimingPcmHash(Path.Combine(directory, file)), smoothing,
                    rows = execution.Segments.Where(r => r.To != null).Select(r => new
                    {
                        r.Begin, r.Split, r.End, r.From, r.To, r.Stage, r.Reverse,
                        sources = r.Frames.Select(f => BitConverter.Int32BitsToSingle(f.SourceBits)).ToArray()
                    }) });
                Console.WriteLine($"artifact.render={test.Name}/{variant.Name};passed={passed}");
            }
            NativeSampledTiming.Remove((nint)fixture.Part);
        }
        Console.WriteLine($"artifact.total={rendered};rejected={rejected};failed={failed}");
        return rendered > 0 && failed == 0 ? 0 : 1;
    }
}
