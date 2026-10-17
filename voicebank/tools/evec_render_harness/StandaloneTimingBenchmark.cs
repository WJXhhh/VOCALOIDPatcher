using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // No rendering here: golden data was collected separately with normal PCM
    // rendering. All sequences below are metadata-only and owned by this process.
    private static int BenchmarkStandaloneTiming(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        using var counters = new TimingDseCounters();
        var before = counters.Read();
        var cold = Stopwatch.StartNew();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        double initializationMs = cold.Elapsed.TotalMilliseconds;
        var goldenResults = new List<object>();
        var longResults = new List<object>();
        var invalidations = new List<string>();
        var goldenPath = Environment.GetEnvironmentVariable("VOCALOID_TIMING_GOLDEN")
            ?? throw new InvalidOperationException("Set the timing golden fixture path.");
        using var golden = JsonDocument.Parse(File.ReadAllText(goldenPath));
        if (!string.Equals(golden.RootElement.GetProperty("dse_sha256").GetString(),
                "7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5", StringComparison.Ordinal))
            throw new InvalidOperationException("Golden DSE hash differs.");
        var latency = new List<double>();
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            string phones = item.GetProperty("phonemes").GetString()!;
            bool legato = item.GetProperty("legato").GetBoolean();
            using var fixture = CreateStandaloneFixture(manager, legato ? 2 : 1, 480, 0, 3840);
            var part = fixture.Part;
            var target = fixture.Notes[^1];
            part.MoveNote(new VSMRelTick(960), target);
            target.SetPhonemes(phones, true, 0);
            foreach (var note in fixture.Notes) note.SetNoteNumber(60);
            fixture.Sequence.Commit(false);
            backend.ClearCache();
            var starts = item.GetProperty("starts").EnumerateArray().Select(v => v.GetInt64()).ToArray();
            if (starts.Length != 128) throw new InvalidOperationException("Incomplete golden sweep.");
            for (int velocity = 0; velocity < 128; velocity++)
            {
                var result = backend.Query(target, velocity);
                if (result.Boundaries.Length == 0 || result.Boundaries[0] != starts[velocity])
                    throw new InvalidOperationException($"Golden mismatch: {phones}, legato={legato}, VEL={velocity}.");
                latency.Add(result.Milliseconds);
                goldenResults.Add(new { phones, legato, velocity, onset = result.Boundaries[0],
                    expected = starts[velocity], result.Milliseconds, result.PlannerRuns });
            }
            Console.WriteLine($"standalone.golden={phones};legato={legato};passed=128");
        }

        foreach (int count in new[] { 100, 1000, 10000 })
        {
            using var fixture = CreateStandaloneFixture(manager, count, 120, 17003, count * 120 + 1000000L);
            var target = fixture.Notes[count / 2];
            backend.ClearCache();
            var samples = new List<LocalConsonantTimingResult>();
            // Distinct candidate velocities prevent accidentally benchmarking cache hits.
            for (int i = 0; i < 20; i++) samples.Add(backend.Query(target, (i * 7) % 128));
            if (samples.Any(r => r.CacheHit || r.ContextNotes > 33 || r.PlannerRuns > 5))
                throw new InvalidOperationException("Long query did not stay bounded.");
            longResults.Add(new { sourceNotes = count, emptyTailTicks = 1000000, queryCount = samples.Count,
                latency = SummarizeTimingLatency(samples.Select(r => r.Milliseconds)),
                maxContext = samples.Max(r => r.ContextNotes), maxRuns = samples.Max(r => r.PlannerRuns),
                targetBoundariesAtVelocity0 = samples[0].Boundaries });
            Console.WriteLine($"standalone.long_notes={count};p50_ms={SummarizeTimingLatency(samples.Select(r => r.Milliseconds)).P50:F3}");
        }

        using var editing = CreateStandaloneFixture(manager, 3, 480, 0, 10000);
        var editingTarget = editing.Notes[1];
        // Move the small context well after the tempo edit, preserving a short window.
        foreach (var note in editing.Notes) editing.Part.MoveNote(new VSMRelTick(note.RelPosTick.Value + 4800), note);
        editing.Sequence.Commit(false);
        backend.ClearCache();
        var baseline = backend.Query(editingTarget, 64);
        var cached = backend.Query(editingTarget, 64);
        if (!cached.CacheHit) throw new InvalidOperationException("Expected a cached result.");
        cached.Boundaries[0] = long.MaxValue;
        if (!backend.Query(editingTarget, 64).Boundaries.SequenceEqual(baseline.Boundaries))
            throw new InvalidOperationException("Returned arrays alias the cache.");
        void CheckInvalidation(string name, Action mutate)
        {
            mutate();
            editing.Sequence.Commit(false);
            var changed = backend.Query(editingTarget, 64);
            if (changed.CacheHit) throw new InvalidOperationException($"Cache did not invalidate: {name}.");
            backend.ClearCache();
            var fresh = backend.Query(editingTarget, 64);
            if (!changed.Boundaries.SequenceEqual(fresh.Boundaries))
                throw new InvalidOperationException($"Changed cache differs from fresh query: {name}.");
            invalidations.Add(name);
        }
        CheckInvalidation("neighbor velocity", () => editing.Notes[0].NoteVelocity = 16);
        CheckInvalidation("neighbor pitch", () => editing.Notes[0].SetNoteNumber(67));
        CheckInvalidation("neighbor expression", () => editing.Notes[0].SetNoteExpression(new VSMNoteExpression(0, 0, 0, 0, 0, true, true)));
        CheckInvalidation("target phonemes", () => editingTarget.SetPhonemes("h e", true, 0));
        CheckInvalidation("target duration", () => editingTarget.SetDuration(new VSMRelTick(450)));
        CheckInvalidation("target position", () => editing.Part.MoveNote(new VSMRelTick(editingTarget.RelPosTick.Value + 1), editingTarget));
        CheckInvalidation("earlier tempo frame phase", () => editing.Sequence.InsertTempo(new VSMRelTick(1000), 9437));
        CheckInvalidation("in-window tempo", () => editing.Sequence.InsertTempo(new VSMRelTick(editingTarget.AbsPosTick.Value + 100), 15000));
        CheckInvalidation("bank", () => editing.Part.SetVoiceBankID("BKKP765AEHXWSKDB"));

        var canceledBefore = counters.Read();
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { backend.Query(editingTarget, 64, canceled.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        }
        var canceledDelta = counters.Read().Since(canceledBefore);
        if (canceledDelta.Starts != 0 || canceledDelta.Steps != 0 || canceledDelta.Samples != 0)
            throw new InvalidOperationException("Pre-canceled query entered the native engine.");
        try { backend.Query(editingTarget, 128); throw new InvalidOperationException("Invalid VEL accepted."); }
        catch (ArgumentOutOfRangeException) { }

        // Warm JIT and native caches before measuring sustained operation.
        var uncachedSamples = new List<double>();
        for (int i = 0; i < 20; i++) { backend.ClearCache(); backend.Query(editingTarget, i); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var process = Process.GetCurrentProcess(); process.Refresh();
        long privateBefore = process.PrivateMemorySize64;
        int handlesBefore = process.HandleCount, threadsBefore = process.Threads.Count;
        for (int i = 0; i < 300; i++)
        {
            backend.ClearCache();
            uncachedSamples.Add(backend.Query(editingTarget, i % 128).Milliseconds);
        }
        var cachedSamples = new List<double>();
        backend.Query(editingTarget, 64);
        for (int i = 0; i < 2000; i++)
        {
            var result = backend.Query(editingTarget, 64);
            if (!result.CacheHit || result.PlannerRuns != 0) throw new InvalidOperationException("Sustained cache miss.");
            cachedSamples.Add(result.Milliseconds);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
        var sustained = new { uncachedCount = uncachedSamples.Count, uncached = SummarizeTimingLatency(uncachedSamples),
            cachedCount = cachedSamples.Count, cached = SummarizeTimingLatency(cachedSamples),
            privateBytesDelta = process.PrivateMemorySize64 - privateBefore,
            handlesDelta = process.HandleCount - handlesBefore, threadsDelta = process.Threads.Count - threadsBefore };
        // Repeated construction/disposal also exercises engine slot release.
        for (int i = 0; i < 12; i++)
        {
            using var session = new LocalConsonantTimingBackend(manager, dse, database);
            session.Query(editingTarget, 64);
        }
        var delta = counters.Read().Since(before);
        if (delta.Steps != 0 || delta.Samples != 0) throw new InvalidOperationException("Benchmark synthesized audio.");
        File.WriteAllText(Path.Combine(directory, "standalone-benchmark.json"), JsonSerializer.Serialize(new
        {
            initializationMs, goldenComparisons = goldenResults.Count, goldenLatency = SummarizeTimingLatency(latency),
            goldenResults, longResults, invalidations, resultArrayIsolation = true, preCanceled = canceledDelta,
            sustained, disposedSessions = 12, dse = delta,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"standalone.benchmark_complete=True;goldens={goldenResults.Count};pcm_steps={delta.Steps};uncached_p95_ms={sustained.uncached.P95:F3};cached_p95_ms={sustained.cached.P95:F3}");
        return 0;
    }

    private sealed record TimingLatency(double Min, double P50, double P95, double Max);

    private static TimingLatency SummarizeTimingLatency(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return new(sorted[0], sorted[(sorted.Length - 1) / 2], sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
    }

    private sealed record StandaloneFixture(WIVSMSequence Sequence, WIVSMMidiPart Part, WIVSMNote[] Notes) : IDisposable
    {
        public void Dispose() => Sequence.Close();
    }

    private static StandaloneFixture CreateStandaloneFixture(WIVSMSequenceManager manager, int count,
        int duration, long partStart, long partDuration)
    {
        var sequence = manager.CreateSequence(new VSMSequenceData
        { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        try
        {
            sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
            sequence.InsertTempo(VSMRelTick.Zero, 12000);
            var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "benchmark")!;
            var part = track.InsertPart(new VSMAbsTick(partStart), new VSMRelTick(partDuration), "benchmark")!;
            part.SetVoiceBankID("BCNFCY43LB2LZCD4");
            var notes = new WIVSMNote[count];
            for (int i = 0; i < count; i++)
                notes[i] = part.InsertNote(new VSMRelTick(480 + i * duration),
                    new VSMNoteEvent(duration, 60, 64), part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(),
                    "benchmark", i % 3 == 0 ? "a" : i % 3 == 1 ? "k a" : "n a", true, 0)!;
            sequence.Commit(false);
            return new StandaloneFixture(sequence, part, notes);
        }
        catch { sequence.Close(); throw; }
    }
}
