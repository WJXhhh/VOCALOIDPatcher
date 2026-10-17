using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeDynamicsSelection(WIVSMSequenceManager manager, string directory)
    {
        Directory.CreateDirectory(directory);
        LocalTimingNative.VerifyImages();
        using var capture = new TimingCoefficientHook();
        using var filter = new DurationCandidateHook { CaptureBounds = true }; // Observes; PitchWidth remains null.
        var report = new List<object>();
        foreach (var test in new[] { ("chinese-a", "BL8CEAM5N4XN3LFK", "a", 4),
            ("chinese-pha", "BL8CEAM5N4XN3LFK", "p_h a", 4),
            ("miku-a", "BCNFCY43LB2LZCD4", "a", 0),
            ("miku-ka", "BCNFCY43LB2LZCD4", "k a", 0) })
        {
            using var fixture = CreateStandaloneFixture(manager, 1, 960, 0, 3840);
            fixture.Part.SetVoiceBankID(test.Item2);
            var note = fixture.Notes[0];
            fixture.Part.MoveNote(new VSMRelTick(960), note);
            note.SetNoteNumber(60); note.NoteVelocity = 64;
            note.SetPhonemes(test.Item3, true, test.Item4);
            fixture.Sequence.InterpolatedMasterVolume();
            var controller = fixture.Part.InsertController(VSMRelTick.Zero, VSMControllerType.Dynamics, 64)
                ?? throw new InvalidOperationException("DYN controller insertion failed.");
            fixture.Sequence.Commit(false);
            var ids = new Dictionary<nint, int>();
            string? baseline = null;
            var hashes = new Dictionary<int, string>();
            foreach (int dynamics in new[] { 16, 32, 64, 96, 127, 64 })
            {
                controller.Value = dynamics;
                Require(controller.Value == dynamics, "DYN controller update failed.");
                fixture.Sequence.Commit(false);
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                capture.Reset(-1, false, null); filter.Reset();
                int consumed = 0;
                var actual = new Dictionary<int, DurationRow>();
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, 0x24158);
                    foreach (var row in ReadDurationRows(state, ids, consumed, cursor)) actual[row.Begin] = row;
                    consumed = cursor;
                };
                string wave = Path.Combine(directory, test.Item1 + "-" + report.Count + ".wav");
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, wave);
                capture.AfterStep = null; capture.Check();
                var metrics = ReadWaveMetrics(wave);
                Require(metrics.Peak > .00001 && filter.Calls > 0 && actual.Count > 0, "DYN probe has no valid audio or native selection.");
                string signature = string.Join(';', actual.Values.Where(r => r.To != -1).OrderBy(r => r.Begin)
                    .Select(r => $"{r.From}>{r.To}/{r.Material.Id}/{r.Stage}/{r.Reverse}"));
                baseline ??= signature;
                string hash = TimingPcmHash(wave);
                bool repeated = !hashes.TryGetValue(dynamics, out string? previous) || previous == hash;
                Require(repeated, "DYN restoration changed PCM.");
                hashes[dynamics] = hash;
                report.Add(new { fixture = test.Item1, dynamics, sameArt = signature == baseline, repeated, hash, metrics,
                    selected = actual.Values.OrderBy(r => r.Begin).ToArray(), selectionBounds = filter.Bounds.Distinct().ToArray() });
                File.WriteAllText(Path.Combine(directory, "dynamics-selection.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"dynamics={test.Item1}/{dynamics}; sameArt={signature == baseline}; RMS={metrics.Rms:F6}; filterCalls={filter.Calls}");
            }
        }
        return 0;
    }
}
