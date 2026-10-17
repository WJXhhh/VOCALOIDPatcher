#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.DurationSelection;

// Compiled only in a separate artifact directory without the module initializer.
// Executes the production host/history/save patches in a disposable STA process.
[CLSCompliant(false)]
public static class DurationHostValidation
{
    private static bool _failSave;
    public static void Run(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Execute(manager, dse, database, directory); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw new InvalidOperationException("Independent duration host validation failed.", error);
    }
    private static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        var application = new Application();
        Patcher.ConfigManager = new ConfigManager(Path.Combine(directory, "isolated-config.json"));
        Patcher.DataDir = Path.GetDirectoryName(Path.GetDirectoryName(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")))!;
        Directory.CreateDirectory(Path.Combine(Patcher.DataDir, "translations"));
        File.Copy(Path.Combine(Environment.CurrentDirectory, "translations", "中文 (简体).xml"), Path.Combine(Patcher.DataDir, "translations", "中文 (简体).xml"), true);
        Check(TranslationManager.LoadLanguage("中文 (简体)"), "Cannot load isolated UI translations.");
        ValidateEditor(directory);
        foreach (var pair in new (string, object)[] { ("SequenceManager", manager), ("DSEManager", dse), ("DatabaseManager", database) })
            typeof(Yamaha.VOCALOID.App).GetProperty(pair.Item1)!.SetValue(null, pair.Item2);
        var harmony = new Harmony("VOCALOIDPatcher.isolated-duration-host");
        foreach (PatchBase patch in new PatchBase[]
        {
            new SampledTimingRenderStartPatch(), new SampledTimingRenderCompletionPatch(), new SampledTimingDirtyPatch(),
            new SampledTimingProjectSavePatch(), new SampledTimingProjectLoadPatch(), new SampledTimingDuplicateSequencePatch(),
            new ConsonantTimingCommitPatch(), new ConsonantTimingUndoPatch(), new ConsonantTimingRedoPatch(), new ConsonantTimingClosePatch(),
            new BreathVolumeCommitHistoryPatch(), new BreathVolumeUndoPatch(), new BreathVolumeRedoPatch(), new BreathVolumeDirtyPatch(),
            new BreathVolumeProjectSavePatch(), new BreathVolumeDuplicateNotePatch(), new BreathVolumeRemoveNotePatch()
        })
        {
            var target = AccessTools.Method(patch.TargetClass, patch.TargetMethodName, patch.ArgumentTypes);
            Check(target != null, "Missing host patch target: " + patch.PatchName);
            HarmonyMethod? Hook(string name) => AccessTools.Method(patch.GetType(), name) is { } method ? new HarmonyMethod(method) : null;
            harmony.Patch(target, Hook("Prefix"), Hook("Postfix"), finalizer: Hook("Finalizer"));
        }
        harmony.Patch(AccessTools.Method(typeof(DurationSelectionArchive), "Write"), new HarmonyMethod(typeof(DurationHostValidation), nameof(FailWrite)));
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        source.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)); source.InsertTempo(VSMRelTick.Zero, 12000);
        var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "duration-validation")!;
        var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(3840), "duration-validation")!;
        part.SetVoiceBankID("BCNFCY43LB2LZCD4");
        var note = part.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60, 64), part.GetDefaultNoteExpression(),
            part.GetDefaultAiNoteExpression(), "validation", "k a", true, 0)!;
        source.InterpolatedMasterVolume(); source.Commit(false); BreathVolumeService.ResetHistory(source);
        SampledTimingHost.Attach(source, new LocalConsonantTimingBackend(manager, dse, database));
        NativeDurationSelection.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        var states = new List<object>();
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void SaveStates() => File.WriteAllText(Path.Combine(directory, "host.json"), JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        string Render(WIVSMSequence sequence, WIVSMMidiPart target, string name, WIVSMMidiPart[]? waitParts = null)
        {
            bool done = false, canceled = false;
            var pending = (waitParts ?? [target]).Select(p => (nint)p).ToHashSet();
            using var observer = new WIVSMRendererObserver();
            observer.Completed += (_, e) => { pending.Remove((nint)e.MidiPart); done = pending.Count == 0; };
            observer.Canceled += (_, e) => { if (pending.Contains((nint)e.MidiPart)) canceled = true; };
            sequence.AddRendererObserver(observer); sequence.BlockRenderingEnabled = false;
            try
            {
                sequence.StartAsyncRendering();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!done && !canceled && timer.ElapsedMilliseconds < 20000)
                {
                    while (sequence.InvokeRendererObserver()) { }
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame); Thread.Sleep(1);
                }
                Check(done && !canceled, "Host render failed: " + name + "; " + JsonSerializer.Serialize(SampledTimingHost.Diagnostics(sequence)));
                string path = Path.Combine(directory, name + ".wav"); File.Copy(target.WaveFilePath, path, true);
                string hash = Hash(path);
                states.Add(new { name, hash, dirty = sequence.IsDirty, parameters = DurationSelectionHost.Snapshot(sequence), timing = SampledTimingHost.Diagnostics(sequence), boundaries = target.NotesEnumerator.Select(n => n.GetPhonemePositions().ToArray()).ToArray() });
                SaveStates(); return hash;
            }
            finally { sequence.StopAsyncRendering(); sequence.RemoveRendererObserver(observer); }
        }
        try
        {
            if (Environment.GetEnvironmentVariable("VOCALOID_CROSS_PROJECT") is { } crossProject)
            {
                var crossShell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence)); GC.SuppressFinalize(crossShell);
                var crossLoad = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile");
                var crossSave = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence");
                object?[] input = [crossProject, null];
                var result = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))crossLoad.Invoke(crossShell, input)!;
                Check(result.Item1 == Yamaha.VOCALOID.Sequence.LoadResult.Success && input[1] is WIVSMSequence, "Cross-version project could not open.");
                var loaded = (WIVSMSequence)input[1]!;
                try
                {
                    loaded.InterpolatedMasterVolume(); loaded.Commit(false);
                    var loadedPart = (WIVSMMidiPart)((WIVSMMidiTrack)loaded.GetTrack(0UL)!).GetPart(0UL)!;
                    string audio = Render(loaded, loadedPart, "cross-version");
                    Check(audio == Environment.GetEnvironmentVariable("VOCALOID_CROSS_EXPECTED_HASH"), "Cross-version project changed rendered PCM.");
                    Check((bool)crossSave.Invoke(crossShell, [loaded, directory, "roundtrip", ".vpr"])!, "Cross-version project could not save.");
                    string roundtrip = Path.Combine(directory, "roundtrip.vpr");
                    var originalTiming = SampledTimingProjectArchive.Read(crossProject);
                    var savedTiming = SampledTimingProjectArchive.Read(roundtrip);
                    Check(originalTiming.Count == savedTiming.Count && originalTiming.All(p => savedTiming.TryGetValue(p.Key, out var value) && value.Fingerprint == p.Value.Fingerprint), "Cross-version save changed timing controls.");
                    Check(JsonSerializer.Serialize(DurationSelectionArchive.Read(crossProject)) == JsonSerializer.Serialize(DurationSelectionArchive.Read(roundtrip)), "Cross-version save changed duration controls.");
                    Console.WriteLine("duration.cross=load, native render PCM and control-preserving save passed");
                }
                finally { loaded.Close(); }
                return;
            }
            string baseline = Render(source, part, "baseline");
            DurationSelectionHost.Set(note, new("k a", .5, DurationCandidatePolicy.ExpandedExperimental));
            string a = Render(source, part, "duration-a"); Check(a != baseline, "Host duration edit did not change audio.");
            var timing = new SampledTimingPlan("k a", [new(TimingAdjustment.PhonemeBoundary, 1, (float)(2 * 256d / 44100))]);
            SampledTimingHost.SetPhonemePlan(note, timing);
            string combined = Render(source, part, "timing-a"); Check(combined != a, "Host timing edit did not change audio.");
            note.SetNoteNumber(62); source.Commit(true); string pitched = Render(source, part, "native-pitch");
            DurationSelectionHost.Set(note, new("k a", .37151927, DurationCandidatePolicy.ExpandedExperimental));
            string b = Render(source, part, "duration-b");
            SampledTimingHost.EnsureForExport(source);
            int misses = JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source)).GetProperty("durationContractMisses").GetInt32();
            var reuseWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) SampledTimingHost.ValidateDurationPart(part);
            Check(JsonSerializer.SerializeToElement(SampledTimingHost.Diagnostics(source)).GetProperty("durationContractMisses").GetInt32() == misses, "Ratio validation rebuilt unchanged native inputs.");
            states.Add(new { name = "input-reuse", runs = 100, elapsedMs = reuseWatch.Elapsed.TotalMilliseconds, extraPlans = 0 }); SaveStates();
            DurationSelectionHost.RenderFailed(source, new InvalidOperationException("Injected isolated render-state failure."));
            DurationSelectionHost.RenderSucceeded(source);
            Check(DurationSelectionHost.Error(source) != null, "A completion in the failed revision cleared its error.");
            DurationSelectionHost.Set(note, DurationSelectionHost.Get(note));
            Check(Render(source, part, "retry-same-value") == b && DurationSelectionHost.Error(source) == null, "Reapplying failed controls did not recover the same PCM.");
            source.Undo(); Check(note.NoteNumber == 62 && DurationSelectionHost.Get(note).Ratio == .5f, "Custom Undo consumed native pitch history.");
            Check(Render(source, part, "undo-duration") == pitched, "Duration Undo PCM changed.");
            source.Undo(); Check(note.NoteNumber == 60, "Native pitch Undo failed.");
            Check(Render(source, part, "undo-pitch") == combined, "Native Undo did not restore combined controls.");
            source.Undo(); Check(Render(source, part, "undo-timing") == a, "Timing Undo did not restore independent duration.");
            source.Undo(); Check(Render(source, part, "undo-all") == baseline, "Automatic restore PCM changed.");
            source.Redo(); Check(Render(source, part, "redo-duration") == a, "Duration Redo changed PCM.");
            source.Redo(); Check(Render(source, part, "redo-timing") == combined, "Timing Redo changed PCM.");
            source.Redo(); Check(Render(source, part, "redo-pitch") == pitched, "Pitch Redo changed PCM.");
            source.Redo(); Check(Render(source, part, "redo-all") == b, "Combined Redo changed PCM.");
            var shell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence));
            GC.SuppressFinalize(shell); // Only its file methods are used, no native/UI ownership.
            var save = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence");
            bool Save(string name) => (bool)save.Invoke(shell, [source, directory, name, ".vpr"])!;
            Check(Save("saved"), "Extension save failed.");
            string project = Path.Combine(directory, "saved.vpr"), savedHash = Hash(project);
            Check(!DurationSelectionHost.IsDirty(source) && !SampledTimingHost.IsDirty(source), "Successful save left custom values dirty.");
            DurationSelectionHost.Set(note, new("k a", .5, DurationCandidatePolicy.ExpandedExperimental));
            _failSave = true;
            try { Check(!Save("saved"), "Injected archive failure reported success."); }
            finally { _failSave = false; }
            Check(Hash(project) == savedHash && DurationSelectionHost.IsDirty(source), "Save failure changed old project or cleared dirty state.");
            states.Add(new { name = "save-failure", oldFileExact = true, dirty = true }); SaveStates();
            source.Undo(); Check(Render(source, part, "after-save-undo") == b, "Save failure broke history.");
            var load = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile");
            for (int pass = 0; pass < 2; pass++)
            {
                object?[] input = [project, null]; var result = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))load.Invoke(shell, input)!;
                Check(result.Item1 == Yamaha.VOCALOID.Sequence.LoadResult.Success && input[1] is WIVSMSequence, "Host project load failed.");
                var reopened = (WIVSMSequence)input[1]!;
                try
                {
                    reopened.InterpolatedMasterVolume(); reopened.Commit(false);
                    var loadedPart = (WIVSMMidiPart)((WIVSMMidiTrack)reopened.GetTrack(0UL)!).GetPart(0UL)!;
                    Check(Render(reopened, loadedPart, "reopened-" + pass) == b, "Loaded project changed PCM.");
                    var cache = NativeSampledTiming.ReadStatus((nint)loadedPart);
                    states.Add(new { name = "reopened-cache", pass, cache.Scopes, cache.CacheKeys }); SaveStates();
                    if (pass == 1) Check(cache.Scopes == 0 && cache.CacheKeys > 0, "Reopened project missed its duration/timing cache.");
                }
                finally { reopened.Close(); }
            }
            part.MoveNote(new VSMRelTick(1200), note); source.Commit(true);
            Render(source, part, "moved");
            Check(DurationSelectionHost.Snapshot(source).Single().Address.Position == 1200, "Moved duration locator was not updated.");
            Check(SampledTimingHost.Find(source)!.Document.Snapshot().Single().Key.Position == 1200, "Moved timing locator was not updated.");
            source.Undo(); part = (WIVSMMidiPart)track.GetPart(0UL)!; note = part.GetNote(0UL)!;
            Check(Render(source, part, "undo-move") == b, "Move Undo did not restore combined PCM.");
            var duplicate = part.DuplicateNote(new VSMRelTick(2200), note)!; source.Commit(true);
            Check(DurationSelectionHost.Get(duplicate).Ratio == DurationSelectionHost.Get(note).Ratio, "Copied note lost duration preference.");
            string duplicated = Render(source, part, "duplicated");
            part.RemoveNote(duplicate); source.Commit(true); Render(source, part, "deleted-copy");
            source.Undo(); part = (WIVSMMidiPart)track.GetPart(0UL)!;
            Check(Render(source, part, "undo-delete") == duplicated, "Delete Undo lost duration identity or PCM.");
            source.Redo(); part = (WIVSMMidiPart)track.GetPart(0UL)!; Render(source, part, "redo-delete");
            source.Undo(); part = (WIVSMMidiPart)track.GetPart(0UL)!;
            Check(Render(source, part, "undo-delete-again") == duplicated, "Repeated delete Undo did not restore duration identity.");
            var copy = manager.DuplicateSequence(source, new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
            Utils.NativeRenderSession.RegisterInternalSequence(copy);
            try
            {
                copy.InterpolatedMasterVolume(); copy.Commit(false);
                var copiedPart = (WIVSMMidiPart)((WIVSMMidiTrack)copy.GetTrack(0UL)!).GetPart(0UL)!;
                // DuplicateSequence inherits a completed render. Force a fresh
                // native commit so this assertion exercises the copy's routes.
                SampledTimingVsmBinding.InvalidateRenderedPart(copiedPart, copiedPart.NotesEnumerator);
                Check(Render(copy, copiedPart, "render-copy") == duplicated, "Internal render copy lost combined parameters.");
                copiedPart.GetNote(0UL)!.NoteVelocity = 91; copy.Commit(false);
                string copyVelocity = Render(copy, copiedPart, "render-copy-velocity");
                part.GetNote(0UL)!.NoteVelocity = 91; source.Commit(true);
                Check(Render(source, part, "source-copy-velocity") == copyVelocity, "Edited copy used a stale timing contract.");
                copiedPart.GetNote(0UL)!.SetPhonemes("a", true, 0); copy.Commit(false);
                bool rejected = false;
                try { SampledTimingHost.BeforeRendering(copy); }
                catch (NotSupportedException) { rejected = true; }
                Check(rejected, "A transformed copy silently dropped note controls.");
                states.Add(new { name = "transformed-copy", rejected }); SaveStates();
            }
            finally { copy.Close(); Utils.NativeRenderSession.UnregisterInternalSequence(copy); }
            var extraParts = new List<WIVSMMidiPart>();
            foreach (int index in new[] { 0, 1 })
            {
                var extraTrack = (WIVSMMidiTrack)source.InsertTrackEx(source.NumTrack, VSMTrackType.Midi, "part-validation")!;
                var extra = extraTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(3840), "part-validation")!;
                extra.SetVoiceBankID(part.VoiceBankID);
                extra.InsertNote(new VSMRelTick(960), new VSMNoteEvent(960, 60 + index, 64), extra.GetDefaultNoteExpression(), extra.GetDefaultAiNoteExpression(), "validation", "k a", true, 0);
                extraParts.Add(extra);
            }
            source.Commit(true);
            for (int i = 0; i < extraParts.Count; i++) DurationSelectionHost.Set(extraParts[i].GetNote(0UL)!, new("k a", i == 0 ? .5 : 2, DurationCandidatePolicy.ExpandedExperimental));
            Render(source, extraParts[0], "multi-part", extraParts.ToArray());
            string untouched = Hash(extraParts[1].WaveFilePath);
            extraParts[0].GetNote(0UL)!.NoteVelocity = 17; source.Commit(true);
            Render(source, extraParts[0], "multi-part-velocity");
            var untouchedStatus = NativeSampledTiming.ReadStatus((nint)extraParts[1]);
            states.Add(new { name = "multi-part-cache", untouchedStatus.Scopes, untouchedStatus.CacheKeys, exact = Hash(extraParts[1].WaveFilePath) == untouched }); SaveStates();
            Check(Hash(extraParts[1].WaveFilePath) == untouched && untouchedStatus.Scopes == 0 && extraParts[1].HasValidRenderedWave,
                "Native edit forced a render or changed PCM in another controlled Part.");
            var destination = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
            try
            {
                destination.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)); destination.InsertTempo(VSMRelTick.Zero, 12000);
                var destinationTrack = (WIVSMMidiTrack)destination.InsertTrackEx(0, VSMTrackType.Midi, "copy-validation")!;
                var destinationPart = destinationTrack.InsertPart(VSMAbsTick.Zero, new VSMRelTick(3840), "copy-validation")!;
                destinationPart.SetVoiceBankID(part.VoiceBankID); destination.InterpolatedMasterVolume(); destination.Commit(false); BreathVolumeService.ResetHistory(destination);
                var pasted = destinationPart.DuplicateNote(new VSMRelTick(960), part.GetNote(0UL)!)!;
                destination.Commit(true);
                Check(DurationSelectionHost.Get(pasted).Ratio == DurationSelectionHost.Get(part.GetNote(0UL)!).Ratio, "Cross-sequence copy lost its duration value.");
                string pastedHash = Render(destination, destinationPart, "cross-sequence-copy");
                destination.Undo(); Check(destinationPart.NumNotes == 0, "Cross-sequence copy Undo did not remove its target.");
                destination.Redo(); destinationPart = (WIVSMMidiPart)destinationTrack.GetPart(0UL)!;
                Check(Render(destination, destinationPart, "cross-sequence-redo") == pastedHash, "First-paste history restored the wrong graph or duration identity.");
            }
            finally { destination.Close(); }
        }
        finally
        {
            source.Close(); harmony.UnpatchAll(harmony.Id); application.Shutdown();
        }
        Console.WriteLine("duration.host=shared undo/redo, save rollback, load and cache passed");
    }
    private static void ValidateEditor(string directory)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var editor = new DurationSelectionEditor();
            int commits = 0; double ratio = 0; DurationCandidatePolicy policy = 0;
            editor.ApplyRequested += (r, p) => { commits++; ratio = r; policy = p; };
            editor.Show(new("k a", 7.375, DurationCandidatePolicy.Native), 1000);
            editor.Apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(commits == 1 && ratio == 7.375, "Opening the panel clamped an off-scale saved value.");
            editor.Milliseconds.Text = "250,125"; editor.Expanded.IsChecked = true;
            Check(commits == 1 && editor.Percent.Text == "25,0125", "Typing wrote values or unit conversion ignored culture.");
            editor.Apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(commits == 2 && Math.Abs(ratio - .250125) < 1e-9 && policy == DurationCandidatePolicy.ExpandedExperimental, "Precise panel submitted an incorrect candidate.");
            editor.Percent.Text = "NaN"; editor.Apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(commits == 2, "Invalid input was submitted.");
            editor.Reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(commits == 3 && ratio == 1 && policy == DurationCandidatePolicy.Native, "Reset retained experimental eligibility.");
            editor.Show(new("@N x 7", .5), 1000);
            var border = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 42)), Child = editor };
            border.Measure(new Size(314, double.PositiveInfinity)); border.Arrange(new Rect(new Point(), border.DesiredSize)); border.UpdateLayout();
            var bitmap = new RenderTargetBitmap(314, (int)Math.Ceiling(border.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(border); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, "duration-panel.png")); png.Save(output);
            Console.WriteLine("duration.ui=precise units, culture, invalid input, off-scale values and reset passed");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private static void FailWrite() { if (_failSave) throw new IOException("Injected isolated duration save failure."); }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}
#endif
