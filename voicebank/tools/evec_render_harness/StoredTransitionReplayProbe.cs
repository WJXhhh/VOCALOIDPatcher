using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeStoredTransitionReplay(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        string project = Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT")!;
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var fixture = OpenGengFixture(manager, directory);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        var saved = SampledTimingProjectArchive.Read(project);
        var transitions = TransitionProjectArchive.Read(project).Data!;
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        var target = fixture.Notes[fixture.Notes.Length / 2];
        using var prepared = backend.PrepareCoefficient(target, 1,
            coefficientForNote: n => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(n))?.PhonemeCoefficient(n.Phonemes),
            planForNote: n => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(n)));
        var before = counters.Read();
        var layout = prepared.QueryTransitions(saved.GetValueOrDefault(SampledTimingVsmBinding.Address(target)) ?? new(target.Phonemes, []), 1);
        var edges = TransitionProjectBinding.MatchEdges(transitions, layout);
        var joints = new List<object>();
        foreach (var stored in transitions.Junctions)
        {
            if (stored.Aliases.Any(a => !edges.ContainsKey(TransitionProjectData.EdgeKey(a.Edge)))) continue;
            var first = stored.Aliases[0];
            var edge = edges[TransitionProjectData.EdgeKey(first.Edge)];
            var endpoint = new TransitionEndpointRef(edge.Identity, first.Endpoint);
            var joint = layout.Junction(endpoint);
            int delta = TransitionTimingScale.Offset(transitions, layout, stored.OffsetSeconds, stored.TimingScale);
            joints.Add(new { edge.Identity.From, edge.Identity.To, endpoint = first.Endpoint.ToString(),
                leftNote = edge.Identity.Left?.Note.Note, rightNote = edge.Identity.Right?.Note.Note,
                joint.OriginalFrame, joint.CurrentFrame, joint.Protected, layout.MinimumOutputFrame,
                requested = joint.CurrentFrame + delta, delta, stored.OffsetSeconds, stored.TimingScale,
                edge.Current, aliases = joint.Aliases.Length,
                outOfDomain = joint.Protected ? delta != 0 : joint.CurrentFrame + delta < layout.MinimumOutputFrame });
        }
        string? error = null;
        try { _ = prepared.QueryTransitionControls(layout, TransitionProjectBinding.ResolveAvailable(transitions, layout), 1); }
        catch (NotSupportedException exception) { error = exception.Message; }
        var report = new { error, notes = fixture.Notes.Length, queryCounters = counters.Read().Since(before), joints };
        File.WriteAllText(Path.Combine(directory, "stored-transition-replay.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { error, notes = fixture.Notes.Length, queryCounters = counters.Read().Since(before) }));
        if (error == null)
        {
            using var capture = new TimingCoefficientHook();
            var renders = new List<object>(); ulong epoch = 110000;
            int continuation = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_REPLAY_CONTINUATION_INDEX"), out int index) ? index : -1;
            int originalPitch = continuation >= 0 ? fixture.Notes[continuation].NoteNumber : 0;
            foreach (int delta in continuation >= 0 ? new[] { 0, -2, 2 } : new[] { 0 })
            {
                if (continuation >= 0)
                {
                    Require(fixture.Notes[continuation].Phonemes == "-", "Replay pitch target is not a continuation.");
                    fixture.Notes[continuation].SetNoteNumber(originalPitch + delta); fixture.Sequence.Commit(false);
                }
                using var replay = backend.PrepareCoefficient(target, 1,
                    coefficientForNote: n => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(n))?.PhonemeCoefficient(n.Phonemes),
                    planForNote: n => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(n)));
                var replayBefore = counters.Read();
                var current = replay.QueryTransitions(saved.GetValueOrDefault(SampledTimingVsmBinding.Address(target)) ?? new(target.Phonemes, []), 1);
                var basis = replay.QueryTransitionControls(current, new(current.Fingerprint), 1);
                var controlled = replay.QueryTransitionControls(current, TransitionProjectBinding.ResolveAvailable(transitions, current), 1);
                Require(counters.Read().Since(replayBefore) == new TimingDseSnapshot(0, 0, 0), "Stored replay planning synthesized audio.");
                var sourceIdentity = fixture.Notes.Select(n => (n.AbsPosTick.Value, n.DurationTick.Value, n.NoteNumber)).ToArray();
                string? neutralHash = null;
                foreach (var variant in new[] { (Name: "warm", Plan: basis), (Name: "neutral", Plan: basis),
                    (Name: "stored", Plan: controlled), (Name: "restored", Plan: basis) })
                {
                    replay.PublishTransitions(fixture.Part, ++epoch, variant.Plan, 1);
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
                    string wave = Path.Combine(directory, $"pitch{delta}-{variant.Name}.wav");
                    try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, wave); capture.Check(); }
                    finally { capture.AfterStep = null; }
                    var timing = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                    var gain = NativeTransitionGain.ReadStatus((nint)fixture.Part);
                    bool map = variant.Plan.Segments.Where(r => r.To != null).SelectMany(r => r.Frames).Where(f => f.Frame < consumed)
                        .All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
                    bool sourceUnchanged = sourceIdentity.SequenceEqual(fixture.Notes.Select(n => (n.AbsPosTick.Value, n.DurationTick.Value, n.NoteNumber)));
                    Require(map && sourceUnchanged && timing.Failures == 0 && gain.Failures == 0, "Stored render failed or changed source note events.");
                    string hash = TimingPcmHash(wave);
                    if (variant.Name == "neutral") neutralHash = hash;
                    if (variant.Name == "restored") Require(hash == neutralHash, "Stored controls did not restore neutral PCM exactly.");
                    if (variant.Name == "stored") Require(hash != neutralHash, "Stored controls did not change PCM.");
                    renders.Add(new { delta, variant.Name, map, sourceUnchanged, timing.Failures, gainFailures = gain.Failures, hash });
                    Console.WriteLine($"stored.render={delta}/{variant.Name}; map={map}; failures={timing.Failures}/{gain.Failures}");
                    File.WriteAllText(Path.Combine(directory, "stored-renders.json"), JsonSerializer.Serialize(renders,
                        new JsonSerializerOptions { WriteIndented = true }));
                }
                NativeSampledTiming.Remove((nint)fixture.Part);
            }
        }
        return error == null ? 0 : 1;
    }
}
