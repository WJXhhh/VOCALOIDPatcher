using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.RegisterShift;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp.Domains.ExtensionParameters;

internal static class ExtensionParameterRegistry
{
    private static bool _initialized;
    private static int _suppressRevision;

    public static IReadOnlyList<ExtensionParameterDescriptor> Schema => ExtensionParameterContracts.Parameters;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        BreathVolumeService.Changed += OnBreathVolumeChanged;
        BreathVolumeService.RebuildCompleted += (part, generation, latest) => PublishRebuildCompleted("patcher.bvl", generation, latest);
        RegisterShiftService.ValuesChanged += OnRegisterShiftChanged;
        RegisterShiftService.RebuildCompleted += (part, generation, latest) => PublishRebuildCompleted("patcher.register_shift", generation, latest);
    }

    public static IReadOnlyList<CapabilityStatus> Capabilities()
    {
        Initialize();
        return new[]
        {
            Status(ExtensionParameterContracts.Parameters[0], Settings.IndividualBreathVolume,
                Settings.IndividualBreathVolume ? null : "Individual Breath Volume is disabled in Patcher settings."),
            Status(ExtensionParameterContracts.Parameters[1], Settings.RegisterShift && RegisterShiftService.IsSupported,
                !Settings.RegisterShift ? "Register Shift is disabled in Patcher settings."
                : !RegisterShiftService.IsSupported ? "The native register-shift hooks are unsupported by this editor build."
                : null),
        };
    }

    public static object Query(WIVSMSequence sequence, string projectId, long revision, JsonElement arguments)
    {
        Initialize();
        JsonElement filter = arguments.TryGetProperty("filter", out JsonElement value) ? value : default;
        string? requested = String(filter, "parameter_id");
        int onlyTrack = Int(filter, "track_index", -1);
        int onlyPart = Int(filter, "part_index", -1);
        int onlyNote = Int(filter, "note_index", -1);
        var items = new List<object>();
        for (int trackIndex = 0; trackIndex < sequence.Tracks.Count; trackIndex++)
        {
            if (onlyTrack >= 0 && onlyTrack != trackIndex) continue;
            WIVSMTrack track = sequence.Tracks[trackIndex];
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
            {
                if (onlyPart >= 0 && onlyPart != partIndex || track.Parts[partIndex] is not WIVSMMidiPart part) continue;
                for (int noteIndex = 0; noteIndex < part.Notes.Count; noteIndex++)
                {
                    if (onlyNote >= 0 && onlyNote != noteIndex) continue;
                    WIVSMNote note = part.Notes[noteIndex];
                    EntityRef reference = McpEntityRegistry.Reference(projectId, revision, "note", note, trackIndex, partIndex, noteIndex);
                    if (requested is null or "patcher.bvl")
                        items.Add(Item(reference, "patcher.bvl", BreathVolumeService.GetValue(note), part, cache: true));
                    if (requested is null or "patcher.register_shift")
                        items.Add(Item(reference, "patcher.register_shift", RegisterShiftService.GetValue(note.CppObjPtr), part, cache: false));
                }
            }
        }
        return new
        {
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            source = "patcher",
            items,
            total = items.Count,
        };
    }

    public static object Apply(
        WIVSMSequence sequence,
        string projectId,
        long previousRevision,
        IReadOnlyList<UnifiedOperationPlanner.PlannedOperation> plan,
        bool dryRun)
    {
        Initialize();
        if (plan.Any(item => item.Domain != ExtensionParameterContracts.DomainId))
            throw new InvalidOperationException("Patcher extension parameter operations cannot be mixed with native operations because they use different atomic history coordinators.");

        var resolved = new List<(UnifiedOperationPlanner.PlannedOperation Operation, WIVSMMidiPart Part, WIVSMNote Note, int Value)>();
        foreach (UnifiedOperationPlanner.PlannedOperation item in plan)
        {
            string error = ExtensionParameterContracts.Validate(item.Payload);
            if (error.Length != 0)
                throw new InvalidOperationException($"Operation {item.Index} ({item.OperationId}): {error}");
            int trackIndex = Int(item.Payload, "track_index", -1);
            int partIndex = Int(item.Payload, "part_index", -1);
            int noteIndex = Int(item.Payload, "note_index", -1);
            if (trackIndex >= sequence.Tracks.Count || partIndex >= sequence.Tracks[trackIndex].Parts.Count
                || sequence.Tracks[trackIndex].Parts[partIndex] is not WIVSMMidiPart part || noteIndex >= part.Notes.Count)
                throw new InvalidOperationException($"Operation {item.Index} references a missing MIDI note.");
            string parameterId = String(item.Payload, "parameter_id")!;
            ExtensionParameterDescriptor descriptor = Schema.First(entry => entry.Id == parameterId);
            int target = String(item.Payload, "op") == "clear" ? descriptor.DefaultValue : Int(item.Payload, "value", descriptor.DefaultValue);
            resolved.Add((item, part, part.Notes[noteIndex], target));
        }

        object[] results = resolved.Select(item => (object)new OperationResult(item.Operation.Index, item.Operation.OperationId,
            dryRun ? "validated" : String(item.Operation.Payload, "op") == "clear" ? "deleted" : "updated",
            null, item.Operation.ClientTag, item.Operation.TempId, new { dry_run = dryRun })).ToArray();
        if (!dryRun && resolved.Count > 0)
            ApplyAtomic(sequence, resolved);
        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        return new
        {
            dry_run = dryRun,
            valid = true,
            confirmation_required = false,
            counts = new { created = 0, updated = resolved.Count(item => String(item.Operation.Payload, "op") == "set"), deleted = resolved.Count(item => String(item.Operation.Payload, "op") == "clear") },
            operations = results,
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
        };
    }

    private static void ApplyAtomic(WIVSMSequence sequence, IReadOnlyList<(UnifiedOperationPlanner.PlannedOperation Operation, WIVSMMidiPart Part, WIVSMNote Note, int Value)> items)
    {
        var bvlBefore = new Dictionary<IntPtr, byte>();
        var bvlAfter = new Dictionary<IntPtr, byte>();
        var regBefore = new Dictionary<IntPtr, byte>();
        var regAfter = new Dictionary<IntPtr, byte>();
        var bvlParts = new HashSet<WIVSMMidiPart>();
        var regParts = new HashSet<WIVSMMidiPart>();
        foreach (var item in items)
        {
            IntPtr handle = item.Note.CppObjPtr;
            if (String(item.Operation.Payload, "parameter_id") == "patcher.bvl")
            {
                bvlBefore.TryAdd(handle, BreathVolumeService.GetValue(item.Note));
                bvlAfter[handle] = checked((byte)item.Value);
                bvlParts.Add(item.Part);
            }
            else
            {
                regBefore.TryAdd(handle, unchecked((byte)(RegisterShiftService.GetValue(handle) +
                    RegisterShiftService.DisplayOffset)));
                regAfter[handle] = unchecked((byte)(item.Value + RegisterShiftService.DisplayOffset));
                regParts.Add(item.Part);
            }
        }
        try
        {
            BreathVolumeService.SetPreviewValues(bvlAfter);
            RegisterShiftService.SetPreviewValues(regAfter);
            CustomParameterHistoryCoordinator.Push(sequence,
                new CompositeHistoryEdit(sequence, bvlBefore, bvlAfter, bvlParts, regBefore, regAfter, regParts));
            Complete(sequence, bvlParts, regParts);
        }
        catch
        {
            BreathVolumeService.SetPreviewValues(bvlBefore);
            RegisterShiftService.SetPreviewValues(regBefore);
            throw;
        }
    }

    private static void Complete(WIVSMSequence sequence, IReadOnlyCollection<WIVSMMidiPart> bvlParts, IReadOnlyCollection<WIVSMMidiPart> regParts)
    {
        _suppressRevision++;
        try
        {
            if (bvlParts.Count > 0) BreathVolumeService.CompleteExternalMutation(sequence, bvlParts);
            if (regParts.Count > 0) RegisterShiftService.CompleteExternalMutation(sequence, regParts);
        }
        finally
        {
            _suppressRevision--;
        }
        PublishChanged(bvlParts.Count > 0 && regParts.Count > 0 ? "patcher.batch" : bvlParts.Count > 0 ? "patcher.bvl" : "patcher.register_shift", null);
        (string projectId, long revision) = McpRevisionTracker.Current();
        McpEventHub.Publish(ExtensionParameterContracts.RebuildEvent, projectId, revision,
            new { bvl_parts = bvlParts.Count, register_shift_parts = regParts.Count, latest_generation_only = true });
    }

    private sealed class CompositeHistoryEdit(
        WIVSMSequence sequence,
        IReadOnlyDictionary<IntPtr, byte> bvlBefore,
        IReadOnlyDictionary<IntPtr, byte> bvlAfter,
        IReadOnlyCollection<WIVSMMidiPart> bvlParts,
        IReadOnlyDictionary<IntPtr, byte> regBefore,
        IReadOnlyDictionary<IntPtr, byte> regAfter,
        IReadOnlyCollection<WIVSMMidiPart> regParts) : ICustomParameterHistoryEdit
    {
        public void ApplyBefore() { BreathVolumeService.SetPreviewValues(bvlBefore); RegisterShiftService.SetPreviewValues(regBefore); }
        public void ApplyAfter() { BreathVolumeService.SetPreviewValues(bvlAfter); RegisterShiftService.SetPreviewValues(regAfter); }
        public void AfterApply() => Complete(sequence, bvlParts, regParts);
    }

    private static object Item(EntityRef reference, string id, int value, WIVSMMidiPart part, bool cache)
        => new
        {
            reference,
            parameter_id = id,
            source = "patcher",
            value,
            is_default = value == Schema.First(item => item.Id == id).DefaultValue,
            state = cache ? BreathVolumeService.GetRegionStatus(part).ToString().ToLowerInvariant() :
                RegisterShiftService.NativeStatusForPart(part).ToString().ToLowerInvariant(),
        };

    private static CapabilityStatus Status(ExtensionParameterDescriptor descriptor, bool available, string? reason)
        => new(descriptor.CapabilityId, true, false, descriptor.MinimumEditorVersion, reason,
            available ? "available" : "temporarily_unavailable");

    private static void OnBreathVolumeChanged(BreathVolumeChangeKind kind, WIVSMMidiPart? part)
    {
        if (kind != BreathVolumeChangeKind.Values || _suppressRevision != 0) return;
        PublishChanged("patcher.bvl", part);
    }

    private static void OnRegisterShiftChanged()
    {
        if (_suppressRevision == 0) PublishChanged("patcher.register_shift", null);
    }

    private static void PublishChanged(string parameterId, WIVSMMidiPart? part)
    {
        long revision = McpRevisionTracker.Changed();
        (string projectId, _) = McpRevisionTracker.Current();
        McpEventHub.Publish(ExtensionParameterContracts.ChangedEvent, projectId, revision,
            new { parameter_id = parameterId, source = "patcher", part_present = part != null });
    }

    private static void PublishRebuildCompleted(string parameterId, int generation, bool latest)
    {
        if (!latest) return;
        // Completion runs on a worker thread. Do not touch Yamaha/App objects here; callers
        // correlate this safe generation event with the revision from the preceding change.
        McpEventHub.Publish(ExtensionParameterContracts.RebuildCompletedEvent, data:
            new { parameter_id = parameterId, source = "patcher", generation, latest_generation = true });
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.ToLowerInvariant() : null;

    private static int Int(JsonElement element, string name, int fallback)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
}
