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
    private static void ProbeFullPhonemeLifecycle(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database, string directory)
    {
        using var fixture = CreateStandaloneFixture(manager, 1, 960, 0, 2880);
        fixture.Part.SetVoiceBankID("BL8CEAM5N4XN3LFK");
        var target = fixture.Notes[0]; target.SetPhonemes("l a i", true, 4);
        fixture.Sequence.InterpolatedMasterVolume(); fixture.Sequence.Commit(false);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        var document = new SampledTimingDocument();
        document.Changed += backend.ClearCache;
        var address = SampledTimingVsmBinding.Address(target);
        var undo = new Stack<ICustomParameterHistoryEdit>();
        var redo = new Stack<ICustomParameterHistoryEdit>();
        var states = new List<object>();
        ulong epoch = 1000;
        SampledTimingPlan Plan(int factor) => new(target.Phonemes,
            Enumerable.Range(0, 4).Select(i => new TimingAdjustment(TimingAdjustment.PhonemeBoundary, i,
                (float)(new[] { -2, -1, 3, 2 }[i] * factor * 256d / 44100)))
                .Append(new(TimingAdjustment.NoteCoefficient, -1, .5f)));
        void Set(SampledTimingPlan? plan) => document.Edit(address, plan, edit => { undo.Push(edit); redo.Clear(); });
        void Undo() { var edit = undo.Pop(); edit.ApplyBefore(); edit.AfterApply(); redo.Push(edit); }
        void Redo() { var edit = redo.Pop(); edit.ApplyAfter(); edit.AfterApply(); undo.Push(edit); }
        string Render(WIVSMSequence source, WIVSMMidiPart part, WIVSMNote note, SampledTimingDocument doc, string name, bool invalidate = true)
        {
            var plans = SampledTimingVsmBinding.ResolvePlans(source, doc);
            var plan = plans.GetValueOrDefault((nint)note) ?? new(note.Phonemes, []);
            using var prepared = backend.PrepareCoefficient(note, doc.Revision,
                neighbor => plans.GetValueOrDefault((nint)neighbor)?.PhonemeCoefficient(neighbor.Phonemes),
                neighbor => plans.GetValueOrDefault((nint)neighbor));
            var layout = prepared.QueryPhonemes(plan, doc.Revision);
            if (invalidate) SampledTimingVsmBinding.InvalidateRenderedPart(part, part.NotesEnumerator);
            if (plans.Count > 0) NativeSampledTiming.Publish(part, ++epoch,
                plans.ToDictionary(v => v.Key, v => v.Value.PhonemeCoefficient(v.Value.Topology) ?? .5f), layout.Anchors);
            else NativeSampledTiming.Remove((nint)part);
            string path = Path.Combine(directory, "phoneme-lifecycle-" + name + ".wav");
            var before = counters.Read();
            RenderTransitionWithScore(source, part, path);
            bool cacheHit = plans.Count > 0 && NativeSampledTiming.ReadStatus((nint)part).Scopes == 0;
            if (cacheHit)
            {
                var cached = counters.Read().Since(before);
                Require(cached.Starts == 0 && cached.Steps == 0 && cached.Samples == 0, "Cache hydration synthesized audio.");
                Require(part.HasValidRenderedScore, "Cache hit did not hydrate the score.");
                Require(NativeSampledTiming.ReadStatus((nint)part).CacheKeys > 0, "Cached result did not use the timing fingerprint.");
            }
            var actual = note.GetPhonemePositions().Select(v => (long)v).ToArray();
            var routing = NativeSampledTiming.ReadStatus((nint)part);
            Console.WriteLine("phonemes.lifecycle_state=" + JsonSerializer.Serialize(new { name, predicted = layout.Result.Boundaries, actual, routing.Failures, routing.Scopes, routing.Applied, routing.CacheKeys,
                diagnostic=routing.Failures==0 ? null : NativeSampledTiming.PhonemeDiagnostic((nint)part) }));
            Require(actual.SequenceEqual(layout.Result.Boundaries), "Phoneme lifecycle boundary mismatch.");
            Require(note.NoteVelocity == 64, "Phoneme control changed VEL.");
            string hash = TimingPcmHash(path);
            states.Add(new { name, layout.Result.Boundaries, hash, doc.IsDirty, doc.Fingerprint, cacheHit });
            return hash;
        }
        try
        {
            string baseline = Render(fixture.Sequence, fixture.Part, target, document, "baseline");
            Set(Plan(1)); string a = Render(fixture.Sequence, fixture.Part, target, document, "a");
            Set(Plan(2)); string b = Render(fixture.Sequence, fixture.Part, target, document, "b");
            Undo(); Require(Render(fixture.Sequence, fixture.Part, target, document, "undo") == a, "Undo reused the wrong PCM.");
            Redo(); Require(Render(fixture.Sequence, fixture.Part, target, document, "redo") == b, "Redo reused the wrong PCM.");
            Require(a != b && a != baseline, "Combined phoneme controls did not change audio.");
            string saveDirectory = Path.Combine(directory, "phoneme-native-save");
            Directory.CreateDirectory(saveDirectory);
            Require(fixture.Sequence.Save(saveDirectory, false), "Native phoneme project save failed.");
            string project = Path.Combine(directory, "full-phoneme-roundtrip.vpr");
            ZipFile.CreateFromDirectory(Path.Combine(saveDirectory, "Project"), project, CompressionLevel.NoCompression, true);
            SampledTimingProjectArchive.Write(project, document.Snapshot()); document.MarkSaved();
            string loadDirectory = Path.Combine(directory, "phoneme-native-reopen");
            ZipFile.ExtractToDirectory(project, loadDirectory);
            for (int pass = 0; pass < 2; pass++)
            {
                var reopened = manager.OpenSequence(Path.Combine(loadDirectory, "Project", "sequence.json"), EditorDirectory,
                    new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 });
                Require(reopened != null, "Native phoneme project load failed.");
                nint reopenedPart = 0;
                try
                {
                    var loaded = new SampledTimingDocument(); loaded.Load(SampledTimingProjectArchive.Read(project));
                    Require(loaded.Fingerprint == document.Fingerprint, "Saved phoneme controls changed.");
                    var part = (WIVSMMidiPart)((WIVSMMidiTrack)reopened!.GetTrack(0UL)!).GetPart(0UL)!;
                    reopenedPart = (nint)part;
                    reopened.InterpolatedMasterVolume(); reopened.Commit(false);
                    Require(Render(reopened, part, part.GetNote(0UL)!, loaded, "reopen-" + pass, false) == b, "Reopened phoneme PCM changed.");
                    if (pass == 1) Require(NativeSampledTiming.ReadStatus((nint)part).Scopes == 0 &&
                        NativeSampledTiming.ReadStatus((nint)part).CacheKeys != 0, "Reopened timing did not reuse its own cache.");
                }
                finally
                {
                    try { reopened?.Close(); }
                    finally { NativeSampledTiming.Remove(reopenedPart); }
                }
            }
            Undo(); Require(document.IsDirty, "Undo did not dirty the saved phoneme document.");
            Redo(); Require(!document.IsDirty, "Redo did not restore saved phoneme identity.");
            Set(null); Require(Render(fixture.Sequence, fixture.Part, target, document, "clear") == baseline, "Clearing phoneme controls reused stale PCM.");
        }
        finally { NativeSampledTiming.Remove((nint)fixture.Part); }
        File.WriteAllText(Path.Combine(directory, "full-phoneme-lifecycle.json"), JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("phonemes.lifecycle=save/load/undo/redo/clear passed");
    }

}
