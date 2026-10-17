using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeTimingRouting(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        NativeSampledTiming.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        var results = new List<object>();
        ulong epoch = 0;
        foreach (long start in new[] { 0L, 17003L })
        foreach (int duration in new[] { 120, 480 })
        {
            using var fixture = CreateStandaloneFixture(manager, 3, duration, start, 2880);
            try
            {
                fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
                var coefficients = fixture.Notes.Select((n,i) => (n,i)).ToDictionary(v => (nint)v.n,
                    v => new[] { .00390625f, .18359375f, .736328125f }[v.i]);
                var predictions = fixture.Notes.Select(n => {
                    using var prepared = backend.PrepareCoefficient(n, 1, neighbor => coefficients[(nint)neighbor]);
                    return prepared.Query(coefficients[(nint)n], 1).Boundaries;
                }).ToArray();
                NativeSampledTiming.Publish(fixture.Part, ++epoch, coefficients);
                string path = Path.Combine(directory, $"route-{start}-{duration}.wav");
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, path);
                var status = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                var actual = fixture.Notes.Select(n => n.GetPhonemePositions().Select(v => (long)v).ToArray()).ToArray();
                Console.WriteLine($"routing={start}/{duration};epoch={status.Epoch};scopes={status.Scopes};applied={status.Applied};failures={status.Failures};actual={JsonSerializer.Serialize(actual)};predicted={JsonSerializer.Serialize(predictions)}");
                Require(status.Failures == 0 && status.Applied > 0 && status.Scopes > 0, "Native timing routing failed.");
                Require(actual.Zip(predictions).All(p => p.First.SequenceEqual(p.Second)), "Native timing routing boundary mismatch.");
                results.Add(new { start, duration, status.Epoch, status.Scopes, status.Applied, status.Failures, actual, hash = TimingPcmHash(path) });
            }
            finally { NativeSampledTiming.Remove((nint)fixture.Part); }
        }
        using (var fixture = CreateStandaloneFixture(manager, 3, 480, 0, 2880))
        {
            var parts = new List<WIVSMMidiPart> { fixture.Part };
            for (int p = 1; p < 4; p++)
            {
                var track = (WIVSMMidiTrack)fixture.Sequence.InsertTrackEx((ulong)p, VSMTrackType.Midi, "route")!;
                var part = track.InsertPart(new VSMAbsTick(p < 2 ? 0 : 17003), new VSMRelTick(2880), "route")!;
                part.SetVoiceBankID(p % 2 == 0 ? "BCNFCY43LB2LZCD4" : "BKKP765AEHXWSKDB");
                foreach (var original in fixture.Notes)
                    part.InsertNote(original.RelPosTick, new VSMNoteEvent(480, 60, 64), part.GetDefaultNoteExpression(),
                        part.GetDefaultAiNoteExpression(), "route", original.Phonemes, true, 0);
                parts.Add(part);
            }
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            var predicted = new Dictionary<nint, long[]>();
            float[] controls = [.00390625f, .18359375f, .736328125f, .376953125f];
            for (int p = 0; p < parts.Count; p++)
            {
                var target = parts[p].GetNote(1UL)!;
                using var prepared = backend.PrepareCoefficient(target, 2);
                predicted[(nint)target] = prepared.Query(controls[p], 2).Boundaries;
                NativeSampledTiming.Publish(parts[p], ++epoch, new Dictionary<nint, float> { [(nint)target] = controls[p] });
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
                Require(completed.Count == parts.Count, "Concurrent route render did not complete.");
                foreach (var part in parts)
                {
                    var target = part.GetNote(1UL)!;
                    var s = NativeSampledTiming.ReadStatus((nint)part);
                    Require(s.Failures == 0 && s.Applied > 0, "Concurrent Part control failed.");
                    Require(target.GetPhonemePositions().Select(v => (long)v).SequenceEqual(predicted[(nint)target]), "Concurrent Part coefficient cross-talk.");
                }
                Console.WriteLine($"routing.concurrent_parts={parts.Count};peak_scopes={NativeSampledTiming.PeakScopes}");
                results.Add(new { concurrentParts = parts.Count, peakScopes = NativeSampledTiming.PeakScopes, matching = true });
            }
            finally
            {
                fixture.Sequence.StopAsyncRendering(); fixture.Sequence.RemoveRendererObserver(observer);
                foreach (var part in parts) NativeSampledTiming.Remove((nint)part);
            }
        }
        File.WriteAllText(Path.Combine(directory, "timing-routing.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
