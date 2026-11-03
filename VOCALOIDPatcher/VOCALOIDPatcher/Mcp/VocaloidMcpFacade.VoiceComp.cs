using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.VoiceComp;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private static SoundPlayer? _compPreviewPlayer;
    private static string? _compPreviewPath, _compPreviewJob, _compPreviewClient;
    private static bool _compPhrasePlaying, _compPreviewCleanup;
    private static object VoiceCompCatalog() => new
    {
        query_tool = "v6_query_voice_comp", edit_tool = "v6_edit_voice_comp", preview_tool = "v6_preview_voice_comp",
        operations = VoiceCompEdit.Operations, maximum_batch_notes = 128,
        source_roles = new[] { "body", "consonant", "vowel" }, preview_actions = new[] { "source", "result", "phrase", "stop" },
        full_recipe_schema = "TimingCompRecipe; same as v6_edit_timing comp.set",
        units = "milliseconds; source/output boundary metadata in samples including Part presend",
        history = "one atomic batch per undo step; previews do not enter history",
        source_capture = "same-bank immutable pronunciation/EVEC/expression snapshot; melody remains owned by target",
        host_verified = false,
    };

    private static int[] CompIndices(JsonElement arguments, WIVSMMidiPart part)
    {
        if (Element(arguments, "note_indices") is { ValueKind: not JsonValueKind.Null } supplied)
        {
            if (supplied.ValueKind != JsonValueKind.Array || supplied.GetArrayLength() is < 1 or > 128)
                throw Fault("invalid_request", "note_indices must contain 1–128 indices.");
            var values = supplied.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int index)
                ? index : throw Fault("invalid_request", "Note indices must be integers.")).ToArray();
            if (values.Distinct().Count() != values.Length) throw Fault("invalid_request", "Duplicate note index.");
            foreach (int index in values) _ = TimingNote(part, index);
            return values;
        }
        int offset = Int(arguments, "offset"), limit = Int(arguments, "limit", 50);
        if (offset < 0 || limit is < 1 or > 100) throw Fault("invalid_request", "offset must be nonnegative; limit must be 1–100.");
        return Enumerable.Range(offset, checked((int)Math.Min((ulong)limit, part.NumNotes > (ulong)offset ? part.NumNotes - (ulong)offset : 0))).ToArray();
    }

    private static object QueryVoiceComp(JsonElement arguments)
    {
        ValidateProject(arguments); var part = TimingPart(arguments);
        var indices = CompIndices(arguments, part); var state = VoiceCompHost.View(part);
        bool sources = Bool(arguments, "include_sources");
        return new
        {
            project = TimingProject(), enabled = Settings.VoiceCompEnabled, supported = !part.IsAi && string.IsNullOrEmpty(part.SecondaryVoiceBankID),
            catalog = VoiceCompCatalog(), total_notes = part.NumNotes,
            next_offset = Element(arguments, "note_indices") is { ValueKind: JsonValueKind.Array } || indices.Length == 0 || (ulong)(indices[^1] + 1) >= part.NumNotes ? (int?)null : indices[^1] + 1,
            state = state == null ? null : new { state.Status, state.Error, state.Generation, state.Dirty, state.Queued, state.Running,
                ready = !state.Dirty && !state.Running && !state.Queued && state.Status == "Ready", source_count = state.Cache.Count,
                composition = state.Composition == null ? null : new { state.Composition.SampleRate, state.Composition.Samples,
                    slices = state.Composition.Slices.Take(2048).Select(s => new { role = s.Role.ToString().ToLowerInvariant(),
                        s.SourceBegin, s.OutputBegin, s.Length, s.FadeIn, s.FadeOut }).ToArray(),
                    slices_truncated = state.Composition.Slices.Length > 2048 } },
            notes = indices.Select(index =>
            {
                var note = TimingNote(part, index); var plan = VoiceCompHost.Get(note);
                object[]? audio = null;
                if (sources && state?.Views.TryGetValue(index, out var views) == true)
                    audio = views.Take(2).Select((v, i) => (object)new
                    {
                        role = plan?.Consonant != null ? i == 0 ? "consonant" : "vowel" : plan?.Body != null ? "body" : "isolated",
                        sample_rate = v.Rate, samples = v.Samples, duration_ms = v.Samples * 1000d / v.Rate,
                        notes = v.Notes.Take(33).Select(n => new { note_index = n.Index, n.Phonemes, n.Boundaries, n.NoteBegin, n.NoteEnd, n.VowelIndex }).ToArray(),
                        peaks = v.Peaks.Where((_, p) => p % Math.Max(1, (int)Math.Ceiling(v.Peaks.Length / 128d)) == 0).Take(128).ToArray(),
                    }).ToArray();
                return new { note_index = index, note.Lyric, note.Phonemes, position_tick = note.RelPosTick.Value,
                    duration_tick = note.DurationTick.Value, pitch = note.NoteNumber, recipe = TimingMcpEdits.Comp(plan),
                    spans = state?.Spans.Where(s => s.NoteIndex == index).Take(128).ToArray(), sources = audio };
            }).ToArray(),
        };
    }

    private static object EditVoiceComp(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments); TimingRequestId(arguments);
        var edit = TimingInput<VoiceCompEdit>(arguments, "edit"); edit.Validate();
        bool dryRun = Bool(arguments, "dry_run"); Authorize(client, "Edit voice comp: " + edit.Op, false, dryRun);
        if (!Settings.VoiceCompEnabled) throw Fault("unsupported", "Voice comp is disabled.");
        var part = TimingPart(arguments); var notes = edit.NoteIndices.Select(i => TimingNote(part, i)).ToArray();
        WIVSMNote? donor = null;
        if (edit.Donor is { } address)
        {
            var other = MidiPart(Context().Vsm, address.TrackIndex, address.PartIndex);
            if (other.IsAi || !string.IsNullOrEmpty(other.SecondaryVoiceBankID) || other.VoiceBankID != part.VoiceBankID) throw Fault("unsupported", "Donor and target must use the same sampled voicebank.");
            donor = TimingNote(other, address.NoteIndex);
        }
        // Compute/validate every target before SetMany can create a history
        // entry. A missing source role in the last note cannot partially edit.
        var before = notes.Select(VoiceCompHost.Get).ToArray();
        var after = before.Select(p => TimingMcpEdits.EditComp(edit, p,
            donor == null ? null : source => VoiceCompHost.SnapshotSource(donor, source))).ToArray();
        int current = 0;
        VoiceCompHost.SetMany(notes, _ => after[current++], dryRun);
        int changed = before.Zip(after).Count(p => p.First != p.Second);
        return new { project = TimingProject(), dry_run = dryRun, changed_notes = dryRun ? 0 : changed, would_change_notes = changed,
            history_steps = !dryRun && changed > 0 ? 1 : 0,
            notes = edit.NoteIndices.Select((index, i) => new { note_index = index, recipe = TimingMcpEdits.Comp(after[i]) }).ToArray(),
            render_state = "asynchronous; query voice comp for current generation and errors" };
    }

    private static void StopCompPreview()
    {
        if (_compPreviewJob != null && _compPreviewClient != null) McpJobManager.Cancel(_compPreviewJob, _compPreviewClient);
        _compPreviewJob = _compPreviewClient = null;
        _compPreviewPlayer?.Stop(); _compPreviewPlayer?.Dispose(); _compPreviewPlayer = null;
        if (_compPhrasePlaying) { App.AudioPlayer?.StopPlay(); _compPhrasePlaying = false; }
        if (_compPreviewPath is { } file)
        { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } _compPreviewPath = null; }
    }

    private static object PreviewVoiceComp(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments); TimingRequestId(arguments); Authorize(client, "Preview voice comp", false, false);
        string action = String(arguments, "action") ?? "";
        if (action is not ("source" or "result" or "phrase" or "stop")) throw Fault("invalid_request", "action must be source/result/phrase/stop.");
        if (action == "stop") { StopCompPreview(); return new { stopped = true, project = TimingProject() }; }
        if (!Settings.VoiceCompEnabled) throw Fault("unsupported", "Voice comp is disabled.");
        var part = TimingPart(arguments); var note = TimingNote(part, Int(arguments, "note_index", -1));
        bool play = Bool(arguments, "play", true);
        if (!_compPreviewCleanup && System.Windows.Application.Current is { } app)
        { app.Exit += (_, _) => StopCompPreview(); _compPreviewCleanup = true; }
        if (action == "phrase")
        {
            if (play)
            {
                if (App.AudioPlayer == null || !part.Sequence!.IsFinishedRendering) throw Fault("temporarily_unavailable", "Native playback is unavailable or rendering.", true);
                if (VoiceCompHost.Owns(part) && VoiceCompHost.View(part) is not { Dirty: false, Running: false, Queued: false, Status: "Ready" })
                    throw Fault("temporarily_unavailable", "The current composition is not ready.", true);
                StopCompPreview();
                long start = Math.Max(0, part.Sequence.GetTickFromTime(part.AbsPosTick, -part.Sequence.PresendTimeSec).Value);
                App.AudioPlayer.SetPlayerCurrentPosition(new VSMAbsTick(start), part.Sequence); App.AudioPlayer.StartPlay(); _compPhrasePlaying = true;
            }
            return new { started = play, mode = "phrase", uses_native_transport = true, project = TimingProject() };
        }
        if (VoiceCompHost.View(part) is not { Dirty: false, Running: false, Queued: false, Status: "Ready", Wave: not null } state)
            throw Fault("temporarily_unavailable", "Current composition is pending, failed or stale; query voice-comp state.", true);
        int index = Int(arguments, "note_index"); string? path; long begin, end;
        if (action == "source")
        {
            string role = String(arguments, "source_role") ?? "body"; var plan = VoiceCompHost.Get(note);
            bool splice = plan?.Consonant != null;
            if (splice && role is not ("consonant" or "vowel") || !splice && role != (plan?.Body != null ? "body" : "isolated"))
                throw Fault("invalid_request", "source_role does not exist in this recipe.");
            if (!state.Views.TryGetValue(index, out var sources) || sources.Length <= (role == "vowel" ? 1 : 0))
                throw Fault("temporarily_unavailable", "Source audio is not ready.", true);
            var source = sources[role == "vowel" ? 1 : 0]; path = source.Path;
            if (source.Notes.Length == 0) throw Fault("operation_failed", "Source boundaries are unavailable.");
            begin = Math.Max(0, source.Notes[0].Boundaries[0] - source.Rate / 10);
            end = Math.Min(source.Samples, source.Notes[^1].Boundaries[^1] + source.Rate / 10);
        }
        else
        {
            path = part.WaveFilePath; var info = CompWave.Inspect(path); var sequence = part.Sequence!;
            var spans = state.Spans.Where(s => s.NoteIndex == index).ToArray();
            begin = spans.Length > 0 ? Math.Max(0, spans.Min(s => s.Begin) - info.Rate / 10) :
                Math.Max(0, (long)((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsPosTick) - .2) * info.Rate));
            end = spans.Length > 0 ? Math.Min(info.Frames, spans.Max(s => s.End) + info.Rate / 5) :
                Math.Min(info.Frames, (long)((sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsEndTick) + .2) * info.Rate));
        }
        var format = CompWave.Inspect(path); long length = end - begin;
        if (length < 1 || length > format.Rate * 120L) throw Fault("unsupported", "Preview clips must be between one sample and 120 seconds.");
        long generation = state.Generation;
        StopCompPreview();
        var job = StartJob("voice_comp.preview", client, async (cancellation, progress) =>
        {
            string output = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "McpVoiceComp", Guid.NewGuid().ToString("N") + ".wav");
            bool retained = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await Task.Run(() => { cancellation.ThrowIfCancellationRequested();
                    CompWave.Mix(output, new(format.Rate, length, [new(path, begin, 0, length)])); }, cancellation).ConfigureAwait(false);
                return await OnUiAsync<object>(() =>
                {
                    cancellation.ThrowIfCancellationRequested(); ValidateProject(arguments); Authorize(client, "Play comp preview", false, false);
                    if (!Settings.VoiceCompEnabled || VoiceCompHost.View(part) != state || state.Generation != generation || state.Dirty || state.Running || state.Queued)
                        throw Fault("stale_project", "Composition changed while preparing preview.", true);
                    if (play)
                    {
                        _compPreviewPlayer = new SoundPlayer(output); _compPreviewPlayer.Play(); _compPreviewPath = output; retained = true;
                    }
                    return new { preview_started = play, mode = action, sample_rate = format.Rate, samples = length,
                        duration_ms = length * 1000d / format.Rate, project = TimingProject() };
                }).ConfigureAwait(false);
            }
            catch (McpFaultException e) { throw new McpJobManager.Fault(new(e.Code, e.Message, e.Retryable)); }
            finally { if (!retained && File.Exists(output)) { try { File.Delete(output); } catch (IOException) { } } }
        });
        _compPreviewJob = job.JobId; _compPreviewClient = client.Id;
        return job;
    }
}
