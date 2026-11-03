#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.PhonemeVolume;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

[CLSCompliant(false)]
public static class VoiceCompValidation
{
    public static void Run(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { Execute(manager, dse, database, directory); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw new InvalidOperationException("Voice-comp validation failed.", error);
    }
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); Thread.Sleep(5);
    }
    private static T Wait<T>(Task<T> task) { while (!task.IsCompleted) Pump(); return task.GetAwaiter().GetResult(); }
    private static void RejectArchiveWrite() => throw new IOException("Injected voice-comp archive write failure.");
    private static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory); var application = new Application();
        Patcher.ConfigManager = new ConfigManager(Path.Combine(directory, "isolated-config.json"));
        Patcher.DataDir = Path.GetDirectoryName(Path.GetDirectoryName(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")))!;
        foreach (var pair in new (string, object)[] { ("SequenceManager", manager), ("DSEManager", dse), ("DatabaseManager", database) })
            typeof(Yamaha.VOCALOID.App).GetProperty(pair.Item1)!.SetValue(null, pair.Item2);
        var harmony = new Harmony("VOCALOIDPatcher.isolated-voice-comp");
        foreach (PatchBase patch in new PatchBase[] { new SampledTimingRenderStartPatch(), new SampledTimingRenderCompletionPatch(),
            new SampledTimingPhonemePositionsPatch(), new SampledTimingBeginPhonemePatch(), new SampledTimingEndPhonemePatch(),
            new ConsonantTimingCommitPatch(), new ConsonantTimingUndoPatch(), new ConsonantTimingRedoPatch(), new ConsonantTimingClosePatch(),
            new BreathVolumeCommitHistoryPatch(), new BreathVolumeUndoPatch(), new BreathVolumeRedoPatch(), new BreathVolumeDirtyPatch(),
            new BreathVolumeDuplicateNotePatch(), new BreathVolumeDuplicatePartPatch(), new BreathVolumeRemoveNotePatch(),
            new SampledTimingProjectSavePatch(), new SampledTimingProjectLoadPatch(), new BreathVolumeProjectSavePatch(), new BreathVolumeProjectLoadPatch(),
            new SampledTimingDuplicateSequencePatch(), new BreathVolumeDuplicateSequencePatch(),
            new VoiceCompCommitPatch(), new VoiceCompStartPatch(), new VoiceCompStopPatch(), new VoiceCompUndoPatch(), new VoiceCompRedoPatch(),
            new VoiceCompClosePatch(), new VoiceCompDirtyPatch(), new VoiceCompValidWavePatch(), new VoiceCompPlacementRangePatch(),
            new VoiceCompManagerDisposalPatch(), new BreathVolumeWavePathPatch() })
        {
            var target = AccessTools.Method(patch.TargetClass, patch.TargetMethodName, patch.ArgumentTypes);
            Check(target != null, "Missing host target: " + patch.PatchName);
            HarmonyMethod? Hook(string name) => AccessTools.Method(patch.GetType(), name) is { } method ? new HarmonyMethod(method) : null;
            harmony.Patch(target, Hook("Prefix"), Hook("Postfix"), finalizer: Hook("Finalizer"));
        }
        VoiceCompHost.Initialize();
        NativeSampledTiming.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        if (Environment.GetEnvironmentVariable("VOCALOID_CONTROLLER_DELETE_PROBE") == "1")
        {
            try { ControllerDeletionValidation.Execute(manager, directory); }
            finally { VoiceCompHost.DrainForManagerDisposal(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_POST_READY_PROBE") == "1")
        {
            try { Rendering.PostProcessingReadyValidation.Execute(manager, dse, database, directory); }
            finally { VoiceCompHost.DrainForManagerDisposal(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_POST_REUSE_PROBE") == "1")
        {
            try { Rendering.PostProcessingReuseValidation.Execute(manager, dse, database, directory); }
            finally { VoiceCompHost.DrainForManagerDisposal(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_COMP_BUSY_TEMPO_PROBE") == "1")
        {
            try { CompBusyTempoValidation.Execute(manager, dse, database, directory); }
            finally { VoiceCompHost.DrainForManagerDisposal(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_COMP_SUSTAIN_TIMING_PROBE") == "1")
        {
            try { CompSustainTimingValidation.Run(manager, directory); }
            finally { VoiceCompHost.DrainForManagerDisposal(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        if (Environment.GetEnvironmentVariable("VOCALOID_COMP_GENG_REPRO") == "1")
        {
            try
            {
                if (Environment.GetEnvironmentVariable("VOCALOID_COMP_PROJECT_REPRO") is { } project) ValidateSavedPart(manager, project, directory);
                else ValidateContinuationSource(manager, directory);
            }
            finally { VoiceCompHost.DrainForManagerDisposal(); harmony.UnpatchAll(harmony.Id); application.Shutdown(); }
            return;
        }
        var sequence = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        sequence.InsertTempo(VSMRelTick.Zero, 12000); sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
        var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "comp-validation")!;
        var part = track.InsertPart(new VSMAbsTick(480), new VSMRelTick(3840), "comp-validation")!;
        Check(part.SetVoiceBankID("BCNFCY43LB2LZCD4"), "Missing validation voicebank.");
        var notes = new[] { "a", "k a", "t a" }.Select((phones, i) => part.InsertNote(new VSMRelTick(480 + i * 480),
            new VSMNoteEvent(480, 60, 64), part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "か", phones, true, 0)!).ToArray();
        sequence.InterpolatedMasterVolume(); Check(sequence.Commit(false), "Fixture commit failed.");
        var records = new List<object>();
        void Record(object value) { records.Add(value); File.WriteAllText(Path.Combine(directory, "voice-comp-validation.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(value)); }
        CompSourceAudio Render(int[] indices, SourceRecipe? recipe, bool hold, string name)
        {
            CompNativeSource? prepared = null;
            while (prepared == null) { try { prepared = new CompNativeSource(manager, part, indices, recipe, hold); } catch (CompNativeCloseBusyException) { Pump(); } }
            using var source = prepared;
            return Wait(source.RenderAsync(Path.Combine(directory, name + ".wav"), CancellationToken.None));
        }
        VoiceCompHost.PartState Current(WIVSMMidiPart? target = null)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(120))
            {
                Pump(); var current = VoiceCompHost.View(target ?? part);
                if (current?.Status == "Failed") throw new InvalidOperationException(current.Error);
                if (current is { Dirty: false, Running: false, Wave: not null }) return current;
            }
            throw new TimeoutException("Composition did not settle.");
        }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            var native = Render([0, 1, 2], null, false, "native-copy");
            Check(native.Peaks.Max() > .0001f && part.NumNotes == 3 && !sequence.IsStaged, "Private copy altered source or produced silence.");
            Record(new { stage = "native-copy", native.Rate, native.Samples, native.Notes.Length, originalPreserved = true });
            if (Environment.GetEnvironmentVariable("VOCALOID_COMP_NATIVE_SMOKE") == "1") return;
            foreach (var trim in Enum.GetValues<CompTrim>())
            {
                VoiceCompHost.Set(notes[1], new(Body: new(LengthMs: 2000, Trim: trim)));
                var result = Current(); Check(result.Composition!.Slices.Count(s => s.Role == CompSourceRole.Body) == 1, "Body contribution was duplicated.");
                File.Copy(result.Wave!, Path.Combine(directory, "long-" + trim + ".wav"), true);
                Record(new { stage = "long-" + trim, hash = Hash(result.Wave!), contributions = result.Composition.Slices.Length });
            }
            string cachedBody = VoiceCompHost.View(part)!.Views[1][0].Path;
            for (int i = 0; i < 25; i++) VoiceCompHost.Set(notes[1], VoiceCompHost.Get(notes[1])! with { Body = new(LengthMs: 2000, Trim: CompTrim.Middle, OffsetMs: i) });
            long expectedGeneration = VoiceCompHost.View(part)!.Generation;
            VoiceCompHost.EnsureForExport(sequence);
            var ready = VoiceCompHost.View(part)!;
            Check(!ready.Dirty && !ready.Running && ready.Generation >= expectedGeneration, "Rapid trims lost the latest request.");
            Check(ready.Views[1][0].Path == cachedBody, "Trim regenerated an unchanged source: version=" + ready.NativeVersion + "; cache=" + string.Join(" | ", ready.Cache.Keys));
            Record(new { stage = "rapid-trim-cache-export-wait", edits = 25, passed = true });
            var contextual = Render([1], new(LengthMs: 2000, Context: new("a", "t a")), false, "source-context");
            Check(contextual.Notes.Length == 1 && contextual.Peaks.Max() > .0001f && part.NumNotes == 3, "Context leaked into the main score or ownership metadata.");
            Record(new { stage = "independent-pronunciation-context", passed = true });
            var edgePart = track.InsertPart(new VSMAbsTick(6000), new VSMRelTick(960), "comp-boundaries")!;
            Check(edgePart.SetVoiceBankID(part.VoiceBankID), "Cannot select boundary fixture bank.");
            var edgeNotes = new[] { "a", "k a" }.Select((phones, i) => edgePart.InsertNote(new VSMRelTick(i * 480),
                new VSMNoteEvent(480, 60, 64), edgePart.GetDefaultNoteExpression(), edgePart.GetDefaultAiNoteExpression(), "edge", phones, true, 0)!).ToArray();
            Check(sequence.Commit(true), "Cannot commit boundary fixture.");
            VoiceCompHost.Set(edgeNotes[0], new(Body: new(Trim: CompTrim.Middle)));
            VoiceCompHost.Set(edgeNotes[1], new(Isolation: new(true, 100)));
            var edgeOutput = Current(edgePart);
            long edgeEnd = (long)Math.Round((sequence.PresendTimeSec + sequence.GetTimeFromTick(edgePart.AbsBeginTick, edgeNotes[1].AbsEndTick) + .1) * 44100);
            Check(edgePart.DurationTick.Value == 960 && edgeNotes.All(n => n.DurationTick.Value == 480) &&
                edgeOutput.Composition!.Slices.Any(s => s.Role == CompSourceRole.Connected && s.OutputEnd == edgeEnd), "Boundary comp moved notes or clipped the extended endpoint.");
            var edgeRange = Yamaha.VOCALOID.AudioPlacementBuilder.CreateTimeRange(sequence, edgePart, Yamaha.VOCALOID.VAE.PlacementStorageType.File, null);
            Check(edgeRange.Len >= edgeEnd / 44100d, "Native playback clipped the Part-end extension.");
            VoiceCompHost.SetMany(edgeNotes, _ => null); _ = Current();
            Record(new { stage = "part-boundaries-and-overrun-range", passed = true });
            VoiceCompHost.Set(notes[1], null); VoiceCompHost.Set(notes[0], new(Isolation: new(true)));
            var independent = Current(); string independentPath = independent.Views[0][0].Path; string before = Hash(independentPath);
            notes[1].SetPhonemes("s a", true, 0); notes[1].NoteVelocity = 32; Check(sequence.Commit(true), "Successor edit failed.");
            independent = Current(); Check(Hash(independent.Views[0][0].Path) == before, "Successor changed isolated previous vowel PCM.");
            Check(independent.Views[0][0].Path == independentPath, "Successor invalidated an unrelated isolated source cache.");
            Record(new { stage = "independent-successor-invariance", passed = true });
            VoiceCompHost.Set(notes[1], new(Consonant: new(Phonemes: "k a", LengthMs: 2000), Vowel: new(Phonemes: "a", LengthMs: 2000, VowelIndex: 0)));
            var splice = Current(); Check(splice.Composition!.Slices.Any(s => s.Role == CompSourceRole.Consonant) &&
                splice.Composition.Slices.Any(s => s.Role == CompSourceRole.Vowel), "Splice is missing a contribution.");
            File.Copy(splice.Wave!, Path.Combine(directory, "splice.wav"), true);
            Record(new { stage = "splice", hash = Hash(splice.Wave!), slices = splice.Composition.Slices.Select(s => new { s.Role, s.SourceBegin, s.OutputBegin, s.Length }) });
            var snapshot = VoiceCompHost.Snapshot(sequence); Check(snapshot.Length == 2, "Archive snapshot lost recipes.");
            VoiceCompHost.Set(notes[1], VoiceCompHost.Get(notes[1])! with { Bypass = true });
            Check(BreathVolumeService.HandleUndo(sequence), "Shared undo failed.");
            Check(VoiceCompHost.Get(notes[1])?.Bypass == false, "Shared undo did not restore splice.");
            _ = Current();
            Record(new { stage = "history", passed = true });
            var beforeDonor = VoiceCompHost.Get(notes[1])!;
            var donorPanel = new CompEditorPanel(); donorPanel.Show(notes[1]); donorPanel.UseSource(notes[1]);
            AccessTools.Method(typeof(CompEditorPanel), "ApplyFields").Invoke(donorPanel, null);
            Check(VoiceCompHost.Get(notes[1])?.Consonant is { Lyric: not null, Phonemes: not null, Expression: not null },
                "Editing another field converted a donor snapshot back into a live pronunciation.");
            VoiceCompHost.Set(notes[1], beforeDonor); _ = Current();
            Record(new { stage = "donor-snapshot-fields", passed = true });
            string retained = VoiceCompHost.View(part)!.Wave!, retainedHash = Hash(retained);
            VoiceCompHost.Set(notes[1], beforeDonor with { Consonant = beforeDonor.Consonant! with { TrimBeginMs = 60000 } });
            try { _ = Current(); throw new Exception("Invalid trim unexpectedly rendered."); } catch (InvalidOperationException) { }
            Check(VoiceCompHost.View(part) is { Status: "Failed", Wave: null } failed && failed.LastCompletedWave == retained && File.Exists(retained),
                "Failure discarded the retained result or published it as current audio.");
            Check(VoiceCompHost.Substitute(part, "native-fallback") == "native-fallback", "Failure substituted stale composition audio.");
            try { VoiceCompHost.EnsureForExport(sequence); throw new Exception("Failed composition was exported."); } catch (InvalidOperationException) { }
            VoiceCompHost.Set(notes[1], beforeDonor); _ = Current();
            Check(Hash(VoiceCompHost.View(part)!.Wave!) == retainedHash, "Recovering from a failed edit changed the retained recipe.");
            Record(new { stage = "failure-retention-export-rejection", passed = true });
            string translationDir = Path.Combine(Patcher.DataDir, "translations"); Directory.CreateDirectory(translationDir);
            foreach (string language in new[] { "中文 (简体)", "English", "日本語", "中文 (繁體)" })
            {
                File.Copy(Path.Combine(Environment.CurrentDirectory, "translations", language + ".xml"), Path.Combine(translationDir, language + ".xml"), true);
                Check(TranslationManager.LoadLanguage(language), "Cannot load translations.");
                foreach (double width in new[] { 1040d, 720d, 360d })
                foreach (double scale in new[] { 1d, 1.5, 2 })
                foreach (double height in new[] { 200d, 272d, 360d })
                {
                    var panel = new CompEditorPanel { Width = width, Height = height }; panel.Show(notes[1]);
                    VisualTreeHelper.SetRootDpi(panel, new DpiScale(scale, scale));
                    panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
                    var image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(panel);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(directory, "ui-" + language + "-" + width + "-" + height + "-" + scale * 100 + ".png")); encoder.Save(output);
                }
            }
            Record(new { stage = "wpf", languages = 4, widths = 3, heights = new[] { 200, 272, 360 }, dpi = new[] { 100, 150, 200 }, passed = true });
            Check(part.WaveFilePath == VoiceCompHost.View(part)!.Wave, "Playback path did not select the composed audio.");
            var range = Yamaha.VOCALOID.AudioPlacementBuilder.CreateTimeRange(sequence, part, Yamaha.VOCALOID.VAE.PlacementStorageType.File, null);
            Check(Math.Abs(range.Len - VoiceCompHost.View(part)!.Composition!.Samples / 44100d) < 1e-9, "Playback placement cropped the composed file.");
            Record(new { stage = "effective-playback-range", range.Len, passed = true });
            var shell = RuntimeHelpers.GetUninitializedObject(typeof(Yamaha.VOCALOID.Sequence)); GC.SuppressFinalize(shell);
            var save = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence");
            Check((bool)save.Invoke(shell, [sequence, directory, "voice-comp-saved", ".vpr"])!, "Voice-comp project save failed.");
            Check(!VoiceCompHost.IsDirty(sequence), "Saved voice-comp document is dirty.");
            var saved = VoiceCompArchive.Read(Path.Combine(directory, "voice-comp-saved.vpr"));
            Check(saved.Length == 2, "Saved project lost comp recipes.");
            string savedHash = Hash(Path.Combine(directory, "voice-comp-saved.vpr")); var savedPlan = VoiceCompHost.Get(notes[1])!;
            VoiceCompHost.Set(notes[1], savedPlan with { SeamMs = 6 });
            var archiveWrite = AccessTools.Method(typeof(VoiceCompArchive), "Write");
            harmony.Patch(archiveWrite, prefix: new HarmonyMethod(AccessTools.Method(typeof(VoiceCompValidation), nameof(RejectArchiveWrite))));
            try
            {
                Check(!(bool)save.Invoke(shell, [sequence, directory, "voice-comp-saved", ".vpr"])!, "Injected save failure was accepted.");
                Check(Hash(Path.Combine(directory, "voice-comp-saved.vpr")) == savedHash && VoiceCompHost.IsDirty(sequence), "Save failure replaced the previous file or cleared dirty state.");
            }
            finally { harmony.Unpatch(archiveWrite, HarmonyPatchType.Prefix, harmony.Id); }
            VoiceCompHost.Set(notes[1], savedPlan); _ = Current();
            Record(new { stage = "save-failure-rollback", passed = true });
            var load = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "LoadProjectSequenceFile");
            object?[] loaded = [Path.Combine(directory, "voice-comp-saved.vpr"), null];
            var loadResult = ((Yamaha.VOCALOID.Sequence.LoadResult, bool))load.Invoke(shell, loaded)!;
            Check(loadResult.Item1 == Yamaha.VOCALOID.Sequence.LoadResult.Success, "Voice-comp project reopen failed.");
            var reopened = (WIVSMSequence)loaded[1]!;
            try { Check(VoiceCompHost.Snapshot(reopened).SequenceEqual(saved), "Reopened recipes differ."); }
            finally { VoiceCompHost.Close(reopened); reopened.Close(); }
            Record(new { stage = "save-reopen", entries = saved.Length, passed = true });

            // Independent manual reference: native duplicate, keep one note,
            // extend it, and call VSM's direct renderer without the comp builder.
            while (!SegmentedPhonemeRenderCoordinator.TryEnterCompSource()) Pump();
            WIVSMSequence? manual = null; string manualPath = Path.Combine(directory, "manual-long-source.wav");
            try
            {
                manual = manager.DuplicateSequence(sequence, new VSMSequenceData { SamplingRate = sequence.GetSamplingRate(), MaxNumTracks = 4, MaxUndoCount = 0 })!;
                Utils.NativeRenderSession.RegisterInternalSequence(manual);
                var manualPart = ((WIVSMMidiTrack)manual.GetTrack(0UL)!).GetPart(0UL)!;
                var owned = manualPart.Notes.ToArray();
                manualPart.RemoveNote(owned[2]); manualPart.RemoveNote(owned[0]);
                Check(owned[1].SetDuration(new VSMRelTick(1920)) && manual.Commit(false), "Manual long-note reference failed.");
                using var manualObserver = new WIVSMRendererObserver(); int manualStatus = 0;
                manualObserver.Completed += (_, e) => { if ((nint)e.MidiPart == (nint)manualPart) manualStatus = 1; };
                manualObserver.Canceled += (_, e) => { if ((nint)e.MidiPart == (nint)manualPart) manualStatus = -1; };
                Check(manual.AddRendererObserver(manualObserver), "Cannot observe manual reference.");
                try
                {
                    manual.BlockRenderingEnabled = false; manual.StartAsyncRendering();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while (manualStatus == 0 && timer.Elapsed < TimeSpan.FromSeconds(30))
                    { for (int i = 0; i < 8 && manual.InvokeRendererObserver(); i++) { } Pump(); }
                    Check(manualStatus == 1, "Manual reference render failed."); File.Copy(manualPart.WaveFilePath, manualPath, true);
                }
                finally { manual.StopAsyncRendering(); manual.RemoveRendererObserver(manualObserver); }
            }
            finally { if (manual != null) SegmentedPhonemeRenderCoordinator.CloseSequenceSafely(manual); SegmentedPhonemeRenderCoordinator.ExitCompSource(); }
            var production = Render([1], new(LengthMs: 2000), false, "production-long-source");
            string Pcm(string path)
            {
                var format = CompWave.Inspect(path); using var input = File.OpenRead(path); input.Position = format.Offset;
                var bytes = new byte[checked((int)(format.Frames * format.Align))]; input.ReadExactly(bytes); return Convert.ToHexString(SHA256.HashData(bytes));
            }
            Check(Pcm(production.Path) == Pcm(manualPath), "Production source differs from the manual independent-track reference.");
            Record(new { stage = "manual-independent-track-reference", pcm = Pcm(production.Path), passed = true });

            var restoredPlan = VoiceCompHost.Get(notes[1]);
            part.RemoveNote(notes[1]); sequence.Commit(true); Check(VoiceCompHost.Snapshot(sequence).Length == 1, "Deleted recipe leaked onto another note.");
            sequence.Undo(); part = ((WIVSMMidiTrack)sequence.GetTrack(0UL)!).GetPart(0UL)!; notes = part.Notes.ToArray();
            Check(VoiceCompHost.Get(notes[1]) == restoredPlan, "Native delete/undo lost the comp recipe."); _ = Current();
            Record(new { stage = "native-delete-undo", passed = true });

            var copyTrack = (WIVSMMidiTrack)sequence.GetTrack(0UL)!;
            var copiedPart = copyTrack.DuplicatePart(new VSMAbsTick(12000), part) ?? throw new InvalidOperationException("Cannot copy the comp Part.");
            Check(sequence.Commit(true), "Cannot commit the copied comp Part.");
            Check(VoiceCompHost.Get(copiedPart.GetNote(1UL)!) == restoredPlan, "Part copy lost the source recipe.");
            var copiedState = Current(copiedPart); var copiedFiles = copiedState.Files.Keys.ToArray();
            Check(copyTrack.RemovePart(copiedPart) && sequence.Commit(true), "Cannot delete copied comp Part.");
            Check(copiedFiles.All(path => !File.Exists(path)), "Deleting a completed Part retained its temporary audio.");
            sequence.Undo(); copyTrack = (WIVSMMidiTrack)sequence.GetTrack(0UL)!;
            copiedPart = copyTrack.MidiParts.Single(p => p.AbsPosTick.Value == 12000);
            Check(VoiceCompHost.Get(copiedPart.GetNote(1UL)!) == restoredPlan, "Part delete/undo lost the source recipe.");
            _ = Current(copiedPart);
            Check(copyTrack.RemovePart(copiedPart) && sequence.Commit(true), "Cannot clean up the copied comp Part.");
            part = ((WIVSMMidiTrack)sequence.GetTrack(0UL)!).GetPart(0UL)!; notes = part.Notes.ToArray(); _ = Current();
            Record(new { stage = "part-copy-delete-undo-and-file-cleanup", passed = true });

            Record(new { stage = "timing-input", notes = notes.Select(n => new { n.Phonemes, n.NoteVelocity, n.NoteNumber, position = n.RelPosTick.Value,
                duration = n.DurationTick.Value, positions = n.GetPhonemePositions() }) });

            SampledTimingHost.SetCoefficient(notes[2], .25f); _ = Current();
            string coefficientA = Hash(VoiceCompHost.View(part)!.Views[2][0].Path);
            SampledTimingHost.SetCoefficient(notes[2], .8f); _ = Current();
            Check(Hash(VoiceCompHost.View(part)!.Views[2][0].Path) != coefficientA, "Pure timing was not applied to a connected source.");
            Record(new { stage = "pure-timing", passed = true });
            string flatPitch = Hash(VoiceCompHost.View(part)!.Wave!);
            Check(notes[1].SetDirectPitch(VSMRelTick.Zero, -900) && notes[1].SetDirectPitch(new VSMRelTick(240), -700) &&
                notes[1].SetDirectPitch(new VSMRelTick(480), -900) && sequence.Commit(true), "Cannot create pitch-curve fixture.");
            _ = Current(); Check(Hash(VoiceCompHost.View(part)!.Wave!) != flatPitch, "Main-note pitch curve did not reach the selected source.");
            sequence.InsertTempo(new VSMRelTick(1200), 15000); Check(sequence.Commit(true), "Cannot create tempo fixture."); _ = Current();
            Record(new { stage = "main-pitch-and-tempo", passed = true });
            part.InsertController(VSMRelTick.Zero, VSMControllerType.Dynamics, 64);
            part.InsertController(new VSMRelTick(960), VSMControllerType.Dynamics, 110); sequence.Commit(true); _ = Current();
            string fullGain = Hash(VoiceCompHost.View(part)!.Wave!);
            PhonemeVolumeHost.Set(notes[1], 1, -6); _ = Current();
            Check(Hash(VoiceCompHost.View(part)!.Wave!) != fullGain, "Source phoneme gain did not reach the composed audio.");
            Record(new { stage = "main-controllers-and-phoneme-gain", passed = true });
            string transitionBefore = Hash(VoiceCompHost.View(part)!.Views[2][0].Path);
            using (var preparation = SampledTimingHost.PrepareTransitions(part))
            {
                var query = preparation.Query();
                var edge = query.Layout.Edges.First(e => e.Identity.Left?.Note.Note == 2 && e.Identity.Right?.Note.Note == 2);
                var controls = new TransitionControlPlan(query.Layout.Fingerprint, query.Controls.Junctions, [new(edge.Identity, -6)],
                    query.Controls.Onsets, query.Controls.Phases, query.Controls.Coverages);
                SampledTimingHost.CommitTransitions(preparation, preparation.Prepared.QueryTransitionControls(query.Layout, controls, preparation.Revision));
            }
            _ = Current(); Check(Hash(VoiceCompHost.View(part)!.Views[2][0].Path) != transitionBefore, "Native transition gain did not reach the owned stem.");
            Record(new { stage = "native-transition-before-composition", passed = true });
            SampledTimingHost.Find(sequence)!.SetTransitions(TransitionProjectData.Empty); _ = Current();
            string rawComposition = VoiceCompHost.View(part)!.Wave!;
            string beforeAceSource = VoiceCompHost.View(part)!.Views[1][0].Path;
            part.InsertController(VSMRelTick.Zero, VSMControllerType.WeightHmm, 80); sequence.Commit(true); _ = Current();
            Check(VoiceCompHost.View(part)!.Views[1][0].Path == beforeAceSource, "An inert ACE carrier regenerated native source audio.");
            rawComposition = VoiceCompHost.View(part)!.Wave!;
            AceParameters.AceAcousticHost.EnsureForExport(sequence);
            Check(part.WaveFilePath != rawComposition && Pcm(part.WaveFilePath) != Pcm(rawComposition), "ACE did not process the composed result.");
            Record(new { stage = "ace-after-composition", passed = true });

            VoiceCompHost.Set(notes[1], new(Body: new(LengthMs: 2000)));
            notes[1].SetPhonemes("k k#2 a a#2 *#1", true, 0); sequence.Commit(true); _ = Current();
            var evec = VoiceCompHost.View(part)!.Views[1].Single();
            Check(evec.Notes[0].Phonemes.Contains("#2") && evec.Peaks.Max() > .0001f, "EVEC compilation or source metadata was lost.");
            Record(new { stage = "evec", phonemes = evec.Notes[0].Phonemes, passed = true });
            var breathEffect = part.BreathEffect ?? throw new InvalidOperationException("Missing native breath effect.");
            Check(breathEffect.SetBypass(false) && breathEffect.SetBreathMode(VSMBreathMode.Often) &&
                breathEffect.SetExhalation(127) && sequence.Commit(true), "Cannot enable native breath fixture.");
            _ = Current(); string breathBefore = Pcm(VoiceCompHost.View(part)!.Views[1].Single().Path);
            BreathVolumeService.SetValues(sequence, part, [notes[1].CppObjPtr], 64); _ = Current();
            Check(VoiceCompHost.View(part)!.Views[1].Single().Peaks.Max() > .0001f, "BVL processing lost the EVEC source.");
            Record(new { stage = "bvl-before-composition", changed = Pcm(VoiceCompHost.View(part)!.Views[1].Single().Path) != breathBefore, passed = true });

            Check(part.SetVoiceBankID("BL8CEAM5N4XN3LFK"), "Missing Chinese validation voicebank.");
            notes[0].SetPhonemes("a", true, 4); notes[1].Lyric = "fong"; notes[1].SetPhonemes("f UN", true, 4); notes[2].SetPhonemes("t a", true, 4);
            sequence.Commit(true); _ = Current();
            var chinese = VoiceCompHost.View(part)!.Views[1].Single();
            Check(chinese.Notes[0].Phonemes == "f UN" && chinese.Peaks.Max() > .0001f, "Extended-pinyin source or metadata was lost.");
            Record(new { stage = "extended-pinyin", phonemes = chinese.Notes[0].Phonemes, passed = true,
                sdk = typeof(WIVSMSequence).Assembly.GetName().Version!.ToString() });

            // A diphthong's internal glide must not be postponed by a hidden
            // 250 ms source extension when only context isolation was requested.
            VoiceCompHost.Set(notes[0], null);
            Check(notes[1].SetPhonemes("aI", true, 4) && sequence.Commit(true), "Cannot create diphthong fixture.");
            VoiceCompHost.Set(notes[1], new(Isolation: new(true)));
            var isolatedResult = Current(); var automaticSource = isolatedResult.Views[1].Single();
            var automaticNote = automaticSource.Notes.Single(n => n.Index == 1);
            double noteMs = sequence.GetTimeFromTick(notes[1].AbsPosTick, notes[1].AbsEndTick) * 1000;
            double automaticMs = (automaticNote.NoteEnd - automaticNote.NoteBegin) * 1000d / automaticSource.Rate;
            Check(Math.Abs(automaticMs - noteMs) < 2 && automaticNote.Phonemes.Contains("aI"), "Isolation implicitly stretched the diphthong.");
            long outputEnd = isolatedResult.Composition!.Slices.First(s => s.Path == automaticSource.Path).OutputEnd;
            var sustainPanel = new CompEditorPanel(); sustainPanel.Show(notes[1]);
            var sourceLength = (System.Windows.Controls.TextBox)AccessTools.Field(typeof(CompEditorPanel), "_length").GetValue(sustainPanel)!;
            Check(sourceLength.IsEnabled, "Independent source length is disabled.");
            sourceLength.Text = (noteMs + 250).ToString(System.Globalization.CultureInfo.CurrentCulture);
            AccessTools.Method(typeof(CompEditorPanel), "ApplyFields").Invoke(sustainPanel, null);
            isolatedResult = Current(); var explicitSource = isolatedResult.Views[1].Single();
            var explicitNote = explicitSource.Notes.Single(n => n.Index == 1);
            double explicitMs = (explicitNote.NoteEnd - explicitNote.NoteBegin) * 1000d / explicitSource.Rate;
            Check(Math.Abs(explicitMs - noteMs - 250) < 2 && explicitSource.Path != automaticSource.Path &&
                isolatedResult.Composition!.Slices.First(s => s.Path == explicitSource.Path).OutputEnd == outputEnd,
                "Source length did not change generation independently of the output cut.");
            Check(BreathVolumeService.HandleUndo(sequence), "Source length undo failed."); _ = Current();
            Check(VoiceCompHost.Get(notes[1])!.Isolation!.SourceLengthMs == 0,
                "Source length undo did not restore the automatic recipe: " + System.Text.Json.JsonSerializer.Serialize(VoiceCompHost.Get(notes[1])));
            Check(VoiceCompHost.View(part)!.Views[1].Single().Path == automaticSource.Path,
                "Source length undo did not restore the automatic source cache: " + automaticSource.Path + " -> " + VoiceCompHost.View(part)!.Views[1].Single().Path +
                "; cache=" + string.Join(" | ", VoiceCompHost.View(part)!.Cache.Keys));
            Record(new { stage = "independent-diphthong-source-length", automaticMs, explicitMs, outputEnd, passed = true });

            CompEditorPanel? dragPanel = null;
            foreach (var layout in new[] { ("中文 (简体)", 1040d, 272d, 1.5), ("English", 360d, 200d, 2d) })
            {
                Check(TranslationManager.LoadLanguage(layout.Item1), "Cannot load source length handle language.");
                var panel = new CompEditorPanel { Width = layout.Item2, Height = layout.Item3 }; panel.Show(notes[1]);
                VisualTreeHelper.SetRootDpi(panel, new DpiScale(layout.Item4, layout.Item4));
                panel.Measure(new Size(layout.Item2, layout.Item3)); panel.Arrange(new Rect(0, 0, layout.Item2, layout.Item3)); panel.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(layout.Item2 * layout.Item4), (int)(layout.Item3 * layout.Item4), 96 * layout.Item4, 96 * layout.Item4, PixelFormats.Pbgra32);
                bitmap.Render(panel); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(directory, "source-length-" + layout.Item1 + ".png"))) png.Save(file);
                var waves = (CompWaveView)AccessTools.Field(typeof(CompEditorPanel), "_waves").GetValue(panel)!;
                var hits = (Rect[])AccessTools.Field(typeof(CompWaveView), "_lengthHits").GetValue(waves)!;
                Check(!hits[0].IsEmpty && hits[0].Width >= 32 && hits[0].Height >= 28, "Source length handle is missing or too small.");
                dragPanel = panel;
            }
            var dragWave = (CompWaveView)AccessTools.Field(typeof(CompEditorPanel), "_waves").GetValue(dragPanel)!;
            void PreviewLength(double targetMs, bool fine = false)
            {
                void Set(string field, object value) => AccessTools.Field(typeof(CompWaveView), field).SetValue(dragWave, value);
                double scale = Math.Clamp(noteMs / 120, 1, 100);
                Set("_source", 0); Set("_gesture", 6); Set("_down", new Point(100, 100)); Set("_initial", noteMs); Set("_candidate", noteMs);
                AccessTools.Method(typeof(CompWaveView), "UpdateCandidate").Invoke(dragWave,
                    [new Point(100 + (targetMs - noteMs) / scale * (fine ? 10 : 1), 100), fine ? System.Windows.Input.ModifierKeys.Shift : System.Windows.Input.ModifierKeys.None]);
                Check((double)AccessTools.Field(typeof(CompWaveView), "_candidate").GetValue(dragWave)! == Math.Round(targetMs), "Length drag scale or fine adjustment is incorrect.");
            }
            long dragGeneration = VoiceCompHost.View(part)!.Generation;
            PreviewLength(noteMs + 20, true);
            Check(VoiceCompHost.Get(notes[1])!.Isolation!.SourceLengthMs == 0 && VoiceCompHost.View(part)!.Generation == dragGeneration,
                "Length preview modified source data or started synthesis.");
            dragWave.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.LostMouseCaptureEvent });
            AccessTools.Method(typeof(CompWaveView), "CompleteGesture").Invoke(dragWave, null);
            Check(VoiceCompHost.View(part)!.Generation == dragGeneration, "Lost capture committed a length candidate.");
            PreviewLength(noteMs + 200);
            AccessTools.Method(typeof(CompWaveView), "CompleteGesture").Invoke(dragWave, null);
            AccessTools.Method(typeof(CompWaveView), "CompleteGesture").Invoke(dragWave, null);
            Check(VoiceCompHost.Get(notes[1])!.Isolation!.SourceLengthMs == Math.Round(noteMs + 200) &&
                VoiceCompHost.View(part)!.Generation == dragGeneration + 1, "Length release did not submit exactly once.");
            var dragged = Current(); var draggedSource = dragged.Views[1].Single(); var draggedNote = draggedSource.Notes.Single(n => n.Index == 1);
            Check(Math.Abs((draggedNote.NoteEnd - draggedNote.NoteBegin) * 1000d / draggedSource.Rate - noteMs - 200) < 2 &&
                dragged.Composition!.Slices.First(s => s.Path == draggedSource.Path).OutputEnd == outputEnd, "Waveform drag did not preserve source duration and output cut semantics.");
            Check(BreathVolumeService.HandleUndo(sequence), "Waveform source length undo failed."); _ = Current();
            Check(VoiceCompHost.Get(notes[1])!.Isolation!.SourceLengthMs == 0, "Waveform source length undo lost automatic mode.");
            Record(new { stage = "waveform-source-length-drag", previewOnly = true, lostCaptureCancels = true, singleSubmit = true, fineAdjustment = true, passed = true });
            ValidateContinuationSource(manager, directory);
            Record(new { stage = "continuation-source-and-rejection", passed = true });
        }
        finally
        {
            VoiceCompHost.DrainForManagerDisposal(); sequence.StopAsyncRendering();
            Check(SegmentedPhonemeRenderCoordinator.CompCleanupIdle, "Owned native source cleanup did not drain.");
            Record(new { stage = "native-cleanup-drained", passed = true });
            sequence.Close(); harmony.UnpatchAll(harmony.Id); application.Shutdown();
        }
    }

    private static void ValidateSavedPart(WIVSMSequenceManager manager, string project, string directory)
    {
        string extracted = Path.Combine(directory, "project-data"); Directory.CreateDirectory(extracted);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(project))
        using (var input = zip.GetEntry("Project/sequence.json")!.Open())
        using (var output = File.Create(Path.Combine(extracted, "sequence.json"))) input.CopyTo(output);
        var sequence = manager.OpenSequence(Path.Combine(extracted, "sequence.json"), Path.GetDirectoryName(typeof(WIVSMSequence).Assembly.Location)!,
            new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 32, MaxUndoCount = 100 }) ?? throw new InvalidOperationException("Cannot read saved fixture.");
        try
        {
            var part = ((WIVSMMidiTrack)sequence.GetTrack(0)!).GetPart(0)!;
            var timing = SampledTimingProjectArchive.Read(project).Where(p => p.Key.Track == 0 && p.Key.Part == 0).ToDictionary();
            var transitions = TransitionProjectArchive.Read(project).Data!.KeepNotes(n => n.Address.Track == 0 && n.Address.Part == 0);
            SampledTimingHost.Load(sequence, timing, TransitionArchiveData.From(transitions));
            SampledTimingVsmBinding.InvalidateRenderedPart(part, [part.GetNote(0)!]);
            using var observer = new WIVSMRendererObserver(); bool done = false;
            observer.Completed += (_, e) => { if ((nint)e.MidiPart == (nint)part) done = true; };
            sequence.AddRendererObserver(observer);
            sequence.BlockRenderingEnabled = false; sequence.StartAsyncRendering();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!done && timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                for (int i = 0; i < 8 && sequence.InvokeRendererObserver(); i++) { }
                SampledTimingHost.ThrowIfCompRenderFailed(sequence); Pump();
                done |= sequence.IsFinishedRendering && part.HasValidRenderedWave && part.HasValidRenderedScore;
            }
            sequence.StopAsyncRendering(); sequence.RemoveRendererObserver(observer);
            Check(done && part.HasValidRenderedWave, "Saved fixture baseline did not render: " + JsonSerializer.Serialize(new {
                sequence.IsFinishedRendering, part.HasValidRenderedWave, part.HasValidRenderedScore, timing = SampledTimingHost.Diagnostics(sequence) }));
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "saved-part-native", passed = true, elapsedMs = timer.ElapsedMilliseconds }));
            bool resizeProbe = Environment.GetEnvironmentVariable("VOCALOID_COMP_RESIZE_GAIN_PROBE") == "1";
            bool gainProbe = Environment.GetEnvironmentVariable("VOCALOID_COMP_SAVED_GAIN_PROBE") == "1" || resizeProbe;
            if (gainProbe)
            {
                Settings.AceParameterMapping = false; // This oracle measures Comp before ACE.
                PhonemeVolumeHost.Load(sequence, PhonemeVolumeArchive.Read(project).Where(e => e.Address.Track == 0 && e.Address.Part == 0).ToArray());
            }
            VoiceCompHost.Load(sequence, VoiceCompArchive.Read(project).Where(e => e.Address.Track == 0 && e.Address.Part == 0).ToArray());
            VoiceCompHost.EnsureForExport(sequence);
            var state = VoiceCompHost.View(part)!;
            Check(!state.Dirty && !state.Running && state.Wave != null && state.Status != "Failed", "Saved fixture comp did not finish.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "saved-part-comp", passed = true, elapsedMs = timer.ElapsedMilliseconds,
                sourceCount = state.Cache.Count, sliceCount = state.Composition!.Slices.Length, wave = CompWave.Inspect(state.Wave!).Frames }));
            if (resizeProbe) ValidateResizeGain(sequence, part, directory);
            else if (gainProbe) ValidateSavedGain(sequence, part, directory);
        }
        finally { VoiceCompHost.DrainForManagerDisposal(); sequence.StopAsyncRendering(); sequence.Close(); }
    }

    private static void ValidateResizeGain(WIVSMSequence sequence, WIVSMMidiPart part, string directory)
    {
        var note = part.GetNote(12)!;
        Check(note.Phonemes == "ts` u a AN", "Expected compound zhu a ang note.");
        Check(note.DurationTick.Value == 420, "Expected the saved 420-tick baseline.");
        PhonemeVolumeHost.SetPlan(note, new(note.Phonemes, [0, -8.3f, -6.1f, -8.9f]));
        var original = PhonemeVolumeHost.Get(note);
        string Timing() => string.Join(";", SampledTimingHost.Find(sequence)!.Document.Snapshot()
            .OrderBy(p => p.Key.Note).Select(p => p.Key.Note + ":" + p.Value.Fingerprint));
        string timing = Timing();
        var records = new List<object>();
        (double Rms, string Hash) Capture(string stage)
        {
            VoiceCompHost.EnsureForExport(sequence);
            var state = VoiceCompHost.View(part)!;
            Check(state is { Dirty: false, Running: false, Wave: not null } && PhonemeVolumeHost.Status(note) == "Ready",
                "Resized note did not reach a ready Comp result.");
            var span = state.Spans.Single(s => s.NoteIndex == 12 && s.Phoneme == "AN");
            var format = CompWave.Inspect(state.Wave!);
            long begin = span.Begin + format.Rate / 50, end = span.End - format.Rate / 50;
            Check(end > begin, "Resized AN has no interior PCM.");
            byte[] pcm = new byte[checked((int)(end - begin) * format.Align)];
            using (var input = File.OpenRead(state.Wave!))
            { input.Position = format.Offset + begin * format.Align; input.ReadExactly(pcm); }
            double energy = 0; int width = format.Bits / 8;
            for (int at = 0; at < pcm.Length; at += width)
            { double value = CompWave.Decode(pcm.AsSpan(at, width), format); energy += value * value; }
            double rms = Math.Sqrt(energy / (pcm.Length / width));
            Check(rms > 1e-6 && Timing() == timing, "Resize/gain lost audio or modified timing controls.");
            string hash = Convert.ToHexString(SHA256.HashData(pcm));
            File.Copy(state.Wave!, Path.Combine(directory, stage + ".wav"));
            var record = new { stage, passed = true, duration = note.DurationTick.Value, state.Status, state.Error,
                values = PhonemeVolumeHost.Get(note).Values, rms, hash, begin, end,
                spans = state.Spans.Where(s => s.NoteIndex == 12).ToArray() };
            records.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
            return (rms, hash);
        }
        var baseline = Capture("zhu-420");
        Check(note.SetDuration(new(480)) && sequence.Commit(), "Resize to 480 failed.");
        Check(PhonemeVolumeHost.Get(note).SameAs(original), "Resize lost saved gain values.");
        var resized = Capture("zhu-480");
        PhonemeVolumeHost.Set(note, 3, original.Values[3] - 6);
        var lower = Capture("zhu-480-minus6");
        double changeDb = 20 * Math.Log10(lower.Rms / resized.Rms);
        Check(Math.Abs(changeDb + 6) < .05 && lower.Hash != resized.Hash, "Resized AN gain did not change PCM by -6 dB.");
        sequence.Undo();
        Check(Capture("zhu-480-gain-undo").Hash == resized.Hash, "Gain Undo did not restore resized PCM.");
        sequence.Redo();
        Check(Capture("zhu-480-gain-redo").Hash == lower.Hash, "Gain Redo did not restore lowered PCM.");
        sequence.Undo(); sequence.Undo();
        Check(note.DurationTick.Value == 420 && PhonemeVolumeHost.Get(note).SameAs(original), "Resize Undo lost note/gain state.");
        Check(Capture("zhu-420-resize-undo").Hash == baseline.Hash, "Resize Undo did not restore baseline PCM.");
        sequence.Redo(); Check(note.DurationTick.Value == 480, "Resize Redo failed.");
        Check(Capture("zhu-480-resize-redo").Hash == resized.Hash, "Resize Redo did not restore resized PCM.");
        Check(note.SetDuration(new(420)) && sequence.Commit(), "Drag back to 420 failed.");
        Check(Capture("zhu-420-drag-back").Hash == baseline.Hash, "Drag back did not restore baseline PCM.");
        records.Add(new { stage = "resize-gain-and-history", passed = true, changeDb, originalProjectEdited = false });
        File.WriteAllText(Path.Combine(directory, "zhu-resize-gain.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidateSavedGain(WIVSMSequence sequence, WIVSMMidiPart part, string directory)
    {
        var note = part.GetNote(0)!;
        Check(note.Lyric == "dang" && note.Phonemes == "t AN", "The saved gain oracle must target dang / AN.");
        var original = PhonemeVolumeHost.Get(note);
        string Before() => SampledTimingHost.Find(sequence)!.Document.Snapshot().OrderBy(p => p.Key.Note)
            .Aggregate("", (s, p) => s + p.Key + p.Value.Fingerprint);
        string timing = Before();
        var results = new List<object>();
        (double Rms, string Hash) Capture(string name)
        {
            VoiceCompHost.EnsureForExport(sequence);
            var state = VoiceCompHost.View(part)!;
            Check(state is { Dirty: false, Wave: not null } && PhonemeVolumeHost.Status(note) == "Ready", "Current gain did not reach a ready Comp result.");
            var span = state.Spans.Single(s => s.NoteIndex == 0 && s.Phoneme == "AN");
            var format = CompWave.Inspect(state.Wave!);
            long begin = span.Begin + format.Rate / 50, end = span.End - format.Rate / 50;
            Check(end > begin, "AN has no interior PCM for comparison.");
            byte[] pcm = new byte[checked((int)(end - begin) * format.Align)];
            using (var input = File.OpenRead(state.Wave!))
            { input.Position = format.Offset + begin * format.Align; input.ReadExactly(pcm); }
            double energy = 0; int width = format.Bits / 8;
            for (int at = 0; at < pcm.Length; at += width)
            { double value = CompWave.Decode(pcm.AsSpan(at, width), format); energy += value * value; }
            double rms = Math.Sqrt(energy / (pcm.Length / width));
            Check(rms > 1e-6 && Before() == timing, "Gain processing lost audio or changed saved timing controls: " +
                JsonSerializer.Serialize(new { name, rms, timingUnchanged = Before() == timing, span, format, state.Spans }));
            string hash = Convert.ToHexString(SHA256.HashData(pcm));
            File.Copy(state.Wave!, Path.Combine(directory, name + ".wav"));
            results.Add(new { stage = name, passed = true, gain_db = PhonemeVolumeHost.Get(note).Values[1], rms, hash, begin, end, frames = format.Frames });
            return (rms, hash);
        }
        var baseline = Capture("dang-an-saved");
        PhonemeVolumeHost.Set(note, 1, original.Values[1] - 6);
        var lower = Capture("dang-an-minus6");
        double changeDb = 20 * Math.Log10(lower.Rms / baseline.Rms);
        Check(Math.Abs(changeDb + 6) < .05 && lower.Hash != baseline.Hash, "AN gain did not change the actual Comp PCM by -6 dB.");
        sequence.Undo();
        Check(PhonemeVolumeHost.Get(note).SameAs(original), "Gain Undo did not restore the saved value.");
        var undo = Capture("dang-an-undo");
        Check(undo.Hash == baseline.Hash, "Gain Undo did not restore AN PCM.");
        sequence.Redo();
        var redo = Capture("dang-an-redo");
        Check(redo.Hash == lower.Hash, "Gain Redo did not restore AN PCM.");
        results.Add(new { stage = "dang-an-pcm-gain-and-history", passed = true, changeDb, originalProjectEdited = false });
        File.WriteAllText(Path.Combine(directory, "dang-an-gain.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(results));
    }

    private static void ValidateContinuationSource(WIVSMSequenceManager manager, string directory)
    {
        var sequence = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        sequence.InsertTempo(VSMRelTick.Zero, 14600); sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
        var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "continuation-source")!;
        var part = track.InsertPart(new VSMAbsTick(11520), new VSMRelTick(7320), "continuation-source")!;
        Check(part.SetVoiceBankID("BETDB8W6KWZPYEB9"), "Missing Chinese validation voicebank.");
        var fixtures = new[] { (2160, 120, 63, "p ei"), (2280, 120, 63, "-"), (2400, 240, 62, "m u"),
            (2640, 120, 63, "ts\\ y"), (2760, 120, 63, "-"), (2880, 240, 65, "k @N") };
        foreach (var (position, duration, pitch, phones) in fixtures)
            Check(part.InsertNote(new VSMRelTick(position), new VSMNoteEvent(duration, (byte)pitch, 64),
                part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "fixture", phones, true, 4) != null, "Cannot insert continuation fixture.");
        sequence.InterpolatedMasterVolume(); Check(sequence.Commit(false), "Cannot commit continuation fixture.");
        if (Environment.GetEnvironmentVariable("VOCALOID_COMP_CONTROL_FIXTURE") is { } controls)
        {
            var timing = SampledTimingProjectArchive.Read(controls).Where(p => p.Key.Track == 0 && p.Key.Part == 0 && p.Key.Note is >= 3 and <= 8)
                .ToDictionary(p => p.Key with { Note = p.Key.Note - 3 }, p => p.Value);
            var transitions = TransitionProjectArchive.Read(controls);
            var filtered = transitions.Data?.KeepNotes(n => n.Address.Track == 0 && n.Address.Part == 0 && n.Address.Note is >= 3 and <= 8);
            if (filtered != null) filtered = filtered with { Notes = filtered.Notes.Select(n => n with { Address = n.Address with { Note = n.Address.Note - 3 } }).ToImmutableArray() };
            SampledTimingHost.Load(sequence, timing, filtered == null ? null : TransitionArchiveData.From(filtered));
        }
        else
        {
            // A bounded regression fixture: two continuation notes plus
            // inherited offsets from a wider connected pronunciation context.
            var notes = part.Notes.ToArray();
            var timing = new Dictionary<TimingNoteAddress, SampledTimingPlan>();
            foreach (var (index, boundary, frames) in new[] { (0, 0, -7), (1, 1, 2), (2, 0, 2), (2, 2, 7), (3, 0, 7), (5, 2, 4) })
            {
                var address = SampledTimingVsmBinding.Address(notes[index]);
                timing[address] = PhonemeBoundaryEdit.WithBoundary(timing.GetValueOrDefault(address) ?? new(notes[index].Phonemes, []),
                    boundary, (float)(frames / TransitionProjectBinding.FramesPerSecond));
            }
            var root = new TransitionProjectNote(Guid.NewGuid(), SampledTimingVsmBinding.Address(notes[0]));
            var next = new TransitionProjectNote(Guid.NewGuid(), SampledTimingVsmBinding.Address(notes[2]));
            var p = new TransitionProjectPhone(root.Id, 0, "p"); var ei = new TransitionProjectPhone(root.Id, 1, "ei");
            var m = new TransitionProjectPhone(next.Id, 0, "m");
            var input = new TransitionProjectEdge(null, p, "Sil", "p", 0, false, 0);
            var body = new TransitionProjectEdge(p, ei, "p", "ei", 0, false, 0);
            var output = new TransitionProjectEdge(ei, m, "ei", "m", 0, false, 0);
            var transitions = new TransitionProjectData(1, [root, next],
                [new([new(input, TransitionEndpoint.End), new(body, TransitionEndpoint.Begin)], 3 / TransitionProjectBinding.FramesPerSecond) { TimingScale = new(p, 11) },
                 new([new(body, TransitionEndpoint.End), new(output, TransitionEndpoint.Begin)], 6 / TransitionProjectBinding.FramesPerSecond) { TimingScale = new(ei, 25) }], []);
            SampledTimingHost.Load(sequence, timing, TransitionArchiveData.From(transitions));
        }
        using var source = new CompNativeSource(manager, part, Enumerable.Range(0, fixtures.Length).ToArray(), extendEnd: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var audio = Wait(source.RenderAsync(Path.Combine(directory, "continuation-source.wav"), cancellation.Token));
            Check(audio.Peaks.Max() > .0001f && audio.Notes.Length == fixtures.Length, "Continuation source lost owned audio.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "continuation-source", passed = true, elapsedMs = timer.ElapsedMilliseconds, audio.Notes }));
            string before = SampledTimingHost.Find(sequence)!.Transitions.Snapshot.Fingerprint;
            Check(SampledTimingHost.Find(source.Sequence)!.Transitions.Snapshot.Data!.Junctions.Length == 2 &&
                !SampledTimingHost.HasNativeRenderFailure((nint)source.Part), "Inherited transition controls were skipped or failed.");
            SampledTimingHost.Recover(source.Sequence, new NotSupportedException("Injected private source rejection."));
            timer.Restart(); bool rejected = false;
            try { _ = Wait(source.RenderAsync(Path.Combine(directory, "rejected-source.wav"), cancellation.Token)); }
            catch (InvalidOperationException error) { rejected = error.Message.StartsWith("Voice-comp source preparation failed:", StringComparison.Ordinal); }
            Check(rejected && timer.Elapsed < TimeSpan.FromSeconds(3), "Rejected source waited for a completion notification.");
            Check(before == SampledTimingHost.Find(sequence)!.Transitions.Snapshot.Fingerprint, "Source fitting rewrote the owning transition archive.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "private-source-rejection", passed = true, elapsedMs = timer.ElapsedMilliseconds }));
        }
        finally
        {
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "continuation-source-state", elapsedMs = timer.ElapsedMilliseconds,
                finished = source.Sequence.IsFinishedRendering, wave = source.Part.HasValidRenderedWave, score = source.Part.HasValidRenderedScore,
                timing = SampledTimingHost.Diagnostics(source.Sequence),
                notes = source.Part.Notes.Select(n => new { n.Phonemes, positions = n.GetPhonemePositions(), verified = SampledTimingHost.VerifiedPositions(n) }) }));
            source.Dispose(); VoiceCompHost.DrainForManagerDisposal(); sequence.StopAsyncRendering(); sequence.Close();
        }
    }
}
#endif
