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
        DatabaseManager database, string directory)
    {
        string native = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativeTransitionGain.Initialize(native);
        string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        File.WriteAllText(Path.Combine(directory, "artifact-environment.json"), JsonSerializer.Serialize(new
        {
            sdk = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(WIVSMSequenceManager).Assembly.Location).FileVersion,
            nativeLibrarySha256 = Hash(native), dseSha256 = Hash(Path.Combine(EditorDirectory, "DSE.dll")),
            sampleRate = 44100, hop = 256
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
        };
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
            note.SetNoteNumber(60); note.NoteVelocity = 64;
            note.SetPhonemes(test.Phones, true, test.Language);
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            using var prepared = backend.PrepareCoefficient(note, 1);
            var layout = prepared.QueryTransitions(new(test.Phones, []), 1);
            var baseline = new TransitionControlPlan(layout.Fingerprint);
            var variants = new List<(string Name, Func<TransitionControlPlan> Build)> { ("baseline", () => baseline) };
            foreach (var edge in layout.Edges)
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
                    }
                }
            }
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
