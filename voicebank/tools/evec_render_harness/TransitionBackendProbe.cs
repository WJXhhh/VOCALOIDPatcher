using System.Diagnostics;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.PhonemeVolume;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeTransitionBackend(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        string native = Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!;
        NativeTransitionGain.Initialize(native);
        manager.CacheCapacity = 128L * 1024 * 1024;
        using var counters = new TimingDseCounters();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var capture = new TimingCoefficientHook();
        var report = new List<object>();
        ulong epoch = 41000;
        bool existingTiming = Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_EXISTING_TIMING") == "1";
        int offset = int.TryParse(Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_NOTE_OFFSET"), out int shifted) ? shifted : 0;
        TransitionFixture[] fixtures =
        [
            new("a", "BL8CEAM5N4XN3LFK", ["a"], 4, 960, 0),
            new("la", "BL8CEAM5N4XN3LFK", ["l a"], 4, 960, 1),
            new("multi", "BMBNDB8EM5222MK3", ["ts\\_h i iE_r @N"], 4, 960, 2),
            new("connected", "BL8CEAM5N4XN3LFK", ["a", "l a"], 4, 480, 1),
            new("short", "BL8CEAM5N4XN3LFK", ["l a"], 4, 120, 1),
            new("fricative", "BL8CEAM5N4XN3LFK", ["s\\ i_n n"], 4, 480, 1),
            new("miku", "BCNFCY43LB2LZCD4", ["k a"], 0, 960, 1),
            new("triplet", "BCNFCY43LB2LZCD4", ["n n#2 a"], 0, 960, 2)
        ];
        string? only = Environment.GetEnvironmentVariable("VOCALOID_TRANSITION_BACKEND_FIXTURE");
        if (only != null) fixtures = fixtures.Where(f => f.Name == only).ToArray();
        foreach (var test in fixtures)
        {
            using var fixture = CreateStandaloneFixture(manager, test.Phones.Length, test.Duration, 0, checked(4800 + offset));
            fixture.Part.SetVoiceBankID(test.Bank);
            for (int i = 0; i < fixture.Notes.Length; i++)
            {
                fixture.Part.MoveNote(new VSMRelTick(checked(960 + offset + i * test.Duration)), fixture.Notes[i]);
                fixture.Notes[i].SetPhonemes(test.Phones[i], true, test.Language);
            }
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            Console.WriteLine($"transition.source={test.Name}; bank={fixture.Part.VoiceBankID}; phones={fixture.Notes[^1].Phonemes}; nativeBanks={database.NumVoiceBanks}");
            using var prepared = backend.PrepareCoefficient(fixture.Notes[^1], 1);
            var before = counters.Read();
            var sourcePlan = new SampledTimingPlan(test.Phones[^1], !existingTiming ? [] :
                [new(TimingAdjustment.NoteCoefficient, -1, .375f), new(TimingAdjustment.PhonemeBoundary,
                    test.Phones[^1].Split(' ').Length, (float)(2 * 256d / 44100))]);
            var layout = prepared.QueryTransitions(sourcePlan, 1);
            var plan = prepared.QueryTransitionControls(layout, new(layout.Fingerprint), 1);
            Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Transition backend synthesized audio.");
            report.Add(new { test.Name, existingTiming, offset, prepared.PreparationMs, prepared.EncodedNotes,
                sourcePlan.Adjustments, layout.Fingerprint, layout.Edges, layout.Junctions, layout.Notes, plan.NativePlans });
            File.WriteAllText(Path.Combine(directory, "backend.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"transition.backend={test.Name}; edges={layout.Edges.Length}; joints={layout.Junctions.Length}; shared={layout.Junctions.Count(j => j.Aliases.Length > 1)}");
            var target = layout.Edges[test.Edge];
            string? originalHash = null;
            string Render(string variant, TransitionExecutionPlan execution, bool compareBaseline = false)
            {
                prepared.PublishTransitions(fixture.Part, ++epoch, execution, 1);
                if (variant.Contains("repeat", StringComparison.Ordinal))
                    SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                capture.Reset(-1, false, null);
                int consumed = 0;
                var observed = new Dictionary<int, TransitionSegment>();
                var positions = new Dictionary<int, TransitionFrame>();
                capture.AfterStep = state =>
                {
                    int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                    foreach (var segment in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                    {
                        observed[segment.Begin] = segment;
                        foreach (var frame in segment.Frames) positions[frame.Frame] = frame;
                    }
                    consumed = cursor;
                };
                string path = Path.Combine(directory, test.Name + "-" + variant + ".wav");
                try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, path); capture.Check(); }
                finally { capture.AfterStep = null; }
                var status = NativeSampledTiming.ReadStatus((nint)fixture.Part);
                var gain = NativeTransitionGain.ReadStatus((nint)fixture.Part);
                Require(status.Failures == 0 && gain.Failures == 0, "Transition render failed or used a mismatched row.");
                if (status.Scopes > 0)
                {
                    var actual = observed.Values.Where(r => r.To != null).OrderBy(r => r.Begin).ToArray();
                    var expected = execution.Segments.Where(r => r.To != null).ToArray();
                    Require(actual.Length == expected.Length, "Transition native render topology differs from query.");
                    for (int i = 0; i < actual.Length; i++)
                    {
                        var a = actual[i]; var b = expected[i];
                        Require((a.Begin, a.Split, a.End, a.From, a.To, a.Stage, a.Reverse, a.MaterialMetadata) ==
                            (b.Begin, b.Split, b.End, b.From, b.To, b.Stage, b.Reverse, b.MaterialMetadata), "Transition render rows differ from query.");
                        Require(b.Frames.All(f => positions.TryGetValue(f.Frame, out var point) && point == f), "Transition render source-frame map differs from query.");
                    }
                }
                string hash = TimingPcmHash(path);
                originalHash ??= hash;
                if (compareBaseline) Require(hash == originalHash, "Restored transition control changed PCM.");
                report.Add(new { test.Name, variant, hash, status.Epoch, status.Scopes, status.CacheKeys, status.Failures,
                    gainApplied = gain.Applied, gainPrimary = gain.Primary, gainAuxiliary = gain.Auxiliary,
                    execution.NativePlans, execution.CacheHit, execution.Controls.Anchors, nativeFramesCompared = positions.Count });
                File.WriteAllText(Path.Combine(directory, "backend.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"transition.render={test.Name}/{variant}; scopes={status.Scopes}; cache={status.CacheKeys}; applied={gain.Applied}");
                return path;
            }
            NativeSampledTiming.Publish(fixture.Part, ++epoch, fixture.Notes.Select((n, i) => ((nint)n,
                Coefficient: existingTiming && i == fixture.Notes.Length - 1 ? .375f : .5f)).ToDictionary(p => p.Item1, p => p.Coefficient),
                layout.Anchors.Select(a => new NativePhonemeTiming.Point(a.From, a.To)).ToArray());
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
            string referenceWave = Path.Combine(directory, test.Name + "-existing-backend.wav");
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, referenceWave);
            originalHash = TimingPcmHash(referenceWave);
            Render("baseline", plan, true);
            foreach (float db in new[] { -12f, 6f, -60f, 0f })
            {
                before = counters.Read();
                var controlled = prepared.QueryTransitionControls(layout, new(layout.Fingerprint, gains: [new(target.Identity, db)]), 1);
                Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Transition gain query synthesized PCM.");
                string wave = Render("gain-" + db, controlled, db == 0);
                if (db != 0) Require(TimingPcmHash(wave) != originalHash, "Transition gain did not reach output.");
                string repeated = Render("gain-repeat-" + db, controlled, db == 0);
                Require(TimingPcmHash(repeated) == TimingPcmHash(wave), "Repeated transition publication changed audio.");
            }
            foreach (var side in new[] { TransitionEndpoint.Begin, TransitionEndpoint.End })
            {
                var endpoint = new TransitionEndpointRef(target.Identity, side);
                before = counters.Read();
                var range = prepared.QueryTransitionRange(layout, endpoint, 1);
                Require(counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0), "Transition range scan synthesized PCM.");
                var cached = prepared.QueryTransitionRange(layout, endpoint, 1);
                Require(cached.CacheHit && cached.NativePlans == 0 && cached.ReachableFrames.SequenceEqual(range.ReachableFrames), "Transition range cache changed.");
                report.Add(new { test.Name, range = side.ToString(), range.Minimum, range.Maximum, range.IsContiguous,
                    range.ReachableFrames, range.NativePlans, range.Milliseconds, range.Junction.Protected });
                foreach (int frame in new[] { range.Minimum, range.Maximum }.Distinct().Where(f => f != range.Junction.CurrentFrame))
                {
                    var changed = prepared.QueryTransitionControls(layout, new(layout.Fingerprint, [new(endpoint, frame)], [new(target.Identity, -6)]), 1);
                    string wave = Render("timing-" + side + "-" + frame, changed);
                    Require(TimingPcmHash(wave) != originalHash, "Transition timing did not change audio.");
                    var phone = target.Identity.Right ?? target.Identity.Left!;
                    var phoneNote = fixture.Part.GetNote(checked((ulong)phone.Note.Note))!;
                    var phonePositions = phoneNote.GetPhonemePositions();
                    double start = fixture.Sequence.PresendTimeSec + fixture.Sequence.GetTimeFromTick(fixture.Part.AbsBeginTick,
                        phoneNote.GetAbsPositionFromNoteBaseTick(phonePositions[phone.Index]));
                    double end = fixture.Sequence.PresendTimeSec + fixture.Sequence.GetTimeFromTick(fixture.Part.AbsBeginTick,
                        phoneNote.GetAbsPositionFromNoteBaseTick(phonePositions[phone.Index + 1]));
                    string combined = Path.Combine(directory, test.Name + "-volume-combined-" + side + "-" + frame + ".wav");
                    before = counters.Read();
                    PhonemeGainWaveProcessor.Create(wave, combined, [new(start, end, 3)]);
                    Require(TimingPcmHash(combined) != TimingPcmHash(wave) && counters.Read().Since(before) == new TimingDseSnapshot(0, 0, 0),
                        "Phoneme/transition volume combination was lost or synthesized audio.");
                    string restoredVolume = combined + ".zero.wav";
                    PhonemeGainWaveProcessor.Create(wave, restoredVolume, [new(start, end, 0)]);
                    Require(TimingPcmHash(restoredVolume) == TimingPcmHash(wave), "Zero phoneme gain did not preserve transition audio.");
                }
            }
            var shared = layout.Junctions.FirstOrDefault(j => !j.Protected && j.Aliases.Length > 1 && j.MinimumCandidate < j.MaximumCandidate);
            if (shared != null)
            {
                int frame = shared.CurrentFrame < shared.MaximumCandidate ? shared.CurrentFrame + 1 : shared.CurrentFrame - 1;
                var moved = prepared.QueryTransitionControls(layout, new(layout.Fingerprint,
                    shared.Aliases.Select(a => new TransitionJunctionEdit(a, frame))), 1);
                Render("shared-junction", moved);
                report.Add(new { test.Name, sharedAliases = shared.Aliases.Length, frame });
            }
            var simultaneous = prepared.QueryTransitionControls(layout, new(layout.Fingerprint,
                gains: [new(layout.Edges[0].Identity, -3), new(layout.Edges[^1].Identity, 3)]), 1);
            Require(TimingPcmHash(Render("multiple-gains", simultaneous)) != originalHash, "Multiple transition gains were lost.");
            Render("restored", plan, true);
            var cacheControlled = prepared.QueryTransitionControls(layout,
                new(layout.Fingerprint, gains: [new(target.Identity, -12)]), 1);
            string controlledHash = TimingPcmHash(Render("cache-controlled", cacheControlled));
            string saved = Path.Combine(directory, test.Name + "-saved");
            Directory.CreateDirectory(saved);
            Require(fixture.Sequence.Save(saved, false), "Cannot save private cache fixture.");
            nint closedPart = (nint)fixture.Part;
            Require(fixture.Sequence.Close(), "Cannot close private cache fixture.");
            var closed = NativeSampledTiming.ReadStatus(closedPart);
            Console.WriteLine($"transition.cache-close={test.Name}; keys={closed.CacheKeys}; bytes={manager.CacheSize}");
            prepared.Dispose();
            for (int pass = 0; pass < 3; pass++)
            {
                var loaded = manager.OpenSequence(Path.Combine(saved, "Project", "sequence.json"), EditorDirectory,
                    new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })
                    ?? throw new InvalidOperationException("Cannot reopen cache fixture.");
                try
                {
                loaded.InterpolatedMasterVolume(); loaded.Commit(false);
                var loadedPart = (WIVSMMidiPart)((WIVSMMidiTrack)loaded.GetTrack(0UL)!).GetPart(0UL)!;
                using var cachedPreparation = backend.PrepareCoefficient(loadedPart.GetNote(loadedPart.NumNotes - 1)!, 1);
                var cachedLayout = cachedPreparation.QueryTransitions(sourcePlan, 1);
                var cachedPlan = cachedPreparation.QueryTransitionControls(cachedLayout, new(cachedLayout.Fingerprint,
                    gains: pass == 1 ? [] : [new(cachedLayout.Edges[test.Edge].Identity, -12)]), 1);
                cachedPreparation.PublishTransitions(loadedPart, ++epoch, cachedPlan, 1);
                string path = Path.Combine(directory, test.Name + "-reopen-" + pass + ".wav");
                RenderTransitionWithScore(loaded, loadedPart, path);
                var status = NativeSampledTiming.ReadStatus((nint)loadedPart);
                Require(TimingPcmHash(path) == (pass == 1 ? originalHash : controlledHash), "Cache reload changed transition PCM or poisoned ordinary audio.");
                Require(status.CacheKeys > 0 && (pass == 1 || status.Scopes == 0), "Reopened native transition cache was bypassed.");
                report.Add(new { test.Name, variant = "reopened-cache", pass, status.Scopes, status.CacheKeys, hash = TimingPcmHash(path) });
                File.WriteAllText(Path.Combine(directory, "backend.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                loaded.Close();
                }
                finally { loaded.Close(); }
            }
        }
        capture.Dispose();
        if (only == null || only == "isolation") ProbeTransitionIsolation(manager, dse, database, Path.Combine(directory, "isolation"));
        return 0;
    }
}
