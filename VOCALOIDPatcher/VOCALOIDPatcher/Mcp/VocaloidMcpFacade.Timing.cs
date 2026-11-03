using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.PhonemeVolume;
using VOCALOIDPatcher.VoiceComp;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
#if PATCHER_ISOLATED_VALIDATION
    internal static WIVSMSequence? IsolatedTimingSource;
#endif
    private static bool _timingPlanning;
    private static string? _timingSettingsStamp;
    private static object TimingCatalog() => new
    {
        query_tool = "v6_query_timing", edit_tool = "v6_edit_timing", settings_tool = "v6_timing_settings",
        query_kinds = new[] { "status", "settings", "note", "phonemes", "transitions", "range", "comp" },
        operations = TimingEdit.Operations, frame_samples = 256, sample_rate = 44100,
        frame_ms = TimingMcpEdits.FrameSeconds * 1000, positive_direction = "right",
        transition_maximum_part_notes = 33, native_planning = "background_job",
        history = "one edit, including shared aliases, per undo step; preferences are outside project history",
        duration_selection = "retired; saved data is preserved, no new executor",
        host_verified = false,
    };

    private static object TimingSettingsSnapshot() => new
    {
        phoneme_timing = Settings.IntuitiveConsonantTiming, phoneme_volume = Settings.IndividualPhonemeVolume,
        transitions = Settings.IndividualTransitionControl, voice_comp = Settings.VoiceCompEnabled,
        smoothing = Settings.TransitionSmoothingEnabled, volume_threshold_db = Settings.TransitionSmoothingVolumeThresholdDb,
        crossfade_ms = Settings.TransitionSmoothingCrossfadeMs, loudness_window_ms = Settings.TransitionSmoothingLoudnessWindowMs,
        analysis_range_ms = Settings.TransitionSmoothingAnalysisRangeMs, phase_alignment = Settings.TransitionSmoothingPhaseAlignment,
        timing_offset_ms = Settings.TransitionSmoothingTimingOffsetMs,
    };

    private static T TimingInput<T>(JsonElement arguments, string field)
    {
        try { return TimingContract.Parse<T>(Element(arguments, field) ?? default); }
        catch (ArgumentException e) { throw Fault("invalid_request", e.Message); }
    }
    private static object TimingCall(Func<object> action)
    {
        try { return action(); }
        catch (NotSupportedException e) { throw Fault("unsupported", e.Message); }
        catch (Exception e) when (e is ArgumentException or System.IO.InvalidDataException or OverflowException)
        { throw Fault("invalid_request", e.Message); }
        catch (InvalidOperationException e) { throw Fault("operation_failed", e.Message, true); }
    }
    private static void TimingJobCheck(JsonElement arguments)
    {
        ValidateProject(arguments);
        if (_timingSettingsStamp != JsonSerializer.Serialize(TimingSettingsSnapshot(), BridgeProtocol.JsonOptions))
            throw Fault("stale_project", "Timing settings changed during planning; query again.", true);
    }
    private static void TimingEditValid(TimingEdit edit)
    {
        try { edit.Validate(); }
        catch (ArgumentException e) { throw Fault("invalid_request", e.Message); }
    }
    private static void TimingRequestId(JsonElement arguments)
    {
        if (string.IsNullOrWhiteSpace(String(arguments, "client_request_id"))) throw Fault("invalid_request", "client_request_id is required.");
    }
    private static WIVSMMidiPart TimingPart(JsonElement arguments)
    {
        (_, var source) = Context();
        var part = MidiPart(source, Int(arguments, "track_index", -1), Int(arguments, "part_index", -1));
        if (source.IsStaged) throw Fault("operation_failed", "Timing requires a committed source.", true);
        return part;
    }
    private static WIVSMNote TimingNote(WIVSMMidiPart part, int index)
    {
        if (index < 0 || (ulong)index >= part.NumNotes) throw Fault("invalid_reference", "note_index is outside this Part.");
        return part.GetNote((ulong)index) ?? throw Fault("invalid_reference", "The note no longer exists.");
    }
    private static object TimingProject()
    {
        var (id, revision) = McpRevisionTracker.Current();
        return new ProjectContext(McpBridgeService.InstanceId ?? "", id, revision);
    }
    private static object TimingNoteSnapshot(WIVSMNote note)
    {
        var session = SampledTimingHost.Find(note.Sequence!);
        var plan = session?.AllowsNote?.Invoke((nint)note) != false ? session?.Document.Get(SampledTimingVsmBinding.Address(note)) : null;
        var comp = VoiceCompHost.View(note.Parent!);
        return new
        {
            address = SampledTimingVsmBinding.Address(note), note_absolute_tick = note.AbsPosTick.Value,
            boundary_indices = Enumerable.Range(0, note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1).ToArray(),
            coefficient = plan?.Adjustments.SingleOrDefault(a => a.Kind == TimingAdjustment.NoteCoefficient)?.Value,
            coefficient_default = "native VEL mapping", velocity = note.NoteVelocity,
            adjustments = plan?.Adjustments.Select(a => new { a.Kind, a.Index, a.Value,
                offset_frames = a.Kind == TimingAdjustment.PhonemeBoundary ? (int?)Math.Round(a.Value / TimingMcpEdits.FrameSeconds) : null }).ToArray(),
            cached_boundary_limits = SampledTimingHost.BoundaryLimits(note),
            phoneme_volume = PhonemeVolumeHost.Diagnostics(note),
            comp = TimingMcpEdits.Comp(VoiceCompHost.Get(note)),
            comp_status = comp == null ? null : new { comp.Status, comp.Error, comp.Generation, comp.Queued, comp.Running, comp.Dirty },
        };
    }

    private static object QueryTiming(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments);
        string kind = String(arguments, "kind") ?? "status";
        if (kind == "settings") return new { project = TimingProject(), settings = TimingSettingsSnapshot(), catalog = TimingCatalog() };
        if (kind == "status") return new { project = TimingProject(), settings = TimingSettingsSnapshot(),
            diagnostics = SampledTimingHost.Diagnostics(Context().Vsm), catalog = TimingCatalog(), planning = _timingPlanning };
        if (kind is not ("note" or "comp" or "phonemes" or "transitions" or "range")) throw Fault("invalid_request", "Unknown timing query kind.");
        var part = TimingPart(arguments);
        if (kind is "note" or "comp") return new { project = TimingProject(), note = TimingNoteSnapshot(TimingNote(part, Int(arguments, "note_index", -1))) };
        int offset = Int(arguments, "offset"), limit = Int(arguments, "limit", 100);
        if (offset < 0 || limit is < 1 or > 200) throw Fault("invalid_request", "offset must be nonnegative and limit must be 1–200.");
        TimingPlanningAvailable();
        if (kind == "phonemes")
        {
            var note = TimingNote(part, Int(arguments, "note_index", -1));
            var snapshot = TimingNoteSnapshot(note);
            var prepared = new SampledTimingHost.PhonemePreparation(note, preserveTransitions: false);
            return TimingJob(client, arguments, false, prepared, async cancellation =>
            {
                var result = await Task.Run(() => prepared.Prepared.QueryPhonemes(prepared.Original, prepared.Revision, cancellation, 0), cancellation).ConfigureAwait(false);
                return await OnUiAsync<object>(() =>
                {
                    cancellation.ThrowIfCancellationRequested(); TimingJobCheck(arguments); prepared.Check();
                    return new { project = TimingProject(), note = snapshot, timing = TimingPhonemeResult(result, prepared.Address) };
                }).ConfigureAwait(false);
            });
        }
        SampledTimingHost.ForPhonemeEditing(part.Sequence!);
        var transition = SampledTimingHost.PrepareTransitions(part);
        return TimingJob(client, arguments, false, transition, async cancellation =>
        {
            var query = await Task.Run(() => transition.Query(cancellation), cancellation).ConfigureAwait(false);
            object result;
            if (kind == "range")
            {
                var edge = TimingMcpEdits.Edge(query.Layout, String(arguments, "layout_fingerprint"), String(arguments, "transition_id"));
                var endpoint = String(arguments, "endpoint") switch { "begin" => TransitionEndpoint.Begin, "split" => TransitionEndpoint.Split,
                    "end" => TransitionEndpoint.End, _ => throw new ArgumentException("endpoint must be begin, split, or end.") };
                var range = await Task.Run(() => transition.Prepared.QueryTransitionRange(query.Layout, query.Controls,
                    new(edge.Identity, endpoint), transition.Revision, cancellation, 0), cancellation).ConfigureAwait(false);
                result = new { query.Layout.Fingerprint, transition_id = TimingMcpEdits.Id(query.Layout, edge.Identity),
                    endpoint = endpoint.ToString().ToLowerInvariant(), range.ReachableFrames, range.Minimum, range.Maximum,
                    range.IsContiguous, range.Junction.Protected, aliases = TimingAliases(query.Layout, range.Junction),
                    range.NativePlans, range.Milliseconds };
            }
            else result = TimingTransitionResult(query.Execution, query.Controls, offset, limit);
            return await OnUiAsync<object>(() =>
            {
                cancellation.ThrowIfCancellationRequested(); TimingJobCheck(arguments);
                transition.Prepared.ValidateTransitionCommit(part, query.Execution, transition.Revision);
                if (transition.Session.Revision != transition.Revision) throw Fault("stale_project", "Timing changed during planning.", true);
                return new { project = TimingProject(), timing = result };
            }).ConfigureAwait(false);
        });
    }

    private static object TimingPhonemeResult(LocalConsonantTimingBackend.PhonemeLayout result, TimingNoteAddress address) => new
    {
        boundary_ticks_relative_to_note_start = result.Result.Boundaries,
        frame_ms = TimingMcpEdits.FrameSeconds * 1000,
        limits = result.Limits?.Select(p => new { address = p.Key, boundaries = p.Value,
            reason = "requested offset projected to nearest strictly ordered native boundary; stored request retained" }).ToArray(),
        affected_notes = result.Plans?.Select(p => new { address = p.Key, adjustments = p.Value?.Adjustments,
            boundary_ticks_relative_to_note_start = result.RelatedBoundaries?.GetValueOrDefault(p.Key) }).ToArray(),
        target = address, result.Result.ContextNotes, result.Result.PlannerRuns, result.Result.Milliseconds,
    };
    private static object[] TimingAliases(TransitionLayout layout, TransitionJunction junction) => junction.Aliases
        .Select(a => (object)new { transition_id = TimingMcpEdits.Id(layout, a.Edge), endpoint = a.Endpoint.ToString().ToLowerInvariant() }).ToArray();
    private static object TimingTransitionResult(TransitionExecutionPlan execution, TransitionControlPlan requested, int offset = 0, int limit = 200)
    {
        var layout = execution.Layout; var rows = execution.Segments.Where(r => r.To != null).ToArray();
        var joints = layout.ConditionalJunctions(requested);
        object Joint(TransitionIdentity edge, TransitionEndpoint endpoint)
        {
            var joint = joints.Single(j => j.Aliases.Contains(new(edge, endpoint)));
            return new { frame = joint.CurrentFrame, joint.Protected, joint.MinimumCandidate, joint.MaximumCandidate,
                aliases = TimingAliases(layout, joint), range_semantics = "geometric candidates; use kind=range for native-certified reachable frames" };
        }
        return new
        {
            layout_fingerprint = layout.Fingerprint, timing_revision = layout.Revision, total = layout.Edges.Length,
            offset, next_offset = offset + limit < layout.Edges.Length ? (int?)(offset + limit) : null,
            frame_ms = TimingMcpEdits.FrameSeconds * 1000, frame_origin = "native Part coordinates (includes presend); use queried frames directly",
            edges = layout.Edges.Skip(offset).Take(limit).Select((edge, i) => new
            {
                transition_id = TimingMcpEdits.Id(layout, edge.Identity), identity = edge.Identity,
                original = new { edge.Original.Begin, edge.Original.Split, edge.Original.End },
                applied = new { rows[offset + i].Begin, rows[offset + i].Split, rows[offset + i].End },
                begin = Joint(edge.Identity, TransitionEndpoint.Begin), split = Joint(edge.Identity, TransitionEndpoint.Split), end = Joint(edge.Identity, TransitionEndpoint.End),
                decibels = execution.Controls.Gains.FirstOrDefault(g => g.Edge == edge.Identity)?.Decibels ?? 0,
                phase = execution.Controls.Phases.FirstOrDefault(p => p.Edge == edge.Identity)?.Amount ?? 0,
                coverage = (execution.Controls.Coverages.FirstOrDefault(c => c.Edge == edge.Identity)?.Sides ?? TransitionSourceSides.None).ToString().ToLowerInvariant(),
                onset_frames = execution.Controls.Onsets.FirstOrDefault(o => o.Edge == edge.Identity)?.OffsetFrames ?? 0,
                source_progress_available = TransitionExpressionControl.Range(rows[offset + i], edge.Identity, requested) != null,
            }).ToArray(),
            boundary_limits = layout.BoundaryLimits?.Select(p => new { address = p.Key, boundaries = p.Value }).ToArray(),
        };
    }

    private static object EditTiming(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments); TimingRequestId(arguments);
        var edit = TimingInput<TimingEdit>(arguments, "edit"); TimingEditValid(edit);
        bool dryRun = Bool(arguments, "dry_run");
        Authorize(client, "Edit timing: " + edit.Op, false, dryRun);
        var part = TimingPart(arguments);
        if (part.IsAi) throw Fault("unsupported", "Timing controls require a sampled Part.");
        if (edit.Op.StartsWith("transition", StringComparison.Ordinal))
        {
            if (!Settings.IndividualTransitionControl) throw Fault("unsupported", "Individual transitions are disabled.");
            TimingPlanningAvailable(); SampledTimingHost.ForPhonemeEditing(part.Sequence!);
            var prepared = SampledTimingHost.PrepareTransitions(part);
            return TimingJob(client, arguments, !dryRun, prepared, async cancellation =>
            {
                var query = await Task.Run(() => prepared.Query(cancellation), cancellation).ConfigureAwait(false);
                var controls = TimingMcpEdits.Transition(edit, query.Layout, query.Controls, query.Execution);
                var execution = await Task.Run(() => prepared.Prepared.QueryTransitionControls(query.Layout, controls, prepared.Revision, cancellation, 0), cancellation).ConfigureAwait(false);
                return await OnUiAsync<object>(() =>
                {
                    cancellation.ThrowIfCancellationRequested(); TimingJobCheck(arguments); Authorize(client, "Commit timing", false, dryRun);
                    prepared.Prepared.ValidateTransitionCommit(part, execution, prepared.Revision);
                    if (prepared.Session.Revision != prepared.Revision) throw Fault("stale_project", "Timing changed during planning.", true);
                    string before = SampledTimingHost.TransitionData(part.Sequence!).Fingerprint;
                    if (!dryRun) SampledTimingHost.CommitTransitions(prepared, execution);
                    return new { project = TimingProject(), dry_run = dryRun, changed = !dryRun && before != SampledTimingHost.TransitionData(part.Sequence!).Fingerprint,
                        timing = TimingTransitionResult(execution, controls) };
                }).ConfigureAwait(false);
            });
        }
        var note = TimingNote(part, edit.NoteIndex!.Value);
        if (edit.Op.StartsWith("phoneme_gain.", StringComparison.Ordinal))
        {
            var before = PhonemeVolumeHost.Get(note);
            var after = edit.Op == "phoneme_gain.set" ? before.With(edit.PhonemeIndex!.Value, edit.Decibels!.Value) :
                edit.PhonemeIndex is { } index ? before.With(index, 0) : new PhonemeVolumePlan(note.Phonemes);
            PhonemeVolumeHost.SetPlan(note, after, dryRun);
            return new { project = TimingProject(), dry_run = dryRun, changed = !dryRun && !before.SameAs(after), values = after.Values };
        }
        if (edit.Op.StartsWith("comp.", StringComparison.Ordinal))
        {
            if (!Settings.VoiceCompEnabled) throw Fault("unsupported", "Voice Comp is disabled.");
            var before = VoiceCompHost.Get(note); var after = edit.Op == "comp.reset" ? null : TimingMcpEdits.Comp(edit.Comp!);
            VoiceCompHost.SetMany([note], _ => after, dryRun);
            return new { project = TimingProject(), dry_run = dryRun, changed = !dryRun && before?.Fingerprint != VoiceCompHost.Get(note)?.Fingerprint,
                comp = TimingMcpEdits.Comp(after), render_state = "asynchronous; inspect kind=comp for completion/error" };
        }
        if (!Settings.IntuitiveConsonantTiming) throw Fault("unsupported", "Phoneme timing is disabled.");
        TimingPlanningAvailable();
        var phone = new SampledTimingHost.PhonemePreparation(note);
        return TimingJob(client, arguments, !dryRun, phone, async cancellation =>
        {
            var candidate = await Task.Run(() =>
            {
                SampledTimingPlan plan = phone.Original;
                if (edit.Op.StartsWith("boundary.", StringComparison.Ordinal))
                {
                    int index = edit.BoundaryIndex!.Value;
                    int count = phone.Address.Topology.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    if (index > count) throw new ArgumentException("boundary_index exceeds phoneme count.");
                    int? frames = edit.OffsetFrames;
                    if (edit.Op == "boundary.move")
                    {
                        var current = phone.Prepared.QueryPhonemes(phone.Original, phone.Revision, cancellation, 0);
                        var limits = current.Limits != null && current.Limits.TryGetValue(phone.Address, out var found) ? found : [];
                        int applied = limits.FirstOrDefault(l => l.Index == index).AppliedFrames;
                        bool limited = limits.Any(l => l.Index == index);
                        int stored = (int)Math.Round((plan.Adjustments.SingleOrDefault(a => a.Kind == TimingAdjustment.PhonemeBoundary && a.Index == index)?.Value ?? 0) / TimingMcpEdits.FrameSeconds);
                        frames = checked((limited ? applied : stored) + edit.DeltaFrames!.Value);
                    }
                    plan = PhonemeBoundaryEdit.WithBoundary(plan, index, frames is { } value ? (float)(value * TimingMcpEdits.FrameSeconds) : null);
                }
                else if (edit.Op.StartsWith("coefficient.", StringComparison.Ordinal))
                    plan = new(plan.Topology, plan.Adjustments.Where(a => a.Kind != TimingAdjustment.NoteCoefficient)
                        .Concat(edit.Coefficient is { } value ? new[] { new TimingAdjustment(TimingAdjustment.NoteCoefficient, -1, value) } : []));
                else if (edit.Op == "note.reset") plan = new(plan.Topology, []);
                else throw new ArgumentException("Unsupported phoneme operation.");
                return phone.Query(plan.Adjustments.Count == 0 ? null : plan,
                    edit.Op.StartsWith("boundary.", StringComparison.Ordinal) ? edit.BoundaryIndex : null, cancellation, resetAll: edit.Op == "note.reset");
            }, cancellation).ConfigureAwait(false);
            SampledTimingHost.TransitionPreparation? next = null;
            try
            {
                next = await OnUiAsync(() => { cancellation.ThrowIfCancellationRequested(); TimingJobCheck(arguments); return phone.PrepareCandidate(candidate); }).ConfigureAwait(false);
                var execution = await Task.Run(() => phone.ValidateCandidate(candidate, next, cancellation), cancellation).ConfigureAwait(false);
                return await OnUiAsync<object>(() =>
                {
                    cancellation.ThrowIfCancellationRequested(); TimingJobCheck(arguments); Authorize(client, "Commit phoneme timing", false, dryRun);
                    phone.Check();
                    bool changed = !dryRun && phone.Commit(candidate, next, execution);
                    return new { project = TimingProject(), dry_run = dryRun, changed,
                        timing = TimingPhonemeResult(candidate.Layout, phone.Address) };
                }).ConfigureAwait(false);
            }
            finally { if (next != null) await OnUiAsync(() => { next.Dispose(); return true; }).ConfigureAwait(false); }
        });
    }

    private static void TimingPlanningAvailable()
    {
        if (_timingPlanning) throw Fault("temporarily_unavailable", "A timing plan is already running; wait for its job to finish.", true);
    }
    private static object TimingJob(BridgeClientInfo client, JsonElement arguments, bool write, IDisposable prepared,
        Func<CancellationToken, Task<object?>> action)
    {
        async Task<object?> Run(CancellationToken cancellation, Action<double> progress)
        {
            try { return await action(cancellation).ConfigureAwait(false); }
            catch (McpFaultException e) { throw new McpJobManager.Fault(new(e.Code, e.Message, e.Retryable,
                e.Details == null ? null : JsonSerializer.SerializeToElement(e.Details, BridgeProtocol.JsonOptions))); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A native input commit can retire a preparation before its
                // worker reaches the Dispatcher. Report the revision conflict
                // instead of leaking ObjectDisposedException as job_failed.
                McpFaultException? stale = null;
                await OnUiAsync(() => { try { TimingJobCheck(arguments); } catch (McpFaultException changed) { stale = changed; } return true; }).ConfigureAwait(false);
                if (stale != null) throw new McpJobManager.Fault(new(stale.Code, stale.Message, true,
                    stale.Details == null ? null : JsonSerializer.SerializeToElement(stale.Details, BridgeProtocol.JsonOptions)));
                string code = e is NotSupportedException ? "unsupported" : e is ArgumentException or System.IO.InvalidDataException ? "invalid_request" :
                    e is TimeoutException || e.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) ? "temporarily_unavailable" :
                    e.Message.Contains("stale", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("changed", StringComparison.OrdinalIgnoreCase) ? "stale_project" : "operation_failed";
                throw new McpJobManager.Fault(new(code, e.Message, code is "temporarily_unavailable" or "stale_project"));
            }
            finally { await OnUiAsync(() => { try { prepared.Dispose(); } finally { _timingPlanning = false; _timingSettingsStamp = null; } return true; }).ConfigureAwait(false); }
        }
        try
        {
            _timingSettingsStamp = JsonSerializer.Serialize(TimingSettingsSnapshot(), BridgeProtocol.JsonOptions);
            _timingPlanning = true;
            return write ? StartJob("timing.edit", client, Run) : McpJobManager.Start("timing.preview", client.Id, Run);
        }
        catch { prepared.Dispose(); _timingPlanning = false; _timingSettingsStamp = null; throw; }
    }

    private static object EditTimingSettings(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments); TimingRequestId(arguments);
        var edit = TimingInput<TimingSettingsEdit>(arguments, "settings");
        try { edit.Validate(); } catch (ArgumentException e) { throw Fault("invalid_request", e.Message); }
        bool dryRun = Bool(arguments, "dry_run"); Authorize(client, "Change timing preferences", false, dryRun);
        var before = JsonSerializer.SerializeToElement(TimingSettingsSnapshot(), BridgeProtocol.JsonOptions);
        if (!dryRun)
        {
            if (edit.PhonemeTiming is { } pt) Settings.IntuitiveConsonantTiming = pt;
            if (edit.PhonemeVolume is { } pv) Settings.IndividualPhonemeVolume = pv;
            if (edit.Transitions is { } tr) Settings.IndividualTransitionControl = tr;
            if (edit.VoiceComp is { } vc) Settings.VoiceCompEnabled = vc;
            if (edit.Smoothing is { } sm) Settings.TransitionSmoothingEnabled = sm;
            if (edit.VolumeThresholdDb is { } threshold) Settings.TransitionSmoothingVolumeThresholdDb = threshold;
            if (edit.CrossfadeMs is { } crossfade) Settings.TransitionSmoothingCrossfadeMs = crossfade;
            if (edit.LoudnessWindowMs is { } loudness) Settings.TransitionSmoothingLoudnessWindowMs = loudness;
            if (edit.AnalysisRangeMs is { } analysis) Settings.TransitionSmoothingAnalysisRangeMs = analysis;
            if (edit.PhaseAlignment is { } phase) Settings.TransitionSmoothingPhaseAlignment = phase;
            if (edit.TimingOffsetMs is { } timing) Settings.TransitionSmoothingTimingOffsetMs = timing;
            PhonemeVolumeHost.RefreshSettings(); TransitionSmoothingHost.RefreshSettings(); VoiceCompHost.RefreshSettings();
            Patch.Patches.ShowOtherTracksNotesPatch.RequestRefreshPianoroll();
        }
        return new { project = TimingProject(), dry_run = dryRun, before, requested = edit,
            settings = TimingSettingsSnapshot(), history = "global preferences; no project undo entry" };
    }
}
