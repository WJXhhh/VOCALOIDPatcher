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
    private static int ProbeDurationConcurrent(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        using var counters = new TimingDseCounters();
        NativeDurationSelection.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        var fixtures = Enumerable.Range(0, 4).Select(_ => CreateStandaloneFixture(manager, 1, 960, 0, 3840)).ToArray();
        var expected = new string[4]; var completed = new int[4]; var observed = new List<object>();
        var observers = new List<WIVSMRendererObserver>();
        ulong epoch = 10000;
        try
        {
            for (int i = 0; i < fixtures.Length; i++)
            {
                var f = fixtures[i]; f.Part.MoveNote(new VSMRelTick(960), f.Notes[0]);
                f.Notes[0].SetPhonemes("k a", true, 0); f.Notes[0].SetNoteNumber(60 + i);
                f.Sequence.InterpolatedMasterVolume(); f.Sequence.Commit(false);
                // The fourth renderer remains wholly unmodified.
                if (i < 3)
                {
                    var controls = new Dictionary<nint, (float, uint)> { [(nint)f.Notes[0]] = (new[] { .5f, 2f, .37151927f }[i], 1) };
                    using var p = backend.PrepareCoefficient(f.Notes[0], i + 1);
                    var contract = p.CaptureDurationContract(NativeDurationSelection.Notes(f.Part, controls, true), i + 1);
                    NativeSampledTiming.Publish(f.Part, ++epoch, new Dictionary<nint, float>());
                    NativeDurationSelection.Publish((nint)f.Part, epoch, NativeDurationSelection.Notes(f.Part, controls, true), contract);
                }
                else
                {
                    RenderTransitionWithScore(f.Sequence, f.Part, Path.Combine(directory, "ordinary-cold.wav"));
                    SampledTimingVsmBinding.InvalidateRenderedPart(f.Part, f.Notes);
                }
                string reference = Path.Combine(directory, "reference-" + i + ".wav");
                RenderTransitionWithScore(f.Sequence, f.Part, reference); expected[i] = TimingPcmHash(reference);
            }
            for (int pass = 0; pass < 3; pass++)
            {
                Array.Clear(completed);
                observers.Clear();
                for (int i = 0; i < fixtures.Length; i++)
                {
                    int index = i; var f = fixtures[i];
                    SampledTimingVsmBinding.InvalidateRenderedPart(f.Part, f.Notes);
                    var observer = new WIVSMRendererObserver();
                    observer.Completed += (_, e) => { if ((nint)e.MidiPart == (nint)f.Part) completed[index] = 1; };
                    observer.Canceled += (_, e) => { if ((nint)e.MidiPart == (nint)f.Part) completed[index] = -1; };
                    Require(f.Sequence.AddRendererObserver(observer), "Cannot attach duration concurrent observer.");
                    observers.Add(observer); f.Sequence.BlockRenderingEnabled = false;
                }
                foreach (var f in fixtures) f.Sequence.StartAsyncRendering();
                var watch = Stopwatch.StartNew();
                while (completed.Any(v => v == 0) && watch.ElapsedMilliseconds < 30000)
                {
                    foreach (var f in fixtures) for (int i = 0; i < 8 && f.Sequence.InvokeRendererObserver(); i++) { }
                    Thread.Sleep(1);
                }
                for (int i = 0; i < fixtures.Length; i++)
                {
                    var f = fixtures[i]; f.Sequence.StopAsyncRendering(); f.Sequence.RemoveRendererObserver(observers[i]);
                    string hash = completed[i] == 1 ? TimingPcmHash(f.Part.WaveFilePath) : "unavailable";
                    var status = i < 3 ? NativeDurationSelection.ReadStatus((nint)f.Part) : default;
                    observed.Add(new { pass, part = i, completed = completed[i], hash, expected = expected[i], equal = hash == expected[i], status = status.ToString(), peakScopes = NativeSampledTiming.PeakScopes });
                }
                foreach (var observer in observers) observer.Dispose(); observers.Clear();
                File.WriteAllText(Path.Combine(directory, "concurrent.json"), JsonSerializer.Serialize(observed, new JsonSerializerOptions { WriteIndented = true }));
                Require(completed.All(v => v == 1), "Concurrent duration renderer canceled or timed out.");
                // Compare the ordinary neighbor against its own sequential
                // warm reference, with exactly the same preceding native state.
                for (int i = 0; i < 4; i++) Require(TimingPcmHash(fixtures[i].Part.WaveFilePath) == expected[i] &&
                    (i == 3 || NativeDurationSelection.ReadStatus((nint)fixtures[i].Part).Failures == 0), "Duration state leaked between concurrent Parts.");
                Require(NativeSampledTiming.PeakScopes > 1, "Concurrent test did not overlap native render scopes.");
                Console.WriteLine($"duration.concurrent pass={pass}, peak={NativeSampledTiming.PeakScopes}, completed=4, exact=4");
            }
        }
        finally
        {
            foreach (var f in fixtures)
            {
                f.Sequence.StopAsyncRendering();
                NativeDurationSelection.Remove((nint)f.Part); NativeSampledTiming.Remove((nint)f.Part); f.Dispose();
            }
            foreach (var observer in observers) observer.Dispose();
        }
        return 0;
    }
}
