using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeTimingFoundation(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        using var counters = new TimingDseCounters();
        using var hook = new TimingCoefficientHook();
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        var results = new List<object>();
        var preparationTimes = new List<double>();
        var queryTimes = new List<double>();
        foreach (var phones in new[] { "k a", "n a", "k a i", "s a", "j a" })
        {
            using var fixture = CreateStandaloneFixture(manager, 3, 480, 0, 2880);
            var target = fixture.Notes[1];
            target.SetPhonemes(phones, true, 0);
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            using var prepared = backend.PrepareCoefficient(target, 7);
            preparationTimes.Add(prepared.PreparationMs);
            int encoded = prepared.EncodedNotes;
            var beforeQueries = counters.Read();
            int matching = 0;
            foreach (int velocity in Enumerable.Range(0, 128))
            {
                float coefficient = velocity == 127 ? 1 : velocity / 128f;
                var actual = prepared.Query(coefficient, 7);
                var reference = backend.Query(target, velocity);
                Require(actual.Boundaries.SequenceEqual(reference.Boundaries), "Prepared VEL differs.");
                queryTimes.Add(actual.Milliseconds); matching++;
            }
            var delta = counters.Read().Since(beforeQueries);
            Require(delta.Starts == 0 && delta.Steps == 0 && delta.Samples == 0, "Prepared query synthesized PCM.");
            Require(prepared.EncodedNotes == encoded, "Prepared query re-encoded notes.");
            var planQuery = prepared.Query(new SampledTimingPlan(phones,
                [new(TimingAdjustment.NoteCoefficient, -1, .12345679f)]), 7);
            Require(planQuery.Boundaries.SequenceEqual(prepared.Query(.12345679f, 7).Boundaries), "Plan-level query differs from coefficient query.");
            bool reservedRejected = false;
            try { prepared.Query(new SampledTimingPlan(phones, [new(TimingAdjustment.PhonemeBoundary, 1, .01f)]), 7); }
            catch (NotSupportedException) { reservedRejected = true; }
            Require(reservedRejected, "Native executor silently ignored a reserved axis.");
            float[] values = [.00390625f, .18359375f, .736328125f];
            var rendered = new List<object>();
            foreach (float coefficient in values)
            {
                var prediction = prepared.Query(coefficient, 7);
                var second = prepared.Query(coefficient, 7);
                Require(second.CacheHit && second.Boundaries.SequenceEqual(prediction.Boundaries), "Coefficient cache missed.");
                hook.ResetAll(new Dictionary<int, float> { [1] = coefficient });
                SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
                string path = Path.Combine(directory, "prepared-" + results.Count + "-" + rendered.Count + ".wav");
                RenderTransitionWithScore(fixture.Sequence, fixture.Part, path);
                hook.Check();
                Require(target.GetPhonemePositions().Select(v => (long)v).SequenceEqual(prediction.Boundaries), "Prepared prediction differs from PCM boundary.");
                Require(fixture.Notes.All(n => n.NoteVelocity == 64), "Cache invalidation changed source VEL.");
                rendered.Add(new { coefficient, prediction.Boundaries, hash = TimingPcmHash(path), hook.Injections });
            }
            bool stale = false;
            try { prepared.Query(.5f, 8); } catch (InvalidOperationException) { stale = true; }
            Require(stale, "Stale preparation accepted.");
            results.Add(new { phones, matching, prepared.PreparationMs, encoded, prepared.WindowCount, queryDse = delta, rendered });
            Console.WriteLine($"foundation.phones={phones};ordinary={matching};pcm={rendered.Count};prepare_ms={prepared.PreparationMs:F3}");
        }
        var contexts = new List<object>();
        foreach (var spec in new[] {
            (Count: 3, Duration: 120, Start: 0L, Tempo: false, Rin: false),
            (Count: 3, Duration: 480, Start: 17003L, Tempo: true, Rin: false),
            (Count: 3, Duration: 480, Start: 0L, Tempo: false, Rin: true),
            (Count: 100, Duration: 120, Start: 17003L, Tempo: false, Rin: false),
            (Count: 10000, Duration: 120, Start: 17003L, Tempo: false, Rin: false) })
        {
            using var fixture = CreateStandaloneFixture(manager, spec.Count, spec.Duration, spec.Start,
                spec.Count * spec.Duration + 1000000L);
            if (spec.Rin) fixture.Part.SetVoiceBankID("BKKP765AEHXWSKDB");
            if (spec.Tempo) fixture.Sequence.InsertTempo(new VSMRelTick(spec.Start + 910), 9000);
            fixture.Sequence.Commit(false);
            var target = fixture.Notes[spec.Count / 2];
            using var prepared = backend.PrepareCoefficient(target, 21);
            var before = counters.Read();
            int comparisons = 0;
            foreach (int velocity in new[] { 0, 7, 32, 64, 96, 126, 127 })
            {
                var predicted = prepared.Query(velocity == 127 ? 1 : velocity / 128f, 21);
                var reference = backend.Query(target, velocity);
                Require(predicted.Boundaries.SequenceEqual(reference.Boundaries), "Prepared context/coordinate mismatch.");
                comparisons++;
            }
            var delta = counters.Read().Since(before);
            Require(delta.Steps == 0 && delta.Samples == 0, "Context comparison produced PCM.");
            contexts.Add(new { spec.Count, spec.Duration, spec.Start, spec.Tempo, spec.Rin, comparisons,
                prepared.PreparationMs, prepared.EncodedNotes, prepared.WindowCount, dse = delta });
            Console.WriteLine($"foundation.context={spec};encoded={prepared.EncodedNotes};windows={prepared.WindowCount};ms={prepared.PreparationMs:F3}");
        }
        bool detachedOwnedQuery;
        using (var fixture = CreateStandaloneFixture(manager, 2, 480, 0, 2880))
        using (var prepared = backend.PrepareCoefficient(fixture.Notes[1], 33))
        {
            var before = prepared.Query(.12345679f, 33);
            long first = before.Boundaries[0]; before.Boundaries[0] = long.MaxValue;
            Require(prepared.Query(.12345679f, 33).Boundaries[0] == first, "Prepared cached result aliases a returned array.");
            fixture.Sequence.Close();
            var after = prepared.Query(.2345679f, 33);
            detachedOwnedQuery = before.Boundaries.Length > 0 && after.Boundaries.Length > 0;
        }
        Require(detachedOwnedQuery, "Prepared snapshot still reads closed source.");
        bool invalidatedPreparation = false;
        using (var fixture = CreateStandaloneFixture(manager, 2, 480, 0, 2880))
        using (var prepared = backend.PrepareCoefficient(fixture.Notes[1], 40))
        {
            backend.ClearCache();
            try { prepared.Query(.5f, 40); }
            catch (ObjectDisposedException) { invalidatedPreparation = true; }
        }
        Require(invalidatedPreparation, "Cleared preparation remains executable.");
        var multiple = new List<object>();
        foreach (string bank in new[] { "BCNFCY43LB2LZCD4", "BKKP765AEHXWSKDB" })
        {
            using var fixture = CreateStandaloneFixture(manager, 3, 480, 0, 2880);
            fixture.Part.SetVoiceBankID(bank);
            fixture.Notes[1].SetPhonemes("k a i", true, 0);
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            var values = new Dictionary<nint, float> { [(nint)fixture.Notes[1]] = .18359375f, [(nint)fixture.Notes[2]] = .736328125f };
            var predictions = new List<long[]>();
            for (int i = 1; i < 3; i++)
            {
                using var prepared = backend.PrepareCoefficient(fixture.Notes[i], 50, n => values.GetValueOrDefault((nint)n, .5f));
                predictions.Add(prepared.Query(values[(nint)fixture.Notes[i]], 50).Boundaries);
            }
            hook.ResetAll(new Dictionary<int, float> { [1] = .18359375f, [2] = .736328125f });
            string path = Path.Combine(directory, "multiple-" + multiple.Count + ".wav");
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, path); hook.Check();
            for (int i = 1; i < 3; i++) Require(fixture.Notes[i].GetPhonemePositions().Select(v => (long)v).SequenceEqual(predictions[i - 1]), "Neighbor coefficient context mismatch.");
            Require(hook.Injections == 2, "Multiple render plan not applied exactly twice.");
            multiple.Add(new { bank, predictions, hook.Injections, hash = TimingPcmHash(path) });
        }
        object boundedCache;
        using (var fixture = CreateStandaloneFixture(manager, 100, 120, 0, 20000))
        {
            fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
            hook.ResetAll(new Dictionary<int, float>());
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, "bounded-warmup.wav")); hook.Check();
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes.Skip(34).Take(33));
            hook.ResetAll(new Dictionary<int, float>());
            string baseline = Path.Combine(directory, "bounded-baseline.wav");
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, baseline); hook.Check();
            hook.ResetAll(new Dictionary<int, float>());
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes.Skip(34).Take(33));
            string twin = Path.Combine(directory, "bounded-control.wav");
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, twin); hook.Check();
            var left = fixture.Notes[0].GetPhonemePositions().ToArray();
            var right = fixture.Notes[^1].GetPhonemePositions().ToArray();
            using var prepared = backend.PrepareCoefficient(fixture.Notes[50], 61);
            var expected = prepared.Query(.18359375f, 61);
            hook.ResetAll(new Dictionary<int, float> { [50] = .18359375f });
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes.Skip(34).Take(33));
            string changed = Path.Combine(directory, "bounded-changed.wav");
            var before = counters.Read();
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, changed); hook.Check();
            Require(hook.Injections == 1 && fixture.Notes[50].GetPhonemePositions().Select(v => (long)v).SequenceEqual(expected.Boundaries), "Bounded cache invalidation routed to the wrong note.");
            Require(left.SequenceEqual(fixture.Notes[0].GetPhonemePositions()) && right.SequenceEqual(fixture.Notes[^1].GetPhonemePositions()), "Bounded edit changed remote boundaries.");
            var changedCounters = counters.Read().Since(before);
            hook.ResetAll(new Dictionary<int, float>());
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes.Skip(34).Take(33));
            string restored = Path.Combine(directory, "bounded-restored.wav");
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, restored); hook.Check();
            var controlDifference = TimingWaveDifference(baseline, twin);
            var restoredDifference = TimingWaveDifference(baseline, restored);
            Require(TimingPcmHash(restored) == TimingPcmHash(twin), "Bounded cache did not restore the unchanged normal control.");
            // Retain exact differences, including a possible native repeated-
            // render witness. Never call non-identical full PCM identical.
            boundedCache = new { notes = 100, invalidated = 33, expected.Boundaries, changedCounters,
                baselineHash = TimingPcmHash(baseline), controlHash = TimingPcmHash(twin),
                changedHash = TimingPcmHash(changed), restoredHash = TimingPcmHash(restored), controlDifference, restoredDifference };
        }
        var lifecycle = ProbeTimingDocumentLifecycle(manager, backend, hook, counters, directory);
        File.WriteAllText(Path.Combine(directory, "timing-foundation.json"), JsonSerializer.Serialize(new
        { results, contexts, detachedOwnedQuery, invalidatedPreparation, multiple, boundedCache, lifecycle, preparation = SummarizeTimingLatency(preparationTimes), queries = SummarizeTimingLatency(queryTimes) },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static object ProbeTimingDocumentLifecycle(WIVSMSequenceManager manager, LocalConsonantTimingBackend backend,
        TimingCoefficientHook hook, TimingDseCounters counters, string directory)
    {
        using var fixture = CreateStandaloneFixture(manager, 3, 480, 0, 2880);
        fixture.Sequence.SetMaxUndoCount(100);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        var target = fixture.Notes[1];
        target.SetPhonemes("k a i", true, 0); fixture.Sequence.Commit(false);
        var address = SampledTimingVsmBinding.Address(target);
        var document = new SampledTimingDocument();
        var undo = new Stack<ICustomParameterHistoryEdit>();
        var redo = new Stack<ICustomParameterHistoryEdit>();
        document.Changed += backend.ClearCache;
        int events = 0;
        document.Changed += () => events++;
        var snapshots = new List<object>();
        var hashes = new Dictionary<string, string>();
        hook.ResetAll(new Dictionary<int, float>());
        RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, "lifecycle-warmup.wav")); hook.Check();
        void Set(float? value)
        {
            document.Edit(address, value.HasValue ? new SampledTimingPlan(address.Topology,
                [new(TimingAdjustment.NoteCoefficient, -1, value.Value)]) : null,
                edit => { undo.Push(edit); redo.Clear(); });
        }
        void Undo() { var edit = undo.Pop(); edit.ApplyBefore(); edit.AfterApply(); redo.Push(edit); }
        void Redo() { var edit = redo.Pop(); edit.ApplyAfter(); edit.AfterApply(); undo.Push(edit); }
        object RenderState(string name)
        {
            var resolved = SampledTimingVsmBinding.Resolve(fixture.Sequence, document);
            using var prepared = backend.PrepareCoefficient(target, document.Revision,
                n => resolved.TryGetValue((nint)n, out var c) ? c : null);
            float coefficient = resolved.GetValueOrDefault((nint)target, .5f);
            var predicted = prepared.Query(coefficient, document.Revision);
            var ordinals = fixture.Notes.Select((n, i) => (n, i)).Where(v => resolved.ContainsKey((nint)v.n))
                .ToDictionary(v => v.i, v => resolved[(nint)v.n]);
            hook.ResetAll(ordinals);
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
            var before = counters.Read();
            string path = Path.Combine(directory, "lifecycle-" + name + ".wav");
            RenderTransitionWithScore(fixture.Sequence, fixture.Part, path); hook.Check();
            Require(target.GetPhonemePositions().Select(v => (long)v).SequenceEqual(predicted.Boundaries), "Lifecycle boundary mismatch.");
            Require(fixture.Notes.All(n => n.NoteVelocity == 64), "Lifecycle changed VEL.");
            var state = new { name, coefficient, document.Revision, document.IsDirty, document.Fingerprint,
                predicted.Boundaries, hash = TimingPcmHash(path), dse = counters.Read().Since(before) };
            hashes[name] = state.hash;
            snapshots.Add(state); return state;
        }
        RenderState("baseline");
        Set(.009765625f);
        bool nativeCacheStillValidAfterTimingEdit = fixture.Part.HasValidRenderedWave;
        string cachedBeforeInvalidation = TimingPcmHash(fixture.Part.WaveFilePath);
        Require(cachedBeforeInvalidation == hashes["baseline"], "Native cache control changed unexpectedly.");
        RenderState("a");
        Set(.736328125f); RenderState("b");
        Undo(); RenderState("undo_a");
        Redo(); RenderState("redo_b");
        Require(hashes["a"] == hashes["undo_a"] && hashes["b"] == hashes["redo_b"] && hashes["a"] != hashes["b"],
            "Undo/redo PCM cache did not restore distinct states.");
        string nativePath = Path.Combine(directory, "foundation.vpr");
        string saveDirectory = Path.Combine(directory, "native-save");
        Directory.CreateDirectory(saveDirectory);
        Require(fixture.Sequence.Save(saveDirectory, false), $"Native VPR save failed: {fixture.Sequence.LastError}/{manager.LastError}.");
        if (File.Exists(nativePath)) File.Delete(nativePath);
        ZipFile.CreateFromDirectory(Path.Combine(saveDirectory, "Project"), nativePath, CompressionLevel.NoCompression, true);
        // Verify unrelated extension data survives our archive replacement.
        using (var zip = ZipFile.Open(nativePath, ZipArchiveMode.Update))
        using (var stream = new StreamWriter(zip.CreateEntry("VOCALOIDPatcher/other-test.txt").Open())) stream.Write("preserve");
        SampledTimingProjectArchive.Write(nativePath, document.Snapshot()); document.MarkSaved();
        Require(!document.IsDirty, "Saved timing document dirty.");
        var loaded = SampledTimingProjectArchive.Read(nativePath);
        Require(loaded[address].Fingerprint == document.Get(address)!.Fingerprint, "Timing archive changed coefficient.");
        using (var zip = ZipFile.OpenRead(nativePath)) Require(zip.GetEntry("VOCALOIDPatcher/other-test.txt") != null, "Unrelated archive entry lost.");
        string loadDirectory = Path.Combine(directory, "native-reopen-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(nativePath, loadDirectory);
        var reopened = manager.OpenSequence(Path.Combine(loadDirectory, "Project", "sequence.json"), EditorDirectory,
            new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        Require(reopened != null, "Native VPR reopen failed.");
        string? reopenedHash = null;
        long[] reopenedBoundaries = [];
        try
        {
            var loadedDocument = new SampledTimingDocument(); loadedDocument.Load(loaded);
            var bound = SampledTimingVsmBinding.Resolve(reopened!, loadedDocument);
            Require(bound.Count == 1 && bound.Values.Single() == .736328125f, "Reopened coefficient did not bind.");
            var reloadedPart = (WIVSMMidiPart)((WIVSMMidiTrack)reopened!.GetTrack(0UL)!).GetPart(0UL)!;
            var reloadedNote = reloadedPart.GetNote(1UL)!;
            reopened.InterpolatedMasterVolume(); reopened.Commit(false);
            using var prepared = backend.PrepareCoefficient(reloadedNote, loadedDocument.Revision,
                n => bound.TryGetValue((nint)n, out var c) ? c : null);
            var expected = prepared.Query(.736328125f, loadedDocument.Revision);
            hook.ResetAll(new Dictionary<int, float> { [1] = .736328125f });
            SampledTimingVsmBinding.InvalidateRenderedPart(reloadedPart, reloadedPart.NotesEnumerator);
            Console.WriteLine($"foundation.reopen_flags=can:{reopened.CanAsyncRendering()},finished:{reopened.IsFinishedRendering},wave:{reloadedPart.HasValidRenderedWave},score:{reloadedPart.HasValidRenderedScore},bank:{reloadedPart.VoiceBankID},notes:{reloadedPart.NumNotes},error:{reopened.LastError}");
            string reloadedWave = Path.Combine(directory, "lifecycle-reopen.wav");
            RenderTransitionWithScore(reopened, reloadedPart, reloadedWave); hook.Check();
            Require(reloadedNote.GetPhonemePositions().Select(v => (long)v).SequenceEqual(expected.Boundaries), "Reopened boundary mismatch.");
            Require(TimingPcmHash(reloadedWave) == hashes["b"], "Reopened coefficient PCM changed.");
            reopenedHash = TimingPcmHash(reloadedWave); reopenedBoundaries = expected.Boundaries;
        }
        finally { reopened?.Close(); }
        Undo(); Require(document.IsDirty, "Undo after save did not dirty document.");
        Redo(); Require(!document.IsDirty, "Redo to saved content remained dirty.");
        Undo(); Set(.18359375f); Require(redo.Count == 0 && document.IsDirty, "History branch retained redo or saved identity.");
        RenderState("branch");
        Set(null); RenderState("clear");
        Require(hashes["clear"] == hashes["baseline"], "Cleared coefficient reused stale PCM.");
        fixture.Notes[0].SetNoteNumber(61);
        Require(fixture.Sequence.Commit(true) && fixture.Sequence.CanUndo(), "Native history positive control failed.");
        RenderState("native_pitch");
        fixture.Sequence.Undo();
        Require(fixture.Notes[0].NoteNumber == 60 && fixture.Sequence.CanRedo(), "Cache invalidation consumed native undo.");
        RenderState("native_undo");
        Require(fixture.Sequence.CanRedo(), "Cache invalidation destroyed native redo.");
        fixture.Sequence.Redo();
        Require(fixture.Notes[0].NoteNumber == 61, "Native redo lost after timing renders.");
        RenderState("native_redo");
        Require(hashes["native_undo"] == hashes["baseline"] && hashes["native_redo"] == hashes["native_pitch"], "Native history PCM cache mismatch.");
        // An inactive future target survives storage but must never silently render as VEL.
        var reserved = new SampledTimingPlan(address.Topology, [new(TimingAdjustment.PhonemeBoundary, 2, .01f),
            new(TimingAdjustment.Transition, 1, -.02f), new("future.transition.shape", 1, .3f)]);
        document.Edit(address, reserved, e => undo.Push(e));
        SampledTimingProjectArchive.Write(nativePath, document.Snapshot());
        var future = SampledTimingProjectArchive.Read(nativePath)[address];
        Require(future.Fingerprint == reserved.Fingerprint, "Future timing targets lost.");
        bool rejected = false;
        try { future.ExecutableCoefficient(address.Topology); } catch (NotSupportedException) { rejected = true; }
        Require(rejected, "Unsupported future targets executed silently.");
        bool topologyRejected = false;
        try { new SampledTimingPlan("a i", [new(TimingAdjustment.NoteCoefficient, -1, .5f)]).ExecutableCoefficient("i a"); }
        catch (NotSupportedException) { topologyRejected = true; }
        Require(topologyRejected, "Changed phoneme topology accepted.");
        Console.WriteLine("foundation.save_reopen_history_cache=passed");
        return new { snapshots, events, nativeCacheStillValidAfterTimingEdit, cachedBeforeInvalidation,
            reopenedHash, reopenedBoundaries, nativeHistoryPreserved = true,
            saveReopen = true, reservedTargetsPreserved = true, unsupportedRejected = rejected,
            historyScope = "Real timing history commands replayed through ICustomParameterHistoryEdit; Editor shared timeline not installed in this isolated process." };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object TimingWaveDifference(string first, string second)
    {
        short[] Read(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            reader.BaseStream.Position = 12;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                uint tag = reader.ReadUInt32(), size = reader.ReadUInt32();
                if (tag == 0x61746164)
                {
                    var samples = new short[size / 2]; for (int i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16();
                    return samples;
                }
                reader.BaseStream.Position += size + (size & 1);
            }
            throw new InvalidDataException("Missing PCM.");
        }
        var a = Read(first); var b = Read(second);
        Require(a.Length == b.Length, "Repeated PCM length changed.");
        int firstDifference = -1, last = -1, count = 0, peak = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
            {
                if (firstDifference < 0) firstDifference = i;
                last = i; count++; peak = Math.Max(peak, Math.Abs(a[i] - b[i]));
            }
        return new { count, firstSample = firstDifference, lastSample = last, peakInt16Difference = peak };
    }
}
