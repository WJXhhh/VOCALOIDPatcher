using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeStoredPhonemeReplay(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        string project = Environment.GetEnvironmentVariable("VOCALOID_GENG_PROJECT")!;
        int partIndex = int.Parse(Environment.GetEnvironmentVariable("VOCALOID_REPLAY_PART") ?? "3");
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var zip = System.IO.Compression.ZipFile.OpenRead(project);
        var entry = zip.GetEntry("Project/sequence.json") ?? throw new InvalidDataException("Missing source sequence.");
        Require(entry.Length < 10 * 1024 * 1024, "Source sequence exceeds probe bound.");
        string path = Path.Combine(directory, "sequence.json");
        using (var input = entry.Open())
        using (var output = File.Create(path)) input.CopyTo(output);
        var source = manager.OpenSequence(path, EditorDirectory,
            new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        try
        {
            var part = (WIVSMMidiPart)((WIVSMMidiTrack)source.GetTrack(0UL)!).GetPart((ulong)partIndex)!;
            var notes = part.NotesEnumerator.ToArray();
            Require(notes.Length <= 33, "Captured Part exceeds probe bound.");
            source.InterpolatedMasterVolume(); source.Commit(false);
            var saved = SampledTimingProjectArchive.Read(project);
            var transitions = TransitionProjectArchive.Read(project).Data!;
            if (Environment.GetEnvironmentVariable("VOCALOID_REPLAY_RENDER") == "1")
                return RenderStoredPhonemeCandidates(manager, source, part, notes, saved, transitions, dse, database, directory);
            using var backend = new LocalConsonantTimingBackend(manager, dse, database);
            using var counters = new TimingDseCounters();
            var records = new List<object>(); int failed = 0, unavailable = 0;
            for (int n = 0; n < notes.Length; n++)
            {
                var note = notes[n]; var address = SampledTimingVsmBinding.Address(note);
                var original = saved.GetValueOrDefault(address) ?? new(note.Phonemes, []);
                using var prepared = backend.PrepareCoefficient(note, 1,
                    coefficientForNote: other => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(other))?.PhonemeCoefficient(other.Phonemes),
                    planForNote: other => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(other)));
                int count = note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1;
                for (int boundary = 0; boundary < count; boundary++)
                {
                    string? error = null; long[]? baseline = null, current = null;
                    var candidates = new List<object>(); var before = counters.Read(); int accepted = 0;
                    try
                    {
                        baseline = prepared.QueryPhonemes(PhonemeBoundaryEdit.WithBoundary(original, boundary, null), 1,
                            editedBoundary: boundary).Result.Boundaries;
                        current = prepared.QueryPhonemes(original, 1).Result.Boundaries;
                        foreach (int delta in new[] { -1, 1 })
                        {
                            string? rejection = null; long[]? positions = null;
                            try
                            {
                                var candidate = PhonemeBoundaryEdit.WithBoundary(original, boundary, delta * 256f / 44100);
                                var phonemes = prepared.QueryPhonemes(candidate, 1, editedBoundary: boundary);
                                positions = phonemes.Result.Boundaries;
                                if (TransitionProjectBinding.HasPart(transitions, address.Track, address.Part))
                                {
                                    var center = notes[notes.Length / 2];
                                    var centerAddress = SampledTimingVsmBinding.Address(center);
                                    var centerPlan = saved.GetValueOrDefault(centerAddress) ?? new(center.Phonemes, []);
                                    using var whole = backend.PrepareCoefficient(center, 1,
                                        coefficientForNote: other => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(other))?.PhonemeCoefficient(other.Phonemes),
                                        planForNote: other => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(other)));
                                    var prior = whole.QueryTransitions(centerPlan, 1);
                                    var adopted = TransitionTimingScale.Adopt(transitions, transitions, prior);
                                    using var commit = backend.PrepareCoefficient(center, 1,
                                        coefficientForNote: other => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(other))?.PhonemeCoefficient(other.Phonemes),
                                        planForNote: other => phonemes.Plans?.TryGetValue(SampledTimingVsmBinding.Address(other), out var related) == true
                                            ? related : saved.GetValueOrDefault(SampledTimingVsmBinding.Address(other)));
                                    var centerCandidate = phonemes.Plans?.TryGetValue(centerAddress, out var proposed) == true
                                        ? proposed ?? new(center.Phonemes, []) : centerPlan;
                                    var layout = commit.QueryTransitions(centerCandidate, 1);
                                    _ = commit.QueryTransitionControls(layout, TransitionProjectBinding.ResolveAvailable(adopted, layout), 1);
                                }
                            }
                            catch (NotSupportedException exception) { rejection = exception.Message; failed++; }
                            if (rejection == null) accepted++;
                            candidates.Add(new { delta, rejection, positions });
                        }
                    }
                    catch (NotSupportedException exception) { error = exception.Message; failed++; }
                    if (error != null || accepted == 0) unavailable++;
                    var queryCounters = counters.Read().Since(before);
                    Require(queryCounters == new TimingDseSnapshot(0, 0, 0), "Phoneme planning synthesized audio.");
                    records.Add(new { n, note.Phonemes, boundary, error, baseline, current, candidates, queryCounters });
                    Console.WriteLine($"phoneme.note={n};boundary={boundary};error={error ?? "none"}");
                }
                File.WriteAllText(Path.Combine(directory, "stored-phonemes.json"), JsonSerializer.Serialize(records,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine($"phoneme.total={records.Count};rejected={failed};unavailable={unavailable}");
            return unavailable == 0 ? 0 : 1;
        }
        finally { source.Close(); }
    }

    private static int RenderStoredPhonemeCandidates(WIVSMSequenceManager manager, WIVSMSequence source, WIVSMMidiPart part, WIVSMNote[] notes,
        IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> saved, TransitionProjectData transitions,
        DSEManager dse, DatabaseManager database, string directory)
    {
        // Isolate only this disposable sequence. Remap archive locators after
        // removing the other Parts; preserve the musical start and NOTE data.
        var owner = SampledTimingVsmBinding.Address(notes[0]);
        var plans = notes.ToDictionary(n => (nint)n, n => saved.GetValueOrDefault(SampledTimingVsmBinding.Address(n)));
        transitions = transitions.KeepNotes(n => n.Address.Track == owner.Track && n.Address.Part == owner.Part);
        foreach (var track in source.TracksEnumerator.OfType<WIVSMMidiTrack>())
            foreach (var other in track.PartsEnumerator.OfType<WIVSMMidiPart>().Where(p => (nint)p != (nint)part).ToArray())
                Require(track.RemovePart(other), "Cannot isolate phoneme render Part.");
        source.Commit(false);
        int relocatedPart = SampledTimingVsmBinding.Address(notes[0]).Part;
        transitions = transitions with { Notes = transitions.Notes.Select(n => n with
            { Address = n.Address with { Part = relocatedPart } }).ToImmutableArray() };
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        using var capture = new TimingCoefficientHook();
        var center = notes[notes.Length / 2]; var centerAddress = SampledTimingVsmBinding.Address(center);
        var centerPlan = plans.GetValueOrDefault((nint)center) ?? new(center.Phonemes, []);
        using var basis = backend.PrepareCoefficient(center, 1,
            coefficientForNote: n => plans.GetValueOrDefault((nint)n)?.PhonemeCoefficient(n.Phonemes),
            planForNote: n => plans.GetValueOrDefault((nint)n));
        var before = counters.Read();
        var baselineLayout = basis.QueryTransitions(centerPlan, 1);
        var adopted = TransitionTimingScale.Adopt(transitions, transitions, baselineLayout);
        var baseline = basis.QueryTransitionControls(baselineLayout, TransitionProjectBinding.ResolveAvailable(adopted, baselineLayout), 1);
        Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Render baseline planning synthesized audio.");
        var identity = notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes)).ToArray();
        var records = new List<object>(); ulong epoch = 120000;
        string Render(LocalConsonantTimingBackend.PreparedCoefficientTiming prepared, TransitionExecutionPlan execution, string name)
        {
            prepared.ValidateTransitionCommit(part, execution, 1);
            prepared.PublishTransitions(part, ++epoch, execution, 1);
            SampledTimingVsmBinding.InvalidateRenderedPart(part, notes);
            var observed = new Dictionary<int, TransitionFrame>(); int consumed = 0;
            capture.Reset(-1, false, null);
            capture.AfterStep = state =>
            {
                int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                    foreach (var frame in row.Frames) observed[frame.Frame] = frame;
                consumed = cursor;
            };
            string path = Path.Combine(directory, name + ".wav");
            try { RenderTransitionWithScore(source, part, path); capture.Check(); }
            finally { capture.AfterStep = null; }
            var timing = NativeSampledTiming.ReadStatus((nint)part);
            var gain = NativeTransitionGain.ReadStatus((nint)part);
            bool map = observed.Count > 0 && execution.Segments.Where(r => r.To != null).SelectMany(r => r.Frames)
                .Where(f => f.Frame < consumed).All(f => observed.TryGetValue(f.Frame, out var actual) && actual == f);
            bool sourceUnchanged = identity.SequenceEqual(notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value, n.NoteNumber, n.NoteVelocity, n.Phonemes)));
            Require(map && sourceUnchanged && timing.Failures == 0 && gain.Failures == 0, "Phoneme render changed NOTE data or failed its native source map.");
            string hash = TimingPcmHash(path);
            records.Add(new { name, map, sourceUnchanged, hash, timing.Failures, gainFailures = gain.Failures });
            File.WriteAllText(Path.Combine(directory, "phoneme-renders.json"), JsonSerializer.Serialize(records,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"phoneme.render={name};map={map};failures={timing.Failures}/{gain.Failures}");
            return hash;
        }
        Render(basis, baseline, "warm");
        string baselineHash = Render(basis, baseline, "baseline");
        for (int n = 0; n < notes.Length; n++)
        {
            var note = notes[n]; var original = plans.GetValueOrDefault((nint)note) ?? new(note.Phonemes, []);
            using var local = backend.PrepareCoefficient(note, 1,
                coefficientForNote: other => plans.GetValueOrDefault((nint)other)?.PhonemeCoefficient(other.Phonemes),
                planForNote: other => plans.GetValueOrDefault((nint)other));
            var candidate = PhonemeBoundaryEdit.WithBoundary(original, 0, -256f / 44100);
            before = counters.Read();
            var related = local.QueryPhonemes(candidate, 1, editedBoundary: 0).Plans!;
            using var whole = backend.PrepareCoefficient(center, 1,
                coefficientForNote: other => plans.GetValueOrDefault((nint)other)?.PhonemeCoefficient(other.Phonemes),
                planForNote: other => related.TryGetValue(SampledTimingVsmBinding.Address(other), out var changed)
                    ? changed : plans.GetValueOrDefault((nint)other));
            var centerCandidate = related.TryGetValue(centerAddress, out var selected) ? selected ?? new(center.Phonemes, []) : centerPlan;
            var layout = whole.QueryTransitions(centerCandidate, 1);
            var execution = whole.QueryTransitionControls(layout, TransitionProjectBinding.ResolveAvailable(adopted, layout), 1);
            Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Render candidate planning synthesized audio.");
            Require(Render(whole, execution, "note" + n) != baselineHash, "Phoneme edit did not change PCM.");
        }
        Require(Render(basis, baseline, "restored") == baselineHash, "Phoneme edits did not restore PCM exactly.");
        NativeSampledTiming.Remove((nint)part);
        return 0;
    }
}
