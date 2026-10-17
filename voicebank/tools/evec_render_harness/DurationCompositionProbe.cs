using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.DurationSelection;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeDurationComposition(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string library = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        using var counters = new TimingDseCounters();
        NativeDurationSelection.Initialize(library); NativePhonemeTiming.Initialize(library);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var capture = new TimingCoefficientHook();
        var materials = new Dictionary<nint, int>();
        var report = new List<object>(); ulong epoch = 1000;
        foreach (string scenario in new[] { "vel0", "float", "vel127", "chinese" })
        {
            using var fixture = CreateStandaloneFixture(manager, 2, 960, 0, 4800);
            bool chinese = scenario == "chinese";
            string bank = chinese ? "BL8CEAM5N4XN3LFK" : "BCNFCY43LB2LZCD4";
            string phones = chinese ? "s\\ i_n n" : "k a i";
            fixture.Part.SetVoiceBankID(bank);
            for (int i = 0; i < 2; i++)
            {
                fixture.Part.MoveNote(new VSMRelTick(960 + i * 960), fixture.Notes[i]);
                fixture.Notes[i].SetPhonemes(phones, true, chinese ? 4 : 0);
                fixture.Notes[i].NoteVelocity = scenario == "vel0" ? 0 : scenario == "vel127" ? 127 : 64;
            }
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            var coefficients = fixture.Notes.Select((n, i) => (n, i)).Where(_ => scenario == "float")
                .ToDictionary(v => (nint)v.n, v => v.i == 0 ? .18359375f : .736328125f);
            var plans = fixture.Notes.Select((n, i) => (n, i)).ToDictionary(v => (nint)v.n,
                v => new SampledTimingPlan(phones, [new(TimingAdjustment.PhonemeBoundary, 2, (float)((v.i == 0 ? -2 : 2) * 256d / 44100))]));
            var anchors = new SortedDictionary<int, int>();
            var predicted = new List<long[]>();
            foreach (var n in fixture.Notes)
            {
                using var p = backend.PrepareCoefficient(n, 1, neighbor => coefficients.TryGetValue((nint)neighbor, out float c) ? c : null,
                    neighbor => plans[(nint)neighbor]);
                var layout = p.QueryPhonemes(plans[(nint)n], 1);
                predicted.Add(layout.Result.Boundaries);
                foreach (var point in layout.Anchors)
                {
                    if (anchors.TryGetValue(point.From, out int previous) && previous != point.To)
                    {
                        if (point.From == point.To) continue;
                        Require(previous == point.From, "Conflicting test anchors.");
                    }
                    anchors[point.From] = point.To;
                }
            }
            using var prepared = backend.PrepareCoefficient(fixture.Notes[0], 1,
                neighbor => coefficients.TryGetValue((nint)neighbor, out float c) ? c : null,
                neighbor => plans[(nint)neighbor]);
            var before = counters.Read();
            var time = prepared.CaptureDurationContract(NativeDurationSelection.Notes(fixture.Part, new Dictionary<nint, (float, uint)>(), true), 1);
            Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Combined contract synthesized audio.");
            int[][]? expected = null; string? expectedTransitions = null;
            var repeats = new Dictionary<string, string>();
            foreach (var ratio in new[] { (1f, 1f), (.37151927f, 2f), (2f, .37151927f), (.37151927f, 2f), (1f, 1f) })
            {
                var controls = new Dictionary<nint, (float, uint)> { [(nint)fixture.Notes[0]] = (ratio.Item1, 1), [(nint)fixture.Notes[1]] = (ratio.Item2, 1) };
                NativeSampledTiming.Publish(fixture.Part, ++epoch, coefficients, anchors.Select(p => new NativePhonemeTiming.Point(p.Key, p.Value)).ToArray());
                NativeDurationSelection.Publish((nint)fixture.Part, epoch, NativeDurationSelection.Notes(fixture.Part, controls, true), time);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                capture.Reset(-1, false, null); int consumed = 0;
                var actual = new Dictionary<int, DurationRow>();
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, 0x24158);
                    foreach (var row in ReadDurationRows(state, materials, consumed, cursor)) actual[row.Begin] = row;
                    consumed = cursor;
                };
                string path = Path.Combine(directory, scenario + "-" + report.Count + ".wav");
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, path);
                capture.AfterStep = null; capture.Check();
                var status = NativeDurationSelection.ReadStatus((nint)fixture.Part);
                int[][] boundaries = fixture.Notes.Select(n => n.GetPhonemePositions().ToArray()).ToArray();
                string transitions = string.Join(';', actual.Values.Where(r => r.To != -1).OrderBy(r => r.Begin)
                    .Select(r => $"{r.Begin}:{r.End}:{r.From}:{r.To}:{r.LeftLength}"));
                expected ??= boundaries; expectedTransitions ??= transitions;
                bool equal = boundaries.Zip(expected).All(p => p.First.SequenceEqual(p.Second)) && transitions == expectedTransitions;
                string hash = TimingPcmHash(path), key = ratio.ToString();
                bool repeated = !repeats.TryGetValue(key, out string? previous) || previous == hash;
                repeats[key] = hash;
                report.Add(new { scenario, ratio = new { first = ratio.Item1, second = ratio.Item2 }, status = status.ToString(), equal, repeated, hash, boundaries, predicted, transitions, actual = actual.Values.OrderBy(r => r.Begin).ToArray() });
                File.WriteAllText(Path.Combine(directory, "composition.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"duration.composition={scenario}/{ratio}; equal={equal}; repeat={repeated}; {status}");
                Require(status.Failures == 0 && status.Contracts > 0 && equal && repeated, "Duration/timing composition failed.");
            }
            NativeDurationSelection.Remove((nint)fixture.Part); NativeSampledTiming.Remove((nint)fixture.Part);
        }
        return 0;
    }
}
