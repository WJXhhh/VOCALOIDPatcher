using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.DurationSelection;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeDurationRouting(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string library = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        using var counters = new TimingDseCounters();
        var initialize = Stopwatch.StartNew();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        double initializationMs = initialize.Elapsed.TotalMilliseconds;
        int count = Environment.GetEnvironmentVariable("VOCALOID_DURATION_ROUTE_SINGLE") == "1" ? 1 : 2;
        string phones = Environment.GetEnvironmentVariable("VOCALOID_DURATION_ROUTE_PHONES") ?? "k a i";
        string bank = Environment.GetEnvironmentVariable("VOCALOID_DURATION_ROUTE_BANK") ?? "BCNFCY43LB2LZCD4";
        using var fixture = CreateStandaloneFixture(manager, count, 960, 0, 3840);
        fixture.Part.SetVoiceBankID(bank);
        for (int i = 0; i < count; i++) fixture.Part.MoveNote(new VSMRelTick(count == 1 ? 960 : i * 960), fixture.Notes[i]);
        foreach (var n in fixture.Notes) n.SetPhonemes(phones, true, bank == "BL8CEAM5N4XN3LFK" ? 4 : 0);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        string baselinePath = Path.Combine(directory, "baseline.wav");
        RenderTransitionWithScore(fixture.Sequence, fixture.Part, baselinePath);
        int[][] baseline = fixture.Notes.Select(n => n.GetPhonemePositions().ToArray()).ToArray();
        string baselineHash = TimingPcmHash(baselinePath);
        NativeDurationSelection.Initialize(library);
        using var prepared = backend.PrepareCoefficient(fixture.Notes[0], 1);
        var empty = new Dictionary<nint, (float, uint)>();
        var before = counters.Read(); var watch = Stopwatch.StartNew();
        var contract = prepared.CaptureDurationContract(NativeDurationSelection.Notes(fixture.Part, empty, true), 1);
        var query = counters.Read().Since(before);
        Console.WriteLine($"duration.capture events={contract.Length}, ms={watch.Elapsed.TotalMilliseconds}, audio={query}");
        Require(query.Starts == 0 && query.Steps == 0 && query.Samples == 0, "Duration capture synthesized audio.");
        var samples = new List<double>();
        var capturedNotes = NativeDurationSelection.Notes(fixture.Part, empty, true);
        before = counters.Read();
        for (int i = 0; i < 200; i++)
        {
            watch.Restart(); _ = prepared.CaptureDurationContract(capturedNotes, 1); samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        var warmAudio = counters.Read().Since(before); samples.Sort();
        Require(warmAudio == new TimingDseSnapshot(0, 0, 0), "Warm duration planning synthesized audio.");
        var performance = new { initializationMs, preparationMs = prepared.PreparationMs, runs = samples.Count,
            medianMs = samples[99], p95Ms = samples[189], maxMs = samples[^1], warmAudio };
        ulong epoch = 0;
        var rows = new List<object>();
        foreach (uint policy in new uint[] { 0, 1 })
        {
            var repeats = new Dictionary<string, string>();
            foreach (var ratios in new[] { new[] { 1f, 1f }, new[] { .5f, 2f }, new[] { 2f, .5f }, new[] { .37151927f, .37151927f }, new[] { 5.9443083f, .023219954f }, new[] { .9999f, 1.0001f }, new[] { 1.0001f, .9999f }, new[] { .5f, 2f }, new[] { 1f, 1f } })
            {
                var controls = fixture.Notes.Select((n, i) => (n, i)).ToDictionary(v => (nint)v.n, v => (ratios[v.i], policy));
                NativeSampledTiming.Publish(fixture.Part, ++epoch, new Dictionary<nint, float>());
                NativeDurationSelection.Publish((nint)fixture.Part, epoch, NativeDurationSelection.Notes(fixture.Part, controls, true), contract);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                string path = Path.Combine(directory, $"{policy}-{rows.Count}.wav");
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, path);
                var status = NativeDurationSelection.ReadStatus((nint)fixture.Part);
                Console.WriteLine($"duration.render {policy}: {string.Join(',', ratios)}; {status}");
                var actual = fixture.Notes.Select(n => n.GetPhonemePositions().ToArray()).ToArray();
                string hash = TimingPcmHash(path), key = string.Join(',', ratios);
                bool timing = actual.Zip(baseline).All(v => v.First.SequenceEqual(v.Second));
                bool repeat = !repeats.TryGetValue(key, out string? previous) || previous == hash;
                repeats[key] = hash;
                rows.Add(new { policy, ratios, hash, timing, repeat, baselineMatch = hash == baselineHash, status = status.ToString(), actual });
                File.WriteAllText(Path.Combine(directory, "routing.json"), JsonSerializer.Serialize(new { baseline, baselineHash, contractEvents = contract.Length, query, performance, rows }, new JsonSerializerOptions { WriteIndented = true }));
                Require(status.Failures == 0 && status.Contracts > 0 && status.Scores > 0, "Native duration route failed.");
                Require(timing && repeat, "Duration timing or restoration differed.");
            }
        }
        NativeDurationSelection.Remove((nint)fixture.Part); NativeSampledTiming.Remove((nint)fixture.Part);
        return 0;
    }
}
