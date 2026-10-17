using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeFullPhonemeTiming(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string nativePath = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(nativePath);
        NativeSampledTiming.Initialize(nativePath);
        using var fixture = CreateStandaloneFixture(manager, 1, 960, 0, 2880);
        string bank = Environment.GetEnvironmentVariable("VOCALOID_PHONEME_BANK") ?? "BCNFCY43LB2LZCD4";
        string phones = Environment.GetEnvironmentVariable("VOCALOID_PHONEME_FIXTURE") ?? "k a i";
        fixture.Part.SetVoiceBankID(bank);
        var note = fixture.Notes[0]; note.SetPhonemes(phones, true, bank == "BL8CEAM5N4XN3LFK" ? 4 : 0);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        string baselinePath = Path.Combine(directory, "baseline.wav");
        RenderTransitionWithScore(fixture.Sequence, fixture.Part, baselinePath);
        Require(ReadWaveMetrics(baselinePath).Peak > .00001, "Baseline audio is silent.");
        string baselineHash = TimingPcmHash(baselinePath);
        var baseline = note.GetPhonemePositions().ToArray();
        Console.WriteLine("phonemes.baseline=" + JsonSerializer.Serialize(new { bank, phones, baseline }));
        Require(baseline.Length == phones.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1, "Fixture phonemes are not rendered by this voicebank.");
        int Frame(int tick) => checked((int)Math.Round((fixture.Sequence.GetTimeFromTick(new VSMAbsTick(note.AbsPosTick.Value + tick)) + .5) * 44100 / 256) + 3087);
        long Tick(int frame) => fixture.Sequence.GetTickFromTime((frame - 3087) * 256d / 44100 - .5).Value - note.AbsPosTick.Value;
        var frames = baseline.Select(Frame).ToArray();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        var results = new List<object>(); ulong epoch = 0;
        for (int axis = 0; axis < baseline.Length; axis++)
            foreach (int offset in new[] { -2, 2 })
            {
                using var prepared = backend.PrepareCoefficient(note, 1);
                var counterBefore = counters.Read();
                var plan = new SampledTimingPlan(note.Phonemes, [new(TimingAdjustment.PhonemeBoundary, axis, (float)(offset * 256d / 44100))]);
                var layout = prepared.QueryPhonemes(plan, 1);
                var queryCounters = counters.Read().Since(counterBefore);
                Require(queryCounters.Starts == 0 && queryCounters.Steps == 0 && queryCounters.Samples == 0, "Phoneme query synthesized audio.");
                long[] predicted = layout.Result.Boundaries;
                var expected = frames.Select((f, i) => Tick(f + (i == axis ? offset : 0))).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "query", axis, offset, predicted, expected }));
                bool mapped = predicted.SequenceEqual(expected);
                Require(mapped, "Boundary query differs from the requested native frame.");
                note.NoteVelocity = 63; fixture.Sequence.Commit(false); note.NoteVelocity = 64; fixture.Sequence.Commit(false);
                NativeSampledTiming.Publish(fixture.Part, ++epoch, new Dictionary<nint, float> { [(nint)note] = .5f }, layout.Anchors);
                string path = Path.Combine(directory, $"axis-{axis}-{offset}.wav");
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, path);
                var actual = note.GetPhonemePositions().Select(v => (long)v).ToArray();
                var route = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                Console.WriteLine(JsonSerializer.Serialize(new { axis, offset, baseline, predicted, expected, actual, routeFailures = route.Failures }));
                bool needsProjection = actual.Length == 0;
                int vowel = Array.FindIndex(phones.Split(' ', StringSplitOptions.RemoveEmptyEntries), note.IsVowel);
                Require((actual.SequenceEqual(predicted) || needsProjection && axis == vowel && offset > 0) && route.Failures == 0 && route.Scopes > 0,
                    "Full phoneme render differs from query.");
                Require(TimingPcmHash(path) != baselineHash, "Phoneme edit did not change audio.");
                results.Add(new { bank, phones, axis, offset, mapped, baseline, predicted, actual, needsProjection, layout.Result.Milliseconds, queryCounters, hash = TimingPcmHash(path) });
                File.WriteAllText(Path.Combine(directory, "full-phoneme-timing.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                NativeSampledTiming.Remove((nint)fixture.Part);
            }
        File.WriteAllText(Path.Combine(directory, "full-phoneme-timing.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        ProbeFullPhonemeRouting(manager, dse, database, directory);
        int lifecycleCycles = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_PHONEME_LIFECYCLE_CYCLES"), out int requestedCycles) ? Math.Clamp(requestedCycles,1,10) : 1;
        for (int cycle = 0; cycle < lifecycleCycles; cycle++)
        {
            string cycleDirectory = lifecycleCycles == 1 ? directory : Path.Combine(directory,"lifecycle-" + cycle);
            Directory.CreateDirectory(cycleDirectory);
            ProbeFullPhonemeLifecycle(manager, dse, database, cycleDirectory);
        }
        ProbeFullPhonemeLocalContext(manager, dse, database, directory);
        using (var prepared = backend.PrepareCoefficient(note, 2))
        {
            var durations = new List<double>(); var cached = new List<double>();
            var before = counters.Read();
            for (int offset = -20; offset <= 20; offset++)
            {
                var plan = new SampledTimingPlan(note.Phonemes, [new(TimingAdjustment.PhonemeBoundary, 2, (float)(offset * 256d / 44100))]);
                durations.Add(prepared.QueryPhonemes(plan, 2).Result.Milliseconds);
                cached.Add(prepared.QueryPhonemes(plan, 2).Result.Milliseconds);
            }
            var queryCounters = counters.Read().Since(before);
            Require(queryCounters.Starts == 0 && queryCounters.Steps == 0 && queryCounters.Samples == 0, "Performance query synthesized audio.");
            File.WriteAllText(Path.Combine(directory, "full-phoneme-performance.json"), JsonSerializer.Serialize(
                new { bank, phones, prepared.PreparationMs, prepared.EncodedNotes, queries = SummarizeTimingLatency(durations), cached = SummarizeTimingLatency(cached), queryCounters },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("phonemes.schedule_reuses=" + NativeSampledTiming.ScheduleReuses);
        return 0;
    }

    private static void ProbeFullPhonemeLocalContext(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        using var fixture = CreateStandaloneFixture(manager, 24, 480, 17003, 14000);
        var target = fixture.Notes[12]; target.SetPhonemes("k a i", true, 0);
        fixture.Sequence.InsertTempo(new VSMRelTick(17003 + 6500), 9437);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, "phoneme-context-baseline.wav"));
        var first = fixture.Notes[0].GetPhonemePositions().ToArray();
        var last = fixture.Notes[^1].GetPhonemePositions().ToArray();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var prepared = backend.PrepareCoefficient(target, 1);
        using var counters = new TimingDseCounters();
        var before = counters.Read();
        var plan = new SampledTimingPlan(target.Phonemes, [new(TimingAdjustment.PhonemeBoundary, 2, (float)(2 * 256d / 44100))]);
        var layout = prepared.QueryPhonemes(plan, 1);
        var queryCounters = counters.Read().Since(before);
        Require(queryCounters.Starts == 0 && queryCounters.Steps == 0 && queryCounters.Samples == 0, "Local context query synthesized audio.");
        Require(layout.Result.ContextNotes < fixture.Notes.Length && layout.Anchors[0].From > 0, "Local context test did not exercise a shifted window.");
        SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
        NativeSampledTiming.Publish(fixture.Part, 10000, new Dictionary<nint, float>(), layout.Anchors);
        try
        {
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, "phoneme-context-changed.wav"));
            Require(target.GetPhonemePositions().Select(v => (long)v).SequenceEqual(layout.Result.Boundaries), "Local-to-Part anchor conversion differs from render.");
            Require(first.SequenceEqual(fixture.Notes[0].GetPhonemePositions()) && last.SequenceEqual(fixture.Notes[^1].GetPhonemePositions()), "Local phoneme control changed remote boundaries.");
            Require(NativeSampledTiming.ReadStatus((nint)fixture.Part).Failures == 0, "Shifted phoneme route failed.");
            File.WriteAllText(Path.Combine(directory, "full-phoneme-context.json"), JsonSerializer.Serialize(new
            {
                partStart = 17003,
                totalNotes = fixture.Notes.Length,
                layout.Result.ContextNotes,
                layout.Result.PlannerRuns,
                layout.Result.Boundaries,
                firstAnchor = layout.Anchors[0],
                queryCounters,
                remoteBoundariesUnchanged = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { NativeSampledTiming.Remove((nint)fixture.Part); }
    }

    private static void ProbeFullPhonemeRouting(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        using var fixture = CreateStandaloneFixture(manager, 3, 480, 0, 4000);
        var parts = new List<WIVSMMidiPart> { fixture.Part };
        string[] banks = ["BL8CEAM5N4XN3LFK", "BCNFCY43LB2LZCD4", "BL8CEAM5N4XN3LFK", "BKKP765AEHXWSKDB"];
        for (int p = 1; p < 4; p++)
        {
            var track = (WIVSMMidiTrack)fixture.Sequence.InsertTrackEx((ulong)p, VSMTrackType.Midi, "phonemes")!;
            var part = track.InsertPart(new VSMAbsTick(p < 2 ? 0 : 17003), new VSMRelTick(4000), "phonemes")!;
            for (int n = 0; n < 3; n++) part.InsertNote(new VSMRelTick(480 + n * 480), new VSMNoteEvent(480, 60, 64),
                part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "phonemes", "k a i", true, 0);
            parts.Add(part);
        }
        for (int p = 0; p < 4; p++)
        {
            parts[p].SetVoiceBankID(banks[p]);
            foreach (var note in parts[p].NotesEnumerator) note.SetPhonemes(p % 2 == 0 ? "l a i" : "k a i", true, p % 2 == 0 ? 4 : 0);
        }
        fixture.Sequence.InsertTempo(new VSMRelTick(17003 + 1100), 9437);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        var expected = new Dictionary<nint, long[]>();
        var results = new List<object>();
        try
        {
            for (int p = 0; p < 4; p++)
            {
                var notes = parts[p].NotesEnumerator.ToArray();
                var plans = new Dictionary<nint, SampledTimingPlan>();
                for (int n = 0; n < 2; n++) plans[(nint)notes[n]] = new(notes[n].Phonemes,
                    [new(TimingAdjustment.NoteCoefficient,-1,p%2==0?.5f:.375f),
                     new(TimingAdjustment.PhonemeBoundary,2,(float)((p%2==0?2:-2)*256d/44100))]);
                var anchors = new SortedDictionary<int, int>();
                foreach (var note in notes.Take(2))
                {
                    Console.WriteLine($"phonemes.routing_query={p}/{note.RelPosTick.Value}");
                    using var prepared = backend.PrepareCoefficient(note, 1,
                        neighbor => plans.GetValueOrDefault((nint)neighbor)?.PhonemeCoefficient(neighbor.Phonemes),
                        neighbor => plans.GetValueOrDefault((nint)neighbor));
                    var layout = prepared.QueryPhonemes(plans[(nint)note], 1);
                    expected[(nint)note] = layout.Result.Boundaries;
                    foreach (var point in layout.Anchors)
                    {
                        Require(!anchors.TryGetValue(point.From, out int old) || old == point.To, "Context windows disagree on phoneme controls.");
                        anchors[point.From] = point.To;
                    }
                }
                NativeSampledTiming.Publish(parts[p], (ulong)(100 + p), plans.ToDictionary(v => v.Key, v => v.Value.PhonemeCoefficient(v.Value.Topology)!.Value),
                    anchors.Select(v => new NativePhonemeTiming.Point(v.Key, v.Value)).ToArray());
            }
            using var observer = new WIVSMRendererObserver();
            var completed = new HashSet<nint>();
            observer.Completed += (_, e) => completed.Add((nint)e.MidiPart);
            fixture.Sequence.AddRendererObserver(observer);
            try
            {
                fixture.Sequence.StartAsyncRendering();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (completed.Count < parts.Count && watch.Elapsed.TotalSeconds < 30)
                { while (fixture.Sequence.InvokeRendererObserver()) { } Thread.Sleep(10); }
                Require(completed.Count == parts.Count, "Concurrent phoneme render timed out.");
                for (int p = 0; p < parts.Count; p++)
                {
                    var status = NativeSampledTiming.ReadStatus((nint)parts[p]);
                    Require(status.Failures == 0 && status.Applied > 0, "Concurrent phoneme route failed.");
                    foreach (var note in parts[p].NotesEnumerator.Take(2))
                    {
                        var actual = note.GetPhonemePositions().Select(v => (long)v).ToArray();
                        Console.WriteLine("phonemes.concurrent=" + JsonSerializer.Serialize(new { part = p, predicted = expected[(nint)note], actual, status.Failures }));
                        Require(actual.SequenceEqual(expected[(nint)note]), "Concurrent phoneme boundary mismatch.");
                        results.Add(new { part = p, bank = banks[p], predicted = expected[(nint)note], actual, status.Failures });
                    }
                }
                results.Add(new { concurrentParts = parts.Count, peakScopes = NativeSampledTiming.PeakScopes });
            }
            finally { fixture.Sequence.StopAsyncRendering(); fixture.Sequence.RemoveRendererObserver(observer); }
        }
        finally { foreach (var part in parts) NativeSampledTiming.Remove((nint)part); }
        File.WriteAllText(Path.Combine(directory, "full-phoneme-routing.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}

