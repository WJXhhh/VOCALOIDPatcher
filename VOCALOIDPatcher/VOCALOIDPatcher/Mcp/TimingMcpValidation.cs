#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Patch;
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

// Compiled only into the separate, nondeployable validation assembly. Uses
// real VSM/DSE and the production facade, jobs, lease and history adapters.
[CLSCompliant(false)]
public static class TimingMcpValidation
{
    public static void Run(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { Execute(manager, dse, database, directory); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw new InvalidOperationException("MCP timing acceptance failed.", error);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); Thread.Sleep(5);
    }
    private static void Execute(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory); var application = new Application();
        Patcher.ConfigManager = new ConfigManager(Path.Combine(directory, "isolated-config.json"));
        Patcher.DataDir = Path.GetDirectoryName(Path.GetDirectoryName(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")))!;
        Settings.McpEnabled = true; Settings.McpConfirmWrites = false;
        foreach (var pair in new (string, object)[] { ("SequenceManager", manager), ("DSEManager", dse), ("DatabaseManager", database) })
            typeof(Yamaha.VOCALOID.App).GetProperty(pair.Item1)!.SetValue(null, pair.Item2);
        var harmony = new Harmony("VOCALOIDPatcher.isolated-mcp-timing");
        foreach (PatchBase patch in new PatchBase[] { new McpRevisionCommitPatch(), new McpRevisionUndoPatch(), new McpRevisionRedoPatch(),
            new ConsonantTimingCommitPatch(), new ConsonantTimingUndoPatch(), new ConsonantTimingRedoPatch(), new ConsonantTimingClosePatch(),
            new BreathVolumeCommitHistoryPatch(), new BreathVolumeUndoPatch(), new BreathVolumeRedoPatch(), new BreathVolumeCanUndoPatch(), new BreathVolumeCanRedoPatch(), new BreathVolumeDirtyPatch(),
            new BreathVolumeDuplicateNotePatch(), new BreathVolumeRemoveNotePatch() })
        {
            HarmonyMethod? Hook(string name) => AccessTools.Method(patch.GetType(), name) is { } m ? new HarmonyMethod(m) : null;
            harmony.Patch(AccessTools.Method(patch.TargetClass, patch.TargetMethodName, patch.ArgumentTypes), Hook("Prefix"), Hook("Postfix"), finalizer: Hook("Finalizer"));
        }
        var source = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
        source.InsertTempo(VSMRelTick.Zero, 12000); source.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
        var track = (WIVSMMidiTrack)source.InsertTrackEx(0, VSMTrackType.Midi, "mcp-timing")!;
        var part = track.InsertPart(new VSMAbsTick(480), new VSMRelTick(3840), "mcp-timing")!;
        Check(part.SetVoiceBankID("BCNFCY43LB2LZCD4"), "Validation voicebank unavailable.");
        var notes = new[] { "a", "k a", "t a" }.Select((p, i) => part.InsertNote(new VSMRelTick(480 + i * 480),
            new VSMNoteEvent(480, 60, 64), part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "fixture", p, true, 0)!).ToArray();
        source.InterpolatedMasterVolume(); Check(source.Commit(false), "Fixture commit failed.");
        VocaloidMcpFacade.IsolatedTimingSource = source;
        var client = new BridgeClientInfo("isolated-timing", "isolated timing acceptance", "1", "in_process");
        var records = new List<object>(); int serial = 0;
        void Record(string name, object? data = null)
        {
            records.Add(new { name, data }); File.WriteAllText(Path.Combine(directory, "mcp-timing.json"), JsonSerializer.Serialize(records, BridgeProtocol.JsonOptions));
            Console.WriteLine("mcp-timing.pass=" + name);
        }
        BridgeResponse Request(string method, object fields, string? requestId = null)
        {
            var (project, revision) = McpRevisionTracker.Current();
            var json = JsonSerializer.SerializeToElement(fields, BridgeProtocol.JsonOptions);
            var args = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
            args.TryAdd("project_id", project); args.TryAdd("expected_revision", revision);
            args.TryAdd("client_request_id", requestId ?? "request-" + ++serial);
            return VocaloidMcpFacade.Handle(new(BridgeProtocol.Version, Guid.NewGuid().ToString("N"), method,
                JsonSerializer.SerializeToElement(args, BridgeProtocol.JsonOptions), client, "isolated"), CancellationToken.None);
        }
        JsonElement Complete(BridgeResponse response)
        {
            Check(response.Ok, response.Error?.Code + ": " + response.Error?.Message);
            var value = response.Result!.Value;
            if (!value.TryGetProperty("job_id", out var id)) return value;
            var deadline = DateTime.UtcNow.AddSeconds(30); JobInfo? job;
            do { Pump(); job = McpJobManager.Get(id.GetString()!, client.Id); Check(DateTime.UtcNow < deadline, "Timing job timeout."); }
            while (job!.Status is BridgeJobStatus.Queued or BridgeJobStatus.Running);
            Check(job.Status == BridgeJobStatus.Succeeded, job.Error?.Code + ": " + job.Error?.Message);
            return job.Result!.Value;
        }
        JsonElement Query(string kind) => Complete(Request("v6_query_timing", new { kind, track_index = 0, part_index = 0, note_index = 1 }));
        JsonElement Edit(TimingEdit edit, bool dry = false, string? id = null) => Complete(Request("v6_edit_timing", new { edit, dry_run = dry, track_index = 0, part_index = 0 }, id));
        try
        {
            Complete(Request("v6_session", new { action = "acquire_write" }));
            long revision = McpRevisionTracker.Current().Revision;
            var before = Query("note"); Query("settings"); Query("status"); var phonemes = Query("phonemes"); var transitions = Query("transitions");
            Check(revision == McpRevisionTracker.Current().Revision, "Read-only native planning changed project revision.");
            Check(!source.CanUndo(), "Read-only planning created history.");
            Record("reads-have-no-history-or-project-revision", new { phonemes, transitions });
            var edge = transitions.GetProperty("timing").GetProperty("edges")[0];
            string fingerprint = transitions.GetProperty("timing").GetProperty("layout_fingerprint").GetString()!;
            string edgeId = edge.GetProperty("transition_id").GetString()!;
            Complete(Request("v6_query_timing", new { kind = "range", track_index = 0, part_index = 0, transition_id = edgeId,
                layout_fingerprint = fingerprint, endpoint = "begin" }));
            Record("native-certified-transition-range");
            bool smoothingBefore = Settings.TransitionSmoothingEnabled;
            double crossfadeBefore = Settings.TransitionSmoothingCrossfadeMs;
            Edit(new() { Op = "boundary.move", NoteIndex = 1, BoundaryIndex = 1, DeltaFrames = -1 }, true);
            Edit(new() { Op = "coefficient.set", NoteIndex = 1, Coefficient = .5f }, true);
            Edit(new() { Op = "phoneme_gain.set", NoteIndex = 1, PhonemeIndex = 0, Decibels = -6 }, true);
            Edit(new() { Op = "transition.set", LayoutFingerprint = fingerprint, TransitionId = edgeId, Decibels = -6 }, true);
            Edit(new() { Op = "comp.set", NoteIndex = 1, Comp = new(new(LengthMs: 1800), Isolation: new(true, 100)) }, true);
            Complete(Request("v6_timing_settings", new { settings = new TimingSettingsEdit { Smoothing = false, CrossfadeMs = 12 }, dry_run = true }));
            Check(revision == McpRevisionTracker.Current().Revision && !source.CanUndo(), "Dry-run changed project/history.");
            Check(Settings.TransitionSmoothingEnabled == smoothingBefore && Settings.TransitionSmoothingCrossfadeMs == crossfadeBefore, "Dry-run wrote preferences.");
            Record("all-five-control-families-and-settings-dry-run");
            Edit(new() { Op = "boundary.move", NoteIndex = 1, BoundaryIndex = 1, DeltaFrames = -1 }, id: "boundary-idempotent");
            long editedRevision = McpRevisionTracker.Current().Revision;
            Check(editedRevision > revision && source.CanUndo(), "Timing edit omitted revision/history.");
            string changed = SampledTimingHost.Find(source)!.Document.Fingerprint;
            Edit(new() { Op = "boundary.move", NoteIndex = 1, BoundaryIndex = 1, DeltaFrames = -1 }, id: "boundary-idempotent");
            Check(editedRevision == McpRevisionTracker.Current().Revision && changed == SampledTimingHost.Find(source)!.Document.Fingerprint, "Retry applied an edit twice.");
            source.Undo(); Check(!source.CanUndo(), "One boundary edit required more than one undo.");
            Check(SampledTimingHost.Find(source)!.Document.Snapshot().Count == 0, "Undo did not restore timing.");
            source.Redo(); Check(changed == SampledTimingHost.Find(source)!.Document.Fingerprint, "Redo did not restore timing.");
            Edit(new() { Op = "note.reset", NoteIndex = 1 });
            Check(SampledTimingHost.Find(source)!.Document.Snapshot().Count == 0, "Note reset retained timing.");
            Record("boundary-write-idempotency-undo-redo-and-shared-reset");
            foreach (var edit in new TimingEdit[] { new() { Op = "coefficient.set", NoteIndex = 1, Coefficient = .5f },
                new() { Op = "coefficient.reset", NoteIndex = 1 }, new() { Op = "phoneme_gain.set", NoteIndex = 1, PhonemeIndex = 0, Decibels = -3 },
                new() { Op = "phoneme_gain.reset", NoteIndex = 1 }, new() { Op = "comp.set", NoteIndex = 1, Comp = new(new(LengthMs: 1800), Bypass: true) },
                new() { Op = "comp.reset", NoteIndex = 1 } }) { Edit(edit); Record(edit.Op); }
            transitions = Query("transitions"); edge = transitions.GetProperty("timing").GetProperty("edges")[0];
            fingerprint = transitions.GetProperty("timing").GetProperty("layout_fingerprint").GetString()!; edgeId = edge.GetProperty("transition_id").GetString()!;
            Edit(new() { Op = "transition.set", LayoutFingerprint = fingerprint, TransitionId = edgeId, Decibels = -3 });
            transitions = Query("transitions"); fingerprint = transitions.GetProperty("timing").GetProperty("layout_fingerprint").GetString()!;
            Edit(new() { Op = "transitions.reset", LayoutFingerprint = fingerprint });
            Record("transition-write-and-part-reset");
            var stale = Request("v6_edit_timing", new { expected_revision = 0, track_index = 0, part_index = 0,
                edit = new TimingEdit { Op = "note.reset", NoteIndex = 1 } });
            Check(!stale.Ok && stale.Error?.Code == "stale_project", "Stale write was accepted: " + stale.Error?.Code);
            var cancel = Request("v6_query_timing", new { kind = "phonemes", track_index = 0, part_index = 0, note_index = 1 });
            Check(cancel.Ok, "Cannot start cancellation fixture.");
            string jobId = cancel.Result!.Value.GetProperty("job_id").GetString()!; McpJobManager.Cancel(jobId, client.Id);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (McpJobManager.Get(jobId, client.Id)!.Status is BridgeJobStatus.Queued or BridgeJobStatus.Running)
            { Check(DateTime.UtcNow < until, "Cancel timeout."); Pump(); }
            Check(McpJobManager.Get(jobId, client.Id)!.Status == BridgeJobStatus.Cancelled, "Cancellation reached a commit.");
            Record("stale-write-rejected-and-query-cancelled");
            var pending = Request("v6_query_timing", new { kind = "phonemes", track_index = 0, part_index = 0, note_index = 1 });
            string pendingId = pending.Result!.Value.GetProperty("job_id").GetString()!;
            notes[1].NoteVelocity = 65; source.Commit(true);
            until = DateTime.UtcNow.AddSeconds(30);
            while (McpJobManager.Get(pendingId, client.Id)!.Status is BridgeJobStatus.Queued or BridgeJobStatus.Running)
            { Check(DateTime.UtcNow < until, "Concurrent input timeout."); Pump(); }
            Check(McpJobManager.Get(pendingId, client.Id)!.Error?.Code == "stale_project", "Concurrent native edit did not invalidate the candidate.");
            Record("concurrent-native-edit-invalidates-background-candidate");
            Complete(Request("v6_timing_settings", new { settings = new TimingSettingsEdit { CrossfadeMs = 12, PhaseAlignment = false } }));
            Check(Settings.TransitionSmoothingCrossfadeMs == 12 && !Settings.TransitionSmoothingPhaseAlignment, "Settings were not applied.");
            Record("global-settings-write");
            JsonElement CompEdit(VoiceCompEdit edit, bool dry = false) => Complete(Request("v6_edit_voice_comp", new { edit, dry_run = dry, track_index = 0, part_index = 0 }));
            var compQuery = Complete(Request("v6_query_voice_comp", new { track_index = 0, part_index = 0, note_indices = new[] { 1, 2 }, include_sources = true }));
            Check(compQuery.GetProperty("notes").GetArrayLength() == 2, "Voice-comp query lost targets.");
            revision = McpRevisionTracker.Current().Revision;
            CompEdit(new() { Op = "long_slice", NoteIndices = [1, 2], LengthMs = 1800, Trim = "keep_tail" }, true);
            Check(revision == McpRevisionTracker.Current().Revision && VoiceComp.VoiceCompHost.Get(notes[1]) == null, "Batch dry-run wrote a recipe.");
            CompEdit(new() { Op = "long_slice", NoteIndices = [1, 2], LengthMs = 1800, Trim = "keep_tail" });
            Check(VoiceComp.VoiceCompHost.Get(notes[1])?.Body?.LengthMs == 1800 && VoiceComp.VoiceCompHost.Get(notes[2])?.Body?.LengthMs == 1800, "Batch action missed a target.");
            source.Undo(); Check(VoiceComp.VoiceCompHost.Get(notes[1]) == null && VoiceComp.VoiceCompHost.Get(notes[2]) == null, "Batch required more than one undo.");
            source.Redo();
            CompEdit(new() { Op = "independent_sustain", NoteIndices = [1, 2], LengthMs = 2100, EndOffsetMs = 25, FadeMs = 8 });
            Check(VoiceComp.VoiceCompHost.Get(notes[1])?.Isolation?.SourceLengthMs == 2100, "Sustain source duration was lost.");
            CompEdit(new() { Op = "phoneme_splice", NoteIndices = [1, 2], SeamMs = 7 });
            CompEdit(new() { Op = "copy_source", NoteIndices = [1, 2], SourceRole = "consonant", Donor = new(0, 0, 0) });
            Check(VoiceComp.VoiceCompHost.Get(notes[1])?.Consonant?.Phonemes == notes[0].Phonemes, "Donor snapshot mismatch.");
            CompEdit(new() { Op = "bypass", NoteIndices = [1, 2], Bypass = true });
            Check(VoiceComp.VoiceCompHost.Get(notes[2])?.Bypass == true, "Bypass missed a target.");
            var invalidBatch = Request("v6_edit_voice_comp", new { track_index = 0, part_index = 0,
                edit = new VoiceCompEdit { Op = "reset", NoteIndices = [1, 999] } });
            Check(!invalidBatch.Ok && VoiceComp.VoiceCompHost.Get(notes[1]) != null, "Invalid batch partially reset a note.");
            CompEdit(new() { Op = "reset", NoteIndices = [1, 2] });
            Record("voice-comp-query-three-actions-batch-history-snapshot-bypass-and-atomic-rejection");
            Complete(Request("v6_preview_voice_comp", new { action = "phrase", track_index = 0, part_index = 0, note_index = 1, play = false }));
            var unavailablePreview = Request("v6_preview_voice_comp", new { action = "source", track_index = 0, part_index = 0, note_index = 1, source_role = "isolated", play = false });
            Check(!unavailablePreview.Ok && unavailablePreview.Error?.Code == "temporarily_unavailable", "Preview accepted stale or missing audio.");
            Complete(Request("v6_preview_voice_comp", new { action = "stop" }));
            Record("voice-comp-preview-contract-and-unready-audio-rejection");
        }
        finally
        {
            VoiceComp.VoiceCompHost.DrainForManagerDisposal();
            VocaloidMcpFacade.IsolatedTimingSource = null; SampledTimingHost.Close(source); source.Close();
            harmony.UnpatchAll(harmony.Id); application.Shutdown();
        }
    }
}
#endif
