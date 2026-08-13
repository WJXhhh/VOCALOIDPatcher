using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.Mcp.Domains.MixerEffects;
using VOCALOIDPatcher.Mcp.Domains.AudioParts;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    static VocaloidMcpFacade()
    {
        MixerEffectsDomain.Register();
        AudioPartDomain.Register();
    }

    private sealed class McpFaultException : Exception
    {
        public string Code { get; }
        public bool Retryable { get; }
        public object? Details { get; }

        public McpFaultException(string code, string message, bool retryable = false, object? details = null)
            : base(message)
        {
            Code = code;
            Retryable = retryable;
            Details = details;
        }
    }

    private static readonly BoundedIdempotencyCache<BridgeResponse> IdempotencyCache = new(2048);

    public static BridgeResponse Handle(BridgeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            McpAccessController.Observe(request.Client);
            JsonElement arguments = request.Arguments ?? JsonSerializer.SerializeToElement(new { });
            bool mutation = IsMutation(request.Method, arguments);
            string? clientRequestId = String(arguments, "client_request_id");
            string? cacheKey = mutation && !string.IsNullOrWhiteSpace(clientRequestId)
                ? request.Client.Id + ":" + clientRequestId
                : null;
            if (cacheKey != null && IdempotencyCache.TryGet(cacheKey, out BridgeResponse? cached) && cached != null)
                return cached with { RequestId = request.RequestId };

            object? result = request.Method switch
            {
                "v6_session" => Session(request.Client, arguments),
                "v6_get_state" => GetState(),
                "v6_get_catalog" => GetCatalog(),
                "v6_query_project" => QueryProject(arguments),
                "v6_selection_resource" => QuerySelection(),
                "v6_edit_structure" => EditStructure(request.Client, arguments),
                "v6_edit_notes" => EditNotes(request.Client, arguments),
                "v6_g2pa_candidates" => G2paCandidates(arguments),
                "v6_g2pa_apply" => ApplyG2pa(request.Client, arguments),
                "v6_edit_parameters" => EditParameters(request.Client, arguments),
                "v6_apply_operations" => ApplyOperations(request.Client, arguments),
                "v6_select_view" => SelectView(request.Client, arguments),
                "v6_transport" => Transport(arguments),
                "v6_history" => History(request.Client, arguments),
                "v6_run_job" => RunCreativeJob(request.Client, arguments),
                "v6_project_file" => ProjectFile(request.Client, arguments),
                "v6_convert_project" => ConvertProject(request.Client, arguments),
                "v6_mixdown" => Mixdown(request.Client, arguments),
                "v6_job" => ManageJob(request.Client, arguments),
                _ => throw Fault(McpErrorCodes.Unsupported, $"Unknown bridge method '{request.Method}'."),
            };

            BridgeResponse response = BridgeResponse.Success(request.RequestId, result);
            if (cacheKey != null)
                IdempotencyCache.Store(cacheKey, response);
            return response;
        }
        catch (McpFaultException exception)
        {
            return BridgeResponse.Failure(
                request.RequestId,
                exception.Code,
                exception.Message,
                exception.Retryable,
                exception.Details);
        }
        catch (OperationCanceledException)
        {
            return BridgeResponse.Failure(request.RequestId, "cancelled", "The operation was cancelled.");
        }
        catch (Exception exception) when (exception is MissingMethodException or MissingMemberException or TypeLoadException or EntryPointNotFoundException)
        {
            return BridgeResponse.Failure(
                request.RequestId,
                "unsupported",
                "This operation is not supported by the installed VOCALOID version.",
                details: new { reason = exception.Message });
        }
        catch (Exception exception)
        {
            return BridgeResponse.Failure(request.RequestId, "internal_error", exception.Message);
        }
    }

    private static object Session(BridgeClientInfo client, JsonElement arguments)
    {
        string action = String(arguments, "action") ?? "status";
        return action.ToLowerInvariant() switch
        {
            "status" => McpAccessController.GetStatus(),
            "acquire_write" => AcquireWrite(client),
            "release_write" => new { released = McpAccessController.Release(client), access = McpAccessController.GetStatus() },
            "revoke_write" => RevokeWrite(client),
            _ => throw Fault("invalid_request", "Session action must be status, acquire_write, release_write, or revoke_write."),
        };
    }

    private static object AcquireWrite(BridgeClientInfo client)
    {
        if (!McpAccessController.TryAcquire(client, out BridgeError? error))
            throw Fault(error!);
        return new { acquired = true, access = McpAccessController.GetStatus() };
    }

    private static object RevokeWrite(BridgeClientInfo client)
    {
        if (!McpAccessController.AuthorizeWrite(client, "Revoke the current MCP write lease", true, out BridgeError? error))
            throw Fault(error!);
        McpAccessController.RevokeAll();
        return new { revoked = true, access = McpAccessController.GetStatus() };
    }

    private static bool IsMutation(string method, JsonElement arguments)
        => method is "v6_edit_structure" or "v6_edit_notes" or "v6_g2pa_apply" or "v6_edit_parameters" or "v6_apply_operations" or "v6_select_view" or "v6_run_job"
           || method == "v6_history" && !string.Equals(String(arguments, "action"), "status", StringComparison.OrdinalIgnoreCase)
           || method == "v6_project_file" && !string.Equals(String(arguments, "action"), "recent", StringComparison.OrdinalIgnoreCase)
           || method is "v6_convert_project" or "v6_mixdown";

    private static (Yamaha.VOCALOID.Sequence Sequence, WIVSMSequence Vsm) Context()
    {
        var sequence = App.Shared?.Document?.Sequence;
        var vsm = sequence?.VSMSequence;
        if (sequence == null || vsm == null)
            throw Fault("v6_unavailable", "No VOCALOID project is open.", true);
        return (sequence, vsm);
    }

    private static (string ProjectId, long Revision) ValidateProject(JsonElement arguments)
    {
        (string projectId, long revision) = McpRevisionTracker.Current();
        string suppliedProject = String(arguments, "project_id")
                                 ?? throw Fault("invalid_request", "project_id is required.");
        long suppliedRevision = Long(arguments, "expected_revision")
                                ?? throw Fault("invalid_request", "expected_revision is required.");
        BridgeError? error = ProjectRevisionGuard.Validate(projectId, revision, suppliedProject, suppliedRevision);
        if (error != null)
            throw Fault(error);
        return (projectId, revision);
    }

    private static bool Authorize(
        BridgeClientInfo client,
        string action,
        bool dangerous,
        bool dryRun)
    {
        if (dryRun)
            return false;
        if (!McpAccessController.AuthorizeWrite(client, action, dangerous, out BridgeError? error))
            throw Fault(error!);
        return true;
    }

    private static JsonElement Operations(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("operations", out JsonElement operations)
            || operations.ValueKind != JsonValueKind.Array)
            throw Fault("invalid_request", "operations must be a JSON array.");
        if (operations.GetArrayLength() > 1000)
            throw Fault("invalid_request", "A request may contain at most 1000 operations.");
        return operations;
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Long(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.TryGetInt64(out long result)
            ? result
            : null;

    private static int Int(JsonElement element, string name, int defaultValue = 0)
    {
        if (Long(element, name) is { } direct)
            return checked((int)direct);
        if (defaultValue == -1 && name is "track_index" or "part_index" or "note_index")
        {
            string? entityId = String(element, name switch
            {
                "track_index" => "track_entity_id",
                "part_index" => "part_entity_id",
                _ => "note_entity_id",
            }) ?? String(element, "entity_id");
            if (entityId != null)
            {
                (_, WIVSMSequence vsm) = Context();
                (string projectId, _) = McpRevisionTracker.Current();
                var resolved = McpEntityRegistry.Resolve(vsm, projectId, entityId)
                               ?? throw Fault(McpErrorCodes.InvalidReference, $"Entity '{entityId}' is no longer present in this project.");
                return name switch
                {
                    "track_index" => resolved.TrackIndex,
                    "part_index" => resolved.PartIndex,
                    _ => resolved.ItemIndex,
                };
            }
        }
        return defaultValue;
    }

    private static bool Bool(JsonElement element, string name, bool defaultValue = false)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static double Double(JsonElement element, string name, double defaultValue = 0)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.TryGetDouble(out double result)
            ? result
            : defaultValue;

    private static JsonElement? Element(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : null;

    private static McpFaultException Fault(string code, string message, bool retryable = false, object? details = null)
        => new(code, message, retryable, details);

    private static McpFaultException Fault(BridgeError error)
        => new(error.Code, error.Message, error.Retryable, error.Details);

    private static WIVSMTrack Track(WIVSMSequence vsm, int index)
    {
        if (index < 0 || index >= vsm.Tracks.Count)
            throw Fault("invalid_reference", $"Track index {index} is out of range.");
        return vsm.Tracks[index];
    }

    private static WIVSMPart Part(WIVSMSequence vsm, int trackIndex, int partIndex)
    {
        WIVSMTrack track = Track(vsm, trackIndex);
        if (partIndex < 0 || partIndex >= track.Parts.Count)
            throw Fault("invalid_reference", $"Part index {partIndex} is out of range.");
        return track.Parts[partIndex];
    }

    private static WIVSMMidiPart MidiPart(WIVSMSequence vsm, int trackIndex, int partIndex)
    {
        WIVSMPart part = Part(vsm, trackIndex, partIndex);
        if (part is not WIVSMMidiPart midiPart)
            throw Fault("invalid_reference", "The referenced part is not a MIDI part.");
        return midiPart;
    }

    private static WIVSMNote Note(WIVSMSequence vsm, int trackIndex, int partIndex, int noteIndex)
    {
        WIVSMMidiPart part = MidiPart(vsm, trackIndex, partIndex);
        if (noteIndex < 0 || noteIndex >= part.Notes.Count)
            throw Fault("invalid_reference", $"Note index {noteIndex} is out of range.");
        return part.Notes[noteIndex];
    }

    private static EntityRef Ref(
        string projectId,
        long revision,
        string kind,
        object entity,
        int trackIndex = -1,
        int partIndex = -1,
        int itemIndex = -1,
        string? clientTag = null)
        => McpEntityRegistry.Reference(projectId, revision, kind, entity, trackIndex, partIndex, itemIndex, clientTag);

    private static long ResolveAbsoluteTick(WIVSMSequence vsm, JsonElement element, long defaultValue = 0)
    {
        if (Long(element, "absolute_tick") is { } direct)
            return direct;
        JsonElement position = Element(element, "position") ?? default;
        if (position.ValueKind != JsonValueKind.Object)
            return defaultValue;
        if (Long(position, "absolute_tick") is { } absolute)
            return absolute;
        if (Long(position, "bar") is not { } barValue)
            return defaultValue;

        int bar = checked((int)barValue) - 1;
        int beat = checked((int)(Long(position, "beat") ?? 1)) - 1;
        int clock = checked((int)(Long(position, "tick") ?? 0));
        if (bar < 0 || beat < 0 || clock < 0)
            throw Fault("invalid_request", "bar and beat are 1-based and tick cannot be negative.");
        WIVSMTimeSig? signature = vsm.TimeSigs.LastOrDefault(item => item.PosBar <= bar);
        int denominator = signature?.Denom ?? 4;
        int numerator = signature?.Numer ?? 4;
        if (beat >= numerator)
            throw Fault("invalid_request", "beat is outside the time signature at the requested bar.");
        long beatTicks = Yamaha.VOCALOID.Design.Sequence.resolution * 4L / denominator;
        return vsm.GetTickFromBar(bar).Value + beat * beatTicks + clock;
    }

    private static long ResolvePartRelativeTick(
        WIVSMSequence vsm,
        WIVSMMidiPart part,
        JsonElement element,
        long defaultValue = 0)
    {
        if (Long(element, "part_relative_tick") is { } direct)
            return direct;
        JsonElement position = Element(element, "position") ?? default;
        if (position.ValueKind == JsonValueKind.Object
            && Long(position, "part_relative_tick") is { } relative)
            return relative;
        long absolute = ResolveAbsoluteTick(vsm, element, long.MinValue);
        return absolute == long.MinValue ? defaultValue : absolute - part.AbsPosTick.Value;
    }
}
