using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeLeadingNoteTiming(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("VOCALOID_GENG_FIXTURE")!));
        var input = json.RootElement; var notes = input.GetProperty("notes").EnumerateArray().ToArray();
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var renderCapture = new TimingCoefficientHook();
        var renders = new List<object>(); ulong epoch = 130000;
        var baselines = new Dictionary<bool, string>();
        var lifecycle = new List<object>();
        var reports = new List<object>(); int failed = 0;
        foreach (bool leading in new[] { false, true })
        {
            using var fixture = CreateStandaloneFixture(manager, notes.Length + (leading ? 1 : 0), 240,
                input.GetProperty("partStart").GetInt64(), input.GetProperty("partDuration").GetInt64());
            var source = fixture.Sequence; var part = fixture.Part;
            Require(part.SetVoiceBankID(input.GetProperty("bank").GetString()!), "Captured bank unavailable.");
            source.InsertTempo(VSMRelTick.Zero, input.GetProperty("bpm").GetInt32() * 100);
            var plans = new Dictionary<nint, SampledTimingPlan>();
            for (int i = 0; i < fixture.Notes.Length; i++)
            {
                var note = fixture.Notes[i];
                if (leading && i == 0)
                {
                    part.MoveNote(new VSMRelTick(notes[0].GetProperty("position").GetInt64() - 720), note);
                    note.SetDuration(new VSMRelTick(720)); note.SetNoteNumber(65); note.NoteVelocity = 64;
                    note.SetPhonemes("a", true, 4); plans[(nint)note] = new("a", []);
                    continue;
                }
                var captured = notes[i - (leading ? 1 : 0)];
                part.MoveNote(new VSMRelTick(captured.GetProperty("position").GetInt64()), note);
                note.SetDuration(new VSMRelTick(captured.GetProperty("duration").GetInt64()));
                note.SetNoteNumber(captured.GetProperty("pitch").GetInt32()); note.NoteVelocity = captured.GetProperty("velocity").GetInt32();
                string phones = captured.GetProperty("phonemes").GetString()!; note.SetPhonemes(phones, true, 4);
                plans[(nint)note] = new(phones, captured.GetProperty("controls").EnumerateArray().Select(a =>
                    new TimingAdjustment(a.GetProperty("kind").GetString()!, a.GetProperty("index").GetInt32(), a.GetProperty("value").GetSingle())));
            }
            source.InterpolatedMasterVolume(); source.Commit(false);
            using var backend = new LocalConsonantTimingBackend(manager, dse, database);
            using var counters = new TimingDseCounters();
            var target = fixture.Notes[fixture.Notes.Length / 2];
            using var prepared = backend.PrepareCoefficient(target, 1,
                coefficientForNote: n => plans[(nint)n].PhonemeCoefficient(n.Phonemes), planForNote: n => plans[(nint)n]);
            var before = counters.Read();
            var phonemes = prepared.QueryPhonemes(plans[(nint)target], 1, captureTransitions: true);
            var capture = phonemes.TransitionCapture!;
            string? error = null;
            try { _ = prepared.QueryTransitions(plans[(nint)target], 1); }
            catch (NotSupportedException exception) { error = exception.Message; failed++; }
            var points = phonemes.Anchors.ToDictionary(a => a.From, a => a.To);
            foreach (var pair in capture.Original.Zip(capture.Current).Where(p => p.First.To != null))
            {
                points[pair.First.Begin] = pair.Second.Begin;
                points[pair.First.Split] = pair.Second.Split;
                points[pair.First.End] = pair.Second.End;
            }
            var ordered = points.OrderBy(p => p.Key).ToArray();
            var collisions = ordered.Zip(ordered.Skip(1)).Where(p => p.First.Value >= p.Second.Value)
                .Select(p => new { from1 = p.First.Key, to1 = p.First.Value, from2 = p.Second.Key, to2 = p.Second.Value }).ToArray();
            var queryCounters = counters.Read().Since(before);
            Require(queryCounters == new TimingDseSnapshot(0, 0, 0), "Leading-note planning synthesized audio.");
            reports.Add(new { leading, error, queryCounters, collisions, anchors = phonemes.Anchors,
                rows = capture.Original.Zip(capture.Current).Select(p => new { original = new { p.First.Begin, p.First.Split, p.First.End, p.First.From, p.First.To },
                    current = new { p.Second.Begin, p.Second.Split, p.Second.End, p.Second.From, p.Second.To } }) });
            Console.WriteLine($"leading={leading};error={error ?? "none"};collisions={JsonSerializer.Serialize(collisions)}");
            File.WriteAllText(Path.Combine(directory, "leading-note-timing.json"), JsonSerializer.Serialize(reports,
                new JsonSerializerOptions { WriteIndented = true }));
            if (error == null && Environment.GetEnvironmentVariable("VOCALOID_REPLAY_RENDER") == "1")
            {
                var live = part.NotesEnumerator.ToArray();
                var identity = live.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes)).ToArray();
                var layout = prepared.QueryTransitions(plans[(nint)target], 1);
                var neutral = new TransitionControlPlan(layout.Fingerprint);
                var edge = layout.Edges.First(e => e.Identity.From == "p" && e.Identity.To == "ei");
                var endpoint = new TransitionEndpointRef(edge.Identity, TransitionEndpoint.End);
                var variants = new[] { (Name: "warm", Plan: neutral), (Name: "baseline", Plan: neutral),
                    (Name: "phase", Plan: neutral.WithPhase(layout.Edges[0].Identity, .5f)),
                    (Name: "gain", Plan: new TransitionControlPlan(layout.Fingerprint, gains: [new(layout.Edges[0].Identity, 6)])),
                    (Name: "geometry", Plan: neutral.WithJunction(layout, endpoint, layout.Junction(endpoint).CurrentFrame + 1)),
                    (Name: "restored", Plan: neutral) };
                string? baselineHash = null;
                foreach (var variant in variants)
                {
                    before = counters.Read();
                    var execution = prepared.QueryTransitionControls(layout, variant.Plan, 1);
                    Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Leading-note transition query synthesized audio.");
                    TransitionLayout.ValidateAnchors(execution.RenderAnchors);
                    prepared.ValidateTransitionCommit(part, execution, 1);
                    prepared.PublishTransitions(part, ++epoch, execution, 1);
                    SampledTimingVsmBinding.InvalidateRenderedPart(part, live);
                    var observed = new Dictionary<int, TransitionFrame>(); int consumed = 0;
                    renderCapture.Reset(-1, false, null);
                    renderCapture.AfterStep = state =>
                    {
                        int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                        foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                            foreach (var frame in row.Frames) observed[frame.Frame] = frame;
                        consumed = cursor;
                    };
                    string wave = Path.Combine(directory, $"leading{leading}-{variant.Name}.wav");
                    try { RenderTransitionWithScore(source, part, wave); renderCapture.Check(); }
                    finally { renderCapture.AfterStep = null; }
                    var timing = NativeSampledTiming.ReadStatus((nint)part);
                    var gain = NativeTransitionGain.ReadStatus((nint)part);
                    bool map = observed.Count > 0 && execution.Segments.Where(r => r.To != null).SelectMany(r => r.Frames)
                        .Where(f => f.Frame < consumed).All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
                    bool sourceUnchanged = identity.SequenceEqual(live.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes)));
                    Require(map && sourceUnchanged && timing.Failures == 0 && gain.Failures == 0, "Leading-note render failed its source map or changed NOTE data.");
                    string hash = TimingPcmHash(wave);
                    if (variant.Name == "baseline") { baselineHash = hash; baselines[leading] = hash; }
                    if (variant.Name == "restored") Require(hash == baselineHash, "Leading-note controls did not restore PCM exactly.");
                    if (variant.Name == "geometry") Require(hash != baselineHash, "Geometry edit did not change PCM.");
                    renders.Add(new { leading, variant.Name, map, sourceUnchanged, timing.Failures, gainFailures = gain.Failures, hash });
                    File.WriteAllText(Path.Combine(directory, "leading-note-renders.json"), JsonSerializer.Serialize(renders,
                        new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"leading.render={leading}/{variant.Name};map={map};failures={timing.Failures}/{gain.Failures}");
                }
                NativeSampledTiming.Remove((nint)part);
                if (leading)
                {
                    prepared.Dispose();
                    WIVSMNote? left = fixture.Notes[0];
                    for (int cycle = 0; cycle < 4; cycle++)
                    {
                        bool present = cycle % 2 == 1;
                        if (left != null)
                        {
                            plans.Remove((nint)left);
                            Require(part.RemoveNote(left), "Cannot remove leading probe note."); left = null;
                        }
                        if (present)
                        {
                            left = part.InsertNote(new VSMRelTick(notes[0].GetProperty("position").GetInt64() - 720),
                                new VSMNoteEvent(720, 65, 64), part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "probe", "a", true, 4)!;
                            plans[(nint)left] = new("a", []);
                        }
                        source.Commit(false);
                        var currentNotes = part.NotesEnumerator.ToArray();
                        var currentIdentity = currentNotes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes)).ToArray();
                        var middle = currentNotes[currentNotes.Length / 2];
                        using var again = backend.PrepareCoefficient(middle, 1,
                            coefficientForNote: n => plans[(nint)n].PhonemeCoefficient(n.Phonemes), planForNote: n => plans[(nint)n]);
                        before = counters.Read();
                        var currentLayout = again.QueryTransitions(plans[(nint)middle], 1);
                        var execution = again.QueryTransitionControls(currentLayout, new(currentLayout.Fingerprint), 1);
                        Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Lifecycle planning synthesized audio.");
                        again.ValidateTransitionCommit(part, execution, 1);
                        for (int pass = 0; pass < 2; pass++)
                        {
                            again.PublishTransitions(part, ++epoch, execution, 1);
                            SampledTimingVsmBinding.InvalidateRenderedPart(part, currentNotes);
                            string wave = Path.Combine(directory, $"cycle{cycle}-pass{pass}.wav");
                            RenderTransitionWithScore(source, part, wave);
                            var timing = NativeSampledTiming.ReadStatus((nint)part);
                            var gain = NativeTransitionGain.ReadStatus((nint)part);
                            bool unchanged = currentIdentity.SequenceEqual(currentNotes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes)));
                            string hash = TimingPcmHash(wave);
                            Require(unchanged && timing.Failures == 0 && gain.Failures == 0 && timing.Scopes > 0, "Lifecycle render lost native timing or changed NOTE data.");
                            if (pass == 1) Require(hash == baselines[present], "Adding/removing the leading note changed the corresponding baseline PCM.");
                            lifecycle.Add(new { cycle, pass, present, unchanged, hash, timing.Failures, gainFailures = gain.Failures });
                            Console.WriteLine($"leading.cycle={cycle}/{pass};present={present};failures={timing.Failures}/{gain.Failures}");
                            File.WriteAllText(Path.Combine(directory, "leading-note-lifecycle.json"), JsonSerializer.Serialize(lifecycle,
                                new JsonSerializerOptions { WriteIndented = true }));
                        }
                        NativeSampledTiming.Remove((nint)part);
                    }
                }
            }
        }
        return failed == 0 ? 0 : 1;
    }
}
