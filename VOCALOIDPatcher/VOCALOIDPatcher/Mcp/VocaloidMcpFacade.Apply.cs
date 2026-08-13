using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.Mcp.Domains.ExtensionParameters;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private static object ApplyOperations(BridgeClientInfo client, JsonElement arguments)
    {
        (string projectId, long previousRevision) = ValidateProject(arguments);
        JsonElement operations = Operations(arguments);
        bool dryRun = Bool(arguments, "dry_run");
        (_, WIVSMSequence vsm) = Context();
        IReadOnlyList<UnifiedOperationPlanner.PlannedOperation> plan;
        try
        {
            plan = new UnifiedOperationPlanner(vsm, projectId, previousRevision).Plan(operations);
        }
        catch (UnifiedOperationPlanningException exception)
        {
            throw Fault(McpErrorCodes.InvalidRequest, exception.Message, details: new OperationFailure(exception.OperationIndex, exception.OperationId, exception.Field, McpErrorCodes.InvalidRequest, exception.Message, false, false));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            throw Fault(McpErrorCodes.InvalidRequest, exception.Message, details: new OperationFailure(0, "plan", null, McpErrorCodes.InvalidRequest, exception.Message, false, false));
        }

        bool hasNonHistoryMixerState = plan.Any(IsNonHistoryMixerState);
        if (hasNonHistoryMixerState && plan.Any(item => !IsNonHistoryMixerState(item) || HasHistoryBackedMixerValue(item)))
            throw Fault(McpErrorCodes.InvalidRequest, "Mute/solo operations use V6's non-history state contract and must be submitted separately from undoable project operations.");

        bool dangerous = plan.Any(item => item.OperationId is "structure.delete_track" or "structure.delete_part" or "audio_parts.delete" or "audio_parts.replace_source")
                         || plan.Count(item => item.OperationId == "notes.delete") > 32;
        Authorize(client, $"Apply mixed project operations ({plan.Count} operations)", dangerous, dryRun);

        if (plan.Any(item => item.Domain == ExtensionParameterContracts.DomainId))
        {
            try
            {
                return ExtensionParameterRegistry.Apply(vsm, projectId, previousRevision, plan, dryRun);
            }
            catch (InvalidOperationException exception)
            {
                throw Fault(McpErrorCodes.InvalidRequest, exception.Message, details: new OperationFailure(0, "extension_parameters", null, McpErrorCodes.InvalidRequest, exception.Message, false, false));
            }
        }

        var planner = new UnifiedOperationPlanner(vsm, projectId, previousRevision);
        // Re-plan with a planner retained for actual temp-id updates.
        plan = planner.Plan(operations);
        var results = new List<OperationResult>(plan.Count);
        int currentIndex = -1;
        try
        {
            if (dryRun)
            {
                foreach (UnifiedOperationPlanner.PlannedOperation item in plan)
                {
                    currentIndex = item.Index;
                    UnifiedOperationPlanner.PlannedOperation resolvedItem = planner.Resolve(item);
                    ValidateUnifiedOperation(vsm, resolvedItem);
                    results.Add(ResultFor(resolvedItem, null, true));
                }
            }
            else
            {
                if (hasNonHistoryMixerState)
                {
                    foreach (UnifiedOperationPlanner.PlannedOperation item in plan)
                    {
                        currentIndex = item.Index;
                        UnifiedOperationPlanner.PlannedOperation resolvedItem = planner.Resolve(item);
                        ExecuteUnifiedOperation(vsm, projectId, previousRevision, resolvedItem);
                        results.Add(ResultFor(resolvedItem, null, false));
                    }
                }
                else
                {
                    using var transaction = new Transaction(vsm) { Result = false };
                    foreach (UnifiedOperationPlanner.PlannedOperation item in plan)
                    {
                        currentIndex = item.Index;
                        UnifiedOperationPlanner.PlannedOperation resolvedItem = planner.Resolve(item);
                        EntityRef? created = ExecuteUnifiedOperation(vsm, projectId, previousRevision, resolvedItem);
                        if (item.TempId != null && created != null)
                            planner.UpdateTemporary(item.TempId, created);
                        results.Add(ResultFor(resolvedItem, created, false));
                    }
                    transaction.Result = true;
                }
            }
        }
        catch (McpFaultException exception)
        {
            string operationId = currentIndex >= 0 && currentIndex < plan.Count ? plan[currentIndex].OperationId : "plan";
            throw Fault(exception.Code, exception.Message, exception.Retryable, new OperationFailure(currentIndex, operationId, InferOperationField(exception.Message), exception.Code, exception.Message, !dryRun, exception.Retryable));
        }
        catch (Exception exception)
        {
            string operationId = currentIndex >= 0 && currentIndex < plan.Count ? plan[currentIndex].OperationId : "plan";
            throw Fault(McpErrorCodes.OperationFailed, exception.Message, details: new OperationFailure(currentIndex, operationId, InferOperationField(exception.Message), McpErrorCodes.OperationFailed, exception.Message, !dryRun, false));
        }

        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        if (!dryRun)
        {
            for (int index = 0; index < results.Count; index++)
            {
                OperationResult item = results[index];
                if (item.Reference != null)
                    results[index] = item with { Reference = item.Reference with { Revision = revision } };
            }
        }
        if (!dryRun)
            RefreshEditor();
        return new
        {
            dry_run = dryRun,
            valid = true,
            confirmation_required = dangerous,
            counts = new
            {
                created = plan.Count(item => item.Creates),
                updated = plan.Count(item => !item.Creates && !item.Deletes),
                deleted = plan.Count(item => item.Deletes),
            },
            operations = results,
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
        };
    }

    private static bool IsNonHistoryMixerState(UnifiedOperationPlanner.PlannedOperation item)
        => item.Domain == "mixer_effects"
           && item.OperationId == "mixer_effects.set_track_static"
           && (item.Payload.TryGetProperty("mute", out _) || item.Payload.TryGetProperty("solo", out _));

    private static bool HasHistoryBackedMixerValue(UnifiedOperationPlanner.PlannedOperation item)
        => item.Payload.TryGetProperty("volume", out _) || item.Payload.TryGetProperty("pan", out _);

    private static void ValidateUnifiedOperation(WIVSMSequence vsm, UnifiedOperationPlanner.PlannedOperation item)
    {
        if (Bool(item.Payload, "_mcp_virtual_track") || Bool(item.Payload, "_mcp_virtual_part") || Bool(item.Payload, "_mcp_virtual_note"))
        {
            ValidateVirtualOperation(item);
            return;
        }
        switch (item.Domain)
        {
            case "structure": ApplyStructureOperation(vsm, item.Payload, false); break;
            case "notes": ApplyNoteOperation(vsm, item.Payload, false); break;
            case "parameters": ApplyParameterOperation(vsm, item.Payload, false); break;
            case "g2pa":
            {
                int track = Int(item.Payload, "track_index", -1);
                int partIndex = Int(item.Payload, "part_index", -1);
                WIVSMMidiPart part = MidiPart(vsm, track, partIndex);
                Note(vsm, track, partIndex, Int(item.Payload, "note_index", -1));
                ValidateG2paApply(String(item.Payload, "action") ?? string.Empty, item.Payload, part);
                break;
            }
            default: ApplyRegisteredDomain(vsm, item, false); break;
        }
    }

    private static void ValidateVirtualOperation(UnifiedOperationPlanner.PlannedOperation item)
    {
        if (item.OperationId == "structure.add_part")
        {
            long duration = Long(item.Payload, "duration_tick") ?? 1920;
            long tick = Long(item.Payload, "absolute_tick") ?? 0;
            if (duration <= 0 || tick < 0)
                throw Fault(McpErrorCodes.InvalidRequest, "Part position and duration are invalid.");
            return;
        }
        if (item.OperationId == "notes.add")
        {
            long duration = Long(item.Payload, "duration_tick") ?? 480;
            long tick = Long(item.Payload, "part_relative_tick") ?? 0;
            int number = Int(item.Payload, "note_number", 60);
            int velocity = Int(item.Payload, "velocity", 64);
            if (duration <= 0 || tick < 0 || number is < 0 or > 127 || velocity is < 0 or > 127)
                throw Fault(McpErrorCodes.InvalidRequest, "Note position, duration, number, or velocity is invalid.");
            return;
        }
        if (item.Domain == "parameters")
        {
            if (Element(item.Payload, "parameter_type") == null)
                throw Fault(McpErrorCodes.InvalidRequest, "parameter_type is required.");
            if (Element(item.Payload, "value") == null)
                throw Fault(McpErrorCodes.InvalidRequest, "value is required.");
            return;
        }
        if (item.Domain == "g2pa")
        {
            string action = String(item.Payload, "action") ?? string.Empty;
            if (action == "set_lyrics")
                RequiredG2paText(item.Payload, "lyrics", MaximumG2paLyricLength);
            else if (action == "set_phonemes")
                RequiredG2paText(item.Payload, "phonemes", MaximumG2paTextLength);
            else if (action != "reset")
                throw Fault(McpErrorCodes.Unsupported, $"G2PA action '{action}' is not composable in v6_apply_operations.");
            return;
        }
        throw Fault(McpErrorCodes.InvalidRequest, $"Operation '{item.OperationId}' cannot target a temporary entity.");
    }

    private static EntityRef? ExecuteUnifiedOperation(WIVSMSequence vsm, string projectId, long revision, UnifiedOperationPlanner.PlannedOperation item)
    {
        HashSet<object>? before = item.Creates ? SnapshotEntities(vsm, item) : null;
        switch (item.Domain)
        {
            case "structure": ApplyStructureOperation(vsm, item.Payload, true); break;
            case "notes": ApplyNoteOperation(vsm, item.Payload, true); break;
            case "parameters": ApplyParameterOperation(vsm, item.Payload, true); break;
            case "g2pa": ExecuteG2paInline(vsm, item.Payload); break;
            default: ApplyRegisteredDomain(vsm, item, true); break;
        }
        return item.Creates ? FindCreatedEntity(vsm, projectId, revision, item, before!) : null;
    }

    private static void ApplyRegisteredDomain(WIVSMSequence vsm, UnifiedOperationPlanner.PlannedOperation item, bool execute)
    {
        try
        {
            if (!McpDomainRegistry.TryApply(item.Domain, vsm, item.Payload, execute))
                throw Fault(McpErrorCodes.Unsupported, $"Unsupported operation domain '{item.Domain}'.");
        }
        catch (NotSupportedException exception)
        {
            throw Fault(McpErrorCodes.Unsupported, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw Fault(McpErrorCodes.InvalidRequest, exception.Message);
        }
    }

    private static void ExecuteG2paInline(WIVSMSequence vsm, JsonElement operation)
    {
        int track = Int(operation, "track_index", -1);
        int part = Int(operation, "part_index", -1);
        WIVSMNote note = Note(vsm, track, part, Int(operation, "note_index", -1));
        string action = String(operation, "action") ?? string.Empty;
        bool success = action switch
        {
            "set_lyrics" => Element(operation, "language_id") == null
                ? note.SetLyricsAndResetPhonemes(RequiredG2paText(operation, "lyrics", MaximumG2paLyricLength))
                : note.SetLyricsAndResetPhonemes(RequiredG2paText(operation, "lyrics", MaximumG2paLyricLength), G2paLanguage(operation, "language_id")),
            "set_phonemes" => note.SetPhonemes(RequiredG2paText(operation, "phonemes", MaximumG2paTextLength)),
            "reset" => note.ResetPhonemes(Int(operation, "end_note_index", -1) is var end && end >= 0 ? Note(vsm, track, part, end) : null),
            _ => throw Fault(McpErrorCodes.Unsupported, $"G2PA action '{action}' is not composable in v6_apply_operations."),
        };
        if (!success)
            throw Fault(McpErrorCodes.OperationFailed, $"VOCALOID rejected the G2PA operation '{action}'.");
    }

    private static HashSet<object> SnapshotEntities(WIVSMSequence vsm, UnifiedOperationPlanner.PlannedOperation item)
    {
        if (item.OperationId == "structure.add_track")
            return vsm.Tracks.Cast<object>().ToHashSet(ReferenceEqualityComparer.Instance);
        int track = Int(item.Payload, "track_index", -1);
        if (item.OperationId is "structure.add_part" or "structure.duplicate_part")
            return Track(vsm, track).Parts.Cast<object>().ToHashSet(ReferenceEqualityComparer.Instance);
        if (item.Domain == "notes")
            return MidiPart(vsm, track, Int(item.Payload, "part_index", -1)).Notes.Cast<object>().ToHashSet(ReferenceEqualityComparer.Instance);
        return new HashSet<object>(ReferenceEqualityComparer.Instance);
    }

    private static EntityRef? FindCreatedEntity(WIVSMSequence vsm, string projectId, long revision, UnifiedOperationPlanner.PlannedOperation item, HashSet<object> before)
    {
        if (item.OperationId == "structure.add_track")
        {
            for (int index = 0; index < vsm.Tracks.Count; index++)
                if (!before.Contains(vsm.Tracks[index]))
                    return Ref(projectId, revision, "track", vsm.Tracks[index], index, clientTag: item.ClientTag);
        }
        int track = Int(item.Payload, "track_index", -1);
        if (item.OperationId is "structure.add_part" or "structure.duplicate_part")
        {
            WIVSMTrack owner = Track(vsm, track);
            for (int part = 0; part < owner.Parts.Count; part++)
                if (!before.Contains(owner.Parts[part]))
                    return Ref(projectId, revision, "part", owner.Parts[part], track, part, clientTag: item.ClientTag);
        }
        if (item.Domain == "notes")
        {
            int partIndex = Int(item.Payload, "part_index", -1);
            WIVSMMidiPart owner = MidiPart(vsm, track, partIndex);
            for (int note = 0; note < owner.Notes.Count; note++)
                if (!before.Contains(owner.Notes[note]))
                    return Ref(projectId, revision, "note", owner.Notes[note], track, partIndex, note, item.ClientTag);
        }
        return null;
    }

    private static OperationResult ResultFor(UnifiedOperationPlanner.PlannedOperation item, EntityRef? reference, bool dryRun)
        => new(item.Index, item.OperationId, dryRun ? "validated" : item.Creates ? "created" : item.Deletes ? "deleted" : "updated", reference, item.ClientTag, item.TempId,
            new { dry_run = dryRun });

    private static string? InferOperationField(string message)
    {
        string value = message.ToLowerInvariant();
        if (value.Contains("note number", StringComparison.Ordinal)) return "note_number";
        if (value.Contains("duration", StringComparison.Ordinal)) return "duration_tick";
        if (value.Contains("phoneme", StringComparison.Ordinal)) return "phonemes";
        if (value.Contains("lyric", StringComparison.Ordinal)) return "lyrics";
        if (value.Contains("track index", StringComparison.Ordinal)) return "track_index";
        if (value.Contains("part index", StringComparison.Ordinal)) return "part_index";
        if (value.Contains("parameter type", StringComparison.Ordinal)) return "parameter_type";
        return null;
    }
}
