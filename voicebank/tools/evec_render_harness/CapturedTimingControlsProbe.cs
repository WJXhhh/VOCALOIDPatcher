using System.IO;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeCapturedTimingControls(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("VOCALOID_GENG_FIXTURE")!));
        var input = json.RootElement; var notes = input.GetProperty("notes").EnumerateArray().ToArray();
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        var records = new List<object>();
        foreach (string mode in new[] { "neutral", "you-only", "nin-only", "all", "except-you", "except-nin", "except-both" })
        {
            using var fixture = CreateStandaloneFixture(manager, notes.Length, 240,
                input.GetProperty("partStart").GetInt64(), input.GetProperty("partDuration").GetInt64());
            var source = fixture.Sequence; var part = fixture.Part;
            Require(part.SetVoiceBankID(input.GetProperty("bank").GetString()!), "Captured bank unavailable.");
            source.InsertTempo(VSMRelTick.Zero, input.GetProperty("bpm").GetInt32() * 100);
            var plans = new Dictionary<nint, SampledTimingPlan>();
            for (int i = 0; i < notes.Length; i++)
            {
                var note = fixture.Notes[i]; var captured = notes[i];
                part.MoveNote(new VSMRelTick(captured.GetProperty("position").GetInt64()), note);
                note.SetDuration(new VSMRelTick(captured.GetProperty("duration").GetInt64()));
                note.SetNoteNumber(captured.GetProperty("pitch").GetInt32()); note.NoteVelocity = captured.GetProperty("velocity").GetInt32();
                var e = captured.GetProperty("expression");
                note.SetNoteExpression(new VSMNoteExpression(e.GetProperty("accent").GetInt32(), e.GetProperty("decay").GetInt32(),
                    e.GetProperty("bend_depth").GetInt32(), e.GetProperty("bend_length").GetInt32(), e.GetProperty("opening").GetInt32(),
                    e.GetProperty("rise_port").GetBoolean(), e.GetProperty("fall_port").GetBoolean()));
                string phones = captured.GetProperty("phonemes").GetString()!; note.SetPhonemes(phones, true, 4);
                bool controls = mode switch { "neutral" => false, "you-only" => i == 0, "nin-only" => i == 2,
                    "except-you" => i != 0, "except-nin" => i != 2, "except-both" => i != 0 && i != 2, _ => true };
                plans[(nint)note] = new(phones, controls ? captured.GetProperty("controls").EnumerateArray().Select(a =>
                    new TimingAdjustment(a.GetProperty("kind").GetString()!, a.GetProperty("index").GetInt32(), a.GetProperty("value").GetSingle())) : []);
            }
            source.InterpolatedMasterVolume(); source.Commit(false);
            using var backend = new LocalConsonantTimingBackend(manager, dse, database);
            using var counters = new TimingDseCounters();
            foreach (int index in new[] { 0, 2, notes.Length / 2 })
            {
                var target = fixture.Notes[index]; string? error = null; long[]? positions = null; object? frames = null, limits = null;
                var before = counters.Read();
                try
                {
                    using var prepared = backend.PrepareCoefficient(target, 1,
                        coefficientForNote: n => plans[(nint)n].PhonemeCoefficient(n.Phonemes), planForNote: n => plans[(nint)n]);
                    var phonemes = prepared.QueryPhonemes(plans[(nint)target], 1);
                    positions = phonemes.Result.Boundaries;
                    limits = phonemes.Limits?.Select(p => new { p.Key.Note, limits = p.Value }).ToArray();
                    if (index == notes.Length / 2)
                    {
                        var layout = prepared.QueryTransitions(plans[(nint)target], 1);
                        frames = layout.Notes.Select(n => new { n.Address.Note, n.Address.Topology, n.Original });
                        limits = layout.BoundaryLimits?.Select(p => new { p.Key.Note, limits = p.Value }).ToArray();
                        _ = prepared.QueryTransitionControls(layout, new(layout.Fingerprint), 1);
                    }
                }
                catch (NotSupportedException exception) { error = exception.Message; }
                var queryCounters = counters.Read().Since(before);
                Require(queryCounters == new TimingDseSnapshot(0, 0, 0), "Captured timing planning synthesized audio.");
                records.Add(new { mode, index, error, positions, frames, limits, queryCounters });
                Console.WriteLine($"captured.mode={mode};index={index};error={error ?? "none"}");
                File.WriteAllText(Path.Combine(directory, "captured-timing.json"), JsonSerializer.Serialize(records,
                    new JsonSerializerOptions { WriteIndented = true }));
                if (error == null && index == notes.Length / 2 && mode == "all" && Environment.GetEnvironmentVariable("VOCALOID_REPLAY_RENDER") == "1")
                    RenderCapturedTimingCandidates(fixture, plans, backend, counters, directory);
            }
        }
        return 0;
    }

    private static void RenderCapturedTimingCandidates(StandaloneFixture fixture, Dictionary<nint, SampledTimingPlan> plans,
        LocalConsonantTimingBackend backend, TimingDseCounters counters, string directory)
    {
        var source = fixture.Sequence; var part = fixture.Part;
        var raw = fixture.Notes.ToDictionary(n => (nint)n, n => plans[(nint)n].Fingerprint);
        var saved = fixture.Notes.ToDictionary(SampledTimingVsmBinding.Address, n => plans[(nint)n]);
        TransitionProjectData stored = TransitionProjectData.Empty;
        if (Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT") is { } project)
        {
            var data = TransitionProjectArchive.Read(project).Data!;
            stored = data.KeepNotes(n => n.Address.Track == 1 && n.Address.Part == 0 &&
                saved.ContainsKey(n.Address with { Track = 0 }));
            stored = stored with { Notes = stored.Notes.Select(n => n with { Address = n.Address with { Track = 0 } }).ToImmutableArray() };
        }
        string archive = Path.Combine(directory, "timing-controls.vpr");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create)) { }
        SampledTimingProjectArchive.Write(archive, saved);
        var reopened = SampledTimingProjectArchive.Read(archive);
        Require(saved.All(p => reopened[p.Key].Fingerprint == p.Value.Fingerprint), "Projection rewrote archived requested timing.");
        int[] velocities = fixture.Notes.Select(n => n.NoteVelocity).ToArray();
        var reports = new List<object>(); ulong epoch = 140000;
        using var capture = new TimingCoefficientHook();
        var baselineHashes = new Dictionary<string, string>();
        foreach (string stateName in new[] { "captured", "vel64", "restored-context" })
        {
            if (stateName != "captured")
            {
                foreach (int n in new[] { 2, 4, 14 }) fixture.Notes[n].NoteVelocity = stateName == "vel64" ? 64 : velocities[n];
                source.Commit(false);
            }
            var target = fixture.Notes[fixture.Notes.Length / 2];
            using var prepared = backend.PrepareCoefficient(target, 1,
                coefficientForNote: n => plans[(nint)n].PhonemeCoefficient(n.Phonemes), planForNote: n => plans[(nint)n]);
            var before = counters.Read();
            var layout = prepared.QueryTransitions(plans[(nint)target], 1);
            var neutral = TransitionProjectBinding.ResolveAvailable(stored, layout);
            int limited = layout.BoundaryLimits?.Sum(p => p.Value.Length) ?? 0;
            Require(stateName == "vel64" ? limited == 0 : limited > 0, "Timing limits did not track the native VEL context.");
            var edge = layout.Edges.First(e => e.Identity.From == "p" && e.Identity.To == "ei");
            var endpoint = new TransitionEndpointRef(edge.Identity, TransitionEndpoint.End);
            var variants = new[] { (Name: "warm", Plan: neutral), (Name: "baseline", Plan: neutral),
                (Name: "phase", Plan: neutral.WithPhase(edge.Identity, .5f)),
                (Name: "geometry", Plan: neutral.WithJunction(layout, endpoint, layout.Junction(endpoint).CurrentFrame + 1)),
                (Name: "restored", Plan: neutral) };
            Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Captured render planning synthesized audio.");
            string? baseline = null;
            var identity = fixture.Notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes)).ToArray();
            foreach (var variant in variants)
            {
                before = counters.Read();
                var execution = prepared.QueryTransitionControls(layout, variant.Plan, 1);
                Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Captured transition planning synthesized audio.");
                prepared.ValidateTransitionCommit(part, execution, 1); prepared.PublishTransitions(part, ++epoch, execution, 1);
                SampledTimingVsmBinding.InvalidateRenderedPart(part, fixture.Notes);
                var observed = new Dictionary<int, TransitionFrame>(); int consumed = 0;
                capture.Reset(-1, false, null);
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                        foreach (var frame in row.Frames) observed[frame.Frame] = frame;
                    consumed = cursor;
                };
                string path = Path.Combine(directory, stateName + "-" + variant.Name + ".wav");
                try { RenderTransitionWithScore(source, part, path); capture.Check(); }
                finally { capture.AfterStep = null; }
                var timing = NativeSampledTiming.ReadStatus((nint)part); var gain = NativeTransitionGain.ReadStatus((nint)part);
                bool map = observed.Count > 0 && execution.Segments.Where(r => r.To != null).SelectMany(r => r.Frames)
                    .Where(f => f.Frame < consumed).All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
                bool unchanged = identity.SequenceEqual(fixture.Notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes))) &&
                    fixture.Notes.All(n => plans[(nint)n].Fingerprint == raw[(nint)n]);
                Require(map && unchanged && timing.Failures == 0 && gain.Failures == 0, "Captured timing render failed or rewrote source data.");
                string hash = TimingPcmHash(path);
                if (variant.Name == "baseline") baseline = hash;
                if (variant.Name == "restored") Require(hash == baseline, "Captured timing controls did not restore PCM exactly.");
                if (variant.Name == "geometry") Require(hash != baseline, "Captured transition edit did not change PCM.");
                reports.Add(new { stateName, variant.Name, limited, map, unchanged, hash, timing.Failures, gainFailures = gain.Failures });
                File.WriteAllText(Path.Combine(directory, "captured-timing-renders.json"), JsonSerializer.Serialize(reports,
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"captured.render={stateName}/{variant.Name};limited={limited};map={map};failures={timing.Failures}/{gain.Failures}");
            }
            baselineHashes[stateName] = baseline!;
            NativeSampledTiming.Remove((nint)part);
        }
        Require(baselineHashes["captured"] == baselineHashes["restored-context"], "Restoring VEL changed the captured limited PCM.");
        Require(fixture.Notes.All(n => plans[(nint)n].Fingerprint == raw[(nint)n]), "Requested timing values were overwritten.");
    }
}
