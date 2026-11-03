using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeContinuationTiming(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        using var capture = new TimingCoefficientHook();
        var records = new List<object>();
        ulong epoch = 101000;
        int failures = 0;
        foreach (int pitch in new[] { 61, 62, 65 })
        {
            using var fixture = CreateStandaloneFixture(manager, 2, 240, 0, 7680);
            Require(fixture.Part.SetVoiceBankID("BETDB8W6KWZPYEB9"), "Missing live reproduction bank.");
            for (int i = 0; i < 2; i++)
            {
                var note = fixture.Notes[i];
                fixture.Part.MoveNote(new VSMRelTick(1920 + i * 240), note);
                note.SetNoteNumber(i == 0 ? 62 : pitch); note.NoteVelocity = 64;
                note.Lyric = i == 0 ? "mu" : "-";
                note.SetPhonemes(i == 0 ? "m u" : "-", true, 4);
            }
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            string ordinary = Path.Combine(directory, $"pitch{pitch}-ordinary.wav");
            string cold = Path.Combine(directory, $"pitch{pitch}-ordinary-cold.wav");
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, cold);
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, ordinary);
            for (int target = 0; target < 2; target++)
            {
                using var prepared = backend.PrepareCoefficient(fixture.Notes[target], 1);
                var before = counters.Read();
                try
                {
                    var layout = prepared.QueryTransitions(new(fixture.Notes[target].Phonemes, []), 1);
                    var basis = prepared.QueryTransitionControls(layout, new(layout.Fingerprint), 1);
                    var edge = layout.Edges.Single(e => e.Identity.From == "m" && e.Identity.To == "u");
                    var changed = prepared.QueryTransitionControls(layout,
                        TransitionExpressionControl.Progress(new(layout.Fingerprint), edge.Current, edge.Identity, -.203f), 1);
                    var input = layout.Edges.Single(e => e.Identity.From == "Sil");
                    var output = layout.Edges.Single(e => e.Identity.To == "Sil");
                    var combinedControls = changed.RequestedControls!
                        .WithJunction(layout, new(input.Identity, TransitionEndpoint.Begin), input.Current.Begin - 21)
                        .WithJunction(layout, new(edge.Identity, TransitionEndpoint.End), edge.Current.End + 21)
                        .WithJunction(layout, new(output.Identity, TransitionEndpoint.Begin), output.Current.Begin - 8);
                    var combined = prepared.QueryTransitionControls(layout, combinedControls, 1);
                    var encoded = TransitionProjectBinding.Encode(TransitionProjectData.Empty, layout, combinedControls,
                        layout.Notes.ToDictionary(n => n.Address, _ => Guid.NewGuid()));
                    var archive = TransitionArchiveData.Parse(TransitionArchiveData.From(encoded).Payload.Span);
                    Require(archive.Executable && prepared.QueryTransitionControls(layout,
                        TransitionProjectBinding.ResolveAvailable(archive.Data!, layout), 1).Controls.Fingerprint == combined.Controls.Fingerprint,
                        "Continuation control archive roundtrip changed its execution.");
                    var queryCounters = counters.Read().Since(before);
                    Require(queryCounters == new TimingDseSnapshot(0, 0, 0), "Continuation planning synthesized audio.");
                    var renders = new List<object>();
                    string ordinaryHash = TimingPcmHash(ordinary);
                    foreach (var variant in new[] { (Name: "neutral", Plan: basis), (Name: "phase", Plan: changed),
                        (Name: "combined", Plan: combined), (Name: "restored", Plan: basis) })
                    {
                        prepared.PublishTransitions(fixture.Part, ++epoch, variant.Plan, 1);
                        SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                        var observed = new Dictionary<int, TransitionFrame>(); int consumed = 0;
                        capture.Reset(-1, false, null);
                        capture.AfterStep = state =>
                        {
                            int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                            foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                                foreach (var frame in row.Frames) observed[frame.Frame] = frame;
                            consumed = cursor;
                        };
                        string path = Path.Combine(directory, $"pitch{pitch}-target{target}-{variant.Name}.wav");
                        try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, path); capture.Check(); }
                        finally { capture.AfterStep = null; }
                        string hash = TimingPcmHash(path);
                        var timing = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                        var gain = NativeTransitionGain.ReadStatus((nint)fixture.Part);
                        bool map = variant.Plan.Segments.Where(r => r.To != null).SelectMany(r => r.Frames).Where(f => f.Frame < consumed)
                            .All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
                        Console.WriteLine(JsonSerializer.Serialize(new { pitch, target, variant.Name, map,
                            timing.Failures, gainFailures = gain.Failures, observed = observed.Count, consumed, hash, ordinaryHash,
                            mismatches = variant.Plan.Segments.Where(r => r.To != null).SelectMany(r => r.Frames).Where(f => f.Frame < consumed &&
                                (!observed.TryGetValue(f.Frame, out var actual) || actual != f)).Take(3).ToArray() }));
                        Require(map && timing.Failures == 0 && gain.Failures == 0, "Continuation render did not consume its certified source map.");
                        Require((variant.Name is "neutral" or "restored") == (hash == ordinaryHash), "Continuation PCM restoration/control failed.");
                        renders.Add(new { variant.Name, map, hash, timing.Failures, gainFailures = gain.Failures });
                    }
                    NativeSampledTiming.Remove((nint)fixture.Part);
                    records.Add(new { pitch, target, passed = true, counters = queryCounters, renders,
                        notes = layout.Notes, rows = combined.Segments.Select(r => new { r.Begin, r.Split, r.End, r.From, r.To }) });
                }
                catch (NotSupportedException error)
                {
                    failures++;
                    records.Add(new { pitch, target, passed = false, error = error.Message,
                        counters = counters.Read().Since(before),
                        nativePositions = fixture.Notes.Select(n => NativeTimingPositions.Read(n, false)).ToArray() });
                }
                Console.WriteLine(JsonSerializer.Serialize(records[^1]));
            }
        }
        File.WriteAllText(Path.Combine(directory, "continuation-timing.json"), JsonSerializer.Serialize(records,
            new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
