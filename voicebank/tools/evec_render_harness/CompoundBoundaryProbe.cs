using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeCompoundBoundaries(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string nativePath = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativePhonemeTiming.Initialize(nativePath);
        NativeSampledTiming.Initialize(nativePath);
        if (Environment.GetEnvironmentVariable("VOCALOID_COMPOUND_CONCURRENT") == "1")
            return ProbeCompoundConcurrency(manager, dse, database, directory);
        using var fixture = CreateCompoundFixture(manager);
        var part = fixture.Part;
        var source = fixture.Sequence;
        string[] phones = ["x u uo", "s t ei AU", "x aI AU", "t_h i@U u", "k o"];
        RenderTransitionWithScore(source, part, Path.Combine(directory, "baseline.wav"));
        SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
        RenderTransitionWithScore(source, part, Path.Combine(directory, "baseline.wav"));
        string baselineHash = TimingPcmHash(Path.Combine(directory, "baseline.wav"));
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        using var schedule = new MetadataTimingReplay(counters.VtableRva);
        var results = new List<object>();
        bool failed = false;
        int rejected = 0;
        ulong epoch = 0;
        var caseFilter = Environment.GetEnvironmentVariable("VOCALOID_COMPOUND_CASES")?.Split(';').ToHashSet();
        for (int n = 0; n < phones.Length; n++)
        {
            if (caseFilter != null && !caseFilter.Any(c => c.StartsWith(n + ":", StringComparison.Ordinal))) continue;
            using var prepared = backend.PrepareCoefficient(fixture.Notes[n], 1);
            var before = counters.Read();
            object? layout = null;
            string? error = null;
            try { layout = prepared.QueryPhonemes(new SampledTimingPlan(phones[n], []), 1); }
            catch (NotSupportedException exception) { error = exception.Message; failed = true; }
            var queryCounters = counters.Read().Since(before);
            Require(queryCounters.Starts == 0 && queryCounters.Steps == 0 && queryCounters.Samples == 0,
                "Compound query synthesized audio.");
            var result = new { n, phones = phones[n], actual = fixture.Notes[n].GetPhonemePositions(), layout, error,
                queryCounters, evidence = prepared.CaptureCompoundEvidence() };
            results.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(new { n, error, queryCounters }));
            if (error != null) continue;
            int axes = phones[n].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1;
            for (int axis = 0; axis < axes; axis++)
                foreach (int offset in new[] { -1, 1 })
                {
                    if (caseFilter != null && !caseFilter.Contains($"{n}:{axis}:{offset}")) continue;
                    before = counters.Read();
                    var plan = new SampledTimingPlan(phones[n],
                        [new(TimingAdjustment.PhonemeBoundary, axis, (float)(offset * 256d / 44100))]);
                    LocalConsonantTimingBackend.PhonemeLayout candidate;
                    try { candidate = prepared.QueryPhonemes(plan, 1, editedBoundary: axis); }
                    catch (NotSupportedException exception)
                    {
                        var rejection = new { n, axis, offset, error = exception.Message };
                        results.Add(rejection);
                        Console.WriteLine(JsonSerializer.Serialize(rejection));
                        rejected++;
                        // Preserve native rejections separately from rendered successes.
                        // These are not counted as successful boundary edits.
                        continue;
                    }
                    queryCounters = counters.Read().Since(before);
                    Require(queryCounters.Starts == 0 && queryCounters.Steps == 0 && queryCounters.Samples == 0,
                        "Compound candidate synthesized audio.");
                    SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
                    NativeSampledTiming.Publish(part, ++epoch, new Dictionary<nint, float>(), candidate.Anchors);
                    string wav = Path.Combine(directory, "candidate.wav");
                    try
                    {
                        schedule.Capture();
                        RenderTransitionWithScore(source, part, wav);
                        schedule.Check();
                        var status = NativeSampledTiming.ReadStatus((nint)part);
                        var actual = NativeTimingPositions.Read(fixture.Notes[n], false);
                        bool rawMatches = actual.Select(v => (long)v).SequenceEqual(candidate.Result.Boundaries);
                        int firstVowel = Array.FindIndex(phones[n].Split(' ', StringSplitOptions.RemoveEmptyEntries), fixture.Notes[n].IsVowel);
                        bool needsProjection = actual.Length == 0 && axis == firstVowel && offset > 0;
                        bool sampleMatches = candidate.Anchors.Skip(1).Take(candidate.Anchors.Length - 2)
                            .All(p => schedule.SampleBoundaries.Contains(p.To));
                        bool audioChanged = TimingPcmHash(wav) != baselineHash && ReadWaveMetrics(wav).Peak > .00001;
                        var editResult = new { n, axis, offset, candidate.Result.Boundaries, actual, rawMatches, needsProjection,
                            sampleMatches, audioChanged, status.Failures, status.Scopes, queryCounters };
                        results.Add(editResult);
                        Console.WriteLine(JsonSerializer.Serialize(editResult));
                        failed |= !(rawMatches || needsProjection) || !sampleMatches || !audioChanged || status.Failures != 0 || status.Scopes == 0;
                    }
                    finally { NativeSampledTiming.Remove((nint)part); }
                }
        }
        SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
        RenderTransitionWithScore(source, part, Path.Combine(directory, "restored.wav"));
        bool restored = TimingPcmHash(Path.Combine(directory, "restored.wav")) == baselineHash;
        results.Add(new { restored });
        failed |= !restored;
        File.WriteAllText(Path.Combine(directory, "compound-boundaries.json"), JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }));
        // A native rejection keeps the full matrix red, even if the baseline
        // lock and all other edits passed. Do not report it as a valid edit.
        return failed || rejected != 0 ? 1 : 0;
    }

    private static StandaloneFixture CreateCompoundFixture(WIVSMSequenceManager manager)
    {
        var fixture = CreateStandaloneFixture(manager, 5, 480, 0, 7680);
        try
        {
            fixture.Part.SetVoiceBankID("BETDB8W6KWZPYEB9");
            fixture.Sequence.InsertTempo(VSMRelTick.Zero, 14600);
            string[] phones = ["x u uo", "s t ei AU", "x aI AU", "t_h i@U u", "k o"];
            int[] positions = [480, 840, 1440, 1920, 2640];
            int[] durations = [360, 600, 480, 720, 240];
            int[] pitches = [76, 79, 81, 82, 82];
            for (int i = 0; i < phones.Length; i++)
            {
                var note = fixture.Notes[i];
                fixture.Part.MoveNote(new VSMRelTick(positions[i]), note);
                note.SetDuration(new VSMRelTick(durations[i]));
                note.SetNoteNumber(pitches[i]);
                note.NoteVelocity = 64;
                note.SetPhonemes(phones[i], true, 4);
            }
            fixture.Sequence.InterpolatedMasterVolume();
            fixture.Sequence.Commit(false);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static int ProbeCompoundConcurrency(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        var fixtures = Enumerable.Range(0, 4).Select(_ => CreateCompoundFixture(manager)).ToArray();
        var references = new string[4];
        var referencePositions = new int[4][][];
        var completed = new int[4];
        var observers = new List<WIVSMRendererObserver>();
        var results = new List<object>();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        bool ordinary = Environment.GetEnvironmentVariable("VOCALOID_COMPOUND_CONCURRENT_CONTROL") == "ordinary";
        bool allExact = true;
        try
        {
            for (int i = 0; i < fixtures.Length; i++)
            {
                var f = fixtures[i];
                // Distinct pitches also expose accidental cross-Part routing.
                foreach (var note in f.Notes) note.SetNoteNumber(note.NoteNumber - i);
                f.Sequence.Commit(false);
                if (i < 3 && !ordinary)
                {
                    var note = f.Notes[1];
                    using var prepared = backend.PrepareCoefficient(note, 1);
                    var plan = new SampledTimingPlan(note.Phonemes,
                        [new(TimingAdjustment.PhonemeBoundary, i == 1 ? 2 : 1, (float)((i == 1 ? -1 : 1) * 256d / 44100))]);
                    var layout = prepared.QueryPhonemes(plan, 1, editedBoundary: i == 1 ? 2 : 1);
                    NativeSampledTiming.Publish(f.Part, (ulong)(51000 + i), new Dictionary<nint, float>(), layout.Anchors);
                }
                string path = Path.Combine(directory, "reference-" + i + ".wav");
                RenderTransitionWithScore(f.Sequence, f.Part, path);
                SampledTimingVsmBinding.InvalidateRenderedPart(f.Part, f.Notes);
                RenderTransitionWithScore(f.Sequence, f.Part, path);
                references[i] = TimingPcmHash(path);
                referencePositions[i] = f.Notes.Select(n => NativeTimingPositions.Read(n, false)).ToArray();
            }
            for (int pass = 0; pass < 3; pass++)
            {
                Array.Clear(completed);
                for (int i = 0; i < fixtures.Length; i++)
                {
                    int index = i;
                    var f = fixtures[i];
                    SampledTimingVsmBinding.InvalidateRenderedPart(f.Part, f.Notes);
                    var observer = new WIVSMRendererObserver();
                    observer.Completed += (_, e) => { if ((nint)e.MidiPart == (nint)f.Part) completed[index] = 1; };
                    observer.Canceled += (_, e) => { if ((nint)e.MidiPart == (nint)f.Part) completed[index] = -1; };
                    Require(f.Sequence.AddRendererObserver(observer), "Cannot attach compound observer.");
                    observers.Add(observer);
                    f.Sequence.BlockRenderingEnabled = false;
                }
                foreach (var f in fixtures) f.Sequence.StartAsyncRendering();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (completed.Any(v => v == 0) && watch.ElapsedMilliseconds < 30000)
                {
                    foreach (var f in fixtures)
                        for (int j = 0; j < 8 && f.Sequence.InvokeRendererObserver(); j++) { }
                    Thread.Sleep(1);
                }
                for (int i = 0; i < fixtures.Length; i++)
                {
                    var f = fixtures[i];
                    f.Sequence.StopAsyncRendering();
                    f.Sequence.RemoveRendererObserver(observers[i]);
                    string hash = completed[i] == 1 ? TimingPcmHash(f.Part.WaveFilePath) : "unavailable";
                    if (completed[i] == 1) File.Copy(f.Part.WaveFilePath, Path.Combine(directory, "concurrent-" + i + ".wav"), true);
                    var status = i < 3 ? NativeSampledTiming.ReadStatus((nint)f.Part) : default;
                    var positions = f.Notes.Select(n => NativeTimingPositions.Read(n, false)).ToArray();
                    bool boundariesMatch = positions.Zip(referencePositions[i]).All(p => p.First.SequenceEqual(p.Second));
                    results.Add(new { pass, part = i, completed = completed[i], hash, expected = references[i],
                        equal = hash == references[i], path = f.Part.WaveFilePath, status.Failures, status.Applied, status.Scopes,
                        positions, referencePositions = referencePositions[i], boundariesMatch,
                        cursor = i < 3 ? NativeSampledTiming.PhonemeDiagnostic((nint)f.Part) : null,
                        peakScopes = NativeSampledTiming.PeakScopes });
                }
                foreach (var observer in observers) observer.Dispose();
                observers.Clear();
                File.WriteAllText(Path.Combine(directory, "compound-concurrent.json"),
                    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Require(completed.All(v => v == 1), "Compound concurrent rendering canceled or timed out.");
                for (int i = 0; i < fixtures.Length; i++)
                {
                    allExact &= TimingPcmHash(fixtures[i].Part.WaveFilePath) == references[i];
                    Require(i == 3 || NativeSampledTiming.ReadStatus((nint)fixtures[i].Part).Failures == 0,
                        "Compound concurrent native scope failed.");
                    Require(fixtures[i].Notes.Select((n, index) => NativeTimingPositions.Read(n, false)
                        .SequenceEqual(referencePositions[i][index])).All(equal => equal),
                        "Compound concurrent boundaries differ from reference.");
                }
                if (!ordinary) Require(NativeSampledTiming.PeakScopes > 1, "Compound render scopes did not overlap.");
                Console.WriteLine("compound.concurrent=" + pass);
            }
            // Keep PCM nondeterminism visible even when all native routes and
            // boundaries passed. The ordinary control uses the same criterion.
            return allExact ? 0 : 1;
        }
        finally
        {
            for (int i = 0; i < fixtures.Length; i++)
            {
                fixtures[i].Sequence.StopAsyncRendering();
                if (i < observers.Count) fixtures[i].Sequence.RemoveRendererObserver(observers[i]);
                NativeSampledTiming.Remove((nint)fixtures[i].Part);
                fixtures[i].Dispose();
            }
            foreach (var observer in observers) observer.Dispose();
        }
    }
}

namespace VOCALOIDPatcher.ConsonantTiming
{
    internal sealed partial class LocalConsonantTimingBackend
    {
        internal sealed partial class PreparedCoefficientTiming
        {
            internal object CaptureCompoundEvidence()
            {
                var window = _windows.Last();
                _owner._engine.Begin(default, 50);
                try
                {
                    _owner._engine.Plan(0, _singer);
                    foreach (var message in window.Messages)
                        _owner._engine.PlanNote(message.Frame, message.Bytes, message.Coefficient);
                    return new { window.Origin, window.Target, rows = _owner._engine.ReadTransitions(window.Origin)
                        .Select(s => new { s.Begin, s.Split, s.End, s.From, s.To }) };
                }
                finally { _owner._engine.End(); }
            }
        }
    }
}
