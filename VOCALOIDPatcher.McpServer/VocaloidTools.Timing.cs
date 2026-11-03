using System.ComponentModel;
using ModelContextProtocol.Server;
using VOCALOIDPatcher.McpBridge;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

namespace VOCALOIDPatcher.McpServer;

public sealed partial class VocaloidTools
{
    [McpServerTool(Name = "v6_query_timing", Title = "Query VOCALOID Timing", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read all timing controls. kind: status/settings/note/phonemes/transitions/range/comp. Native planning returns a v6_job handle; inspect its result with v6_job. No render or history entry is created. All frames are Part native coordinates at 44100 Hz / 256 samples; positive offsets move right. Transitions require a complete sampled Part of at most 33 notes. Re-query after any edit; transition IDs are bound to layout_fingerprint.")]
    public Task<McpBridgeResult> QueryTiming(McpServerInstance server, string project_id, long expected_revision,
        string kind = "status", int track_index = -1, int part_index = -1, int note_index = -1,
        string? transition_id = null, string? layout_fingerprint = null, string? endpoint = null,
        int offset = 0, int limit = 100, string? instance_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_query_timing", instance_id, new { project_id, expected_revision, kind,
            track_index, part_index, note_index, transition_id, layout_fingerprint, endpoint, offset, limit }, cancellationToken);

    [McpServerTool(Name = "v6_edit_timing", Title = "Edit VOCALOID Timing", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Edit every supported timing control through the editor's timing model: shared phoneme boundaries, note coefficient, phoneme volume, transition endpoints/gain/progress/coverage/legacy onset, resets, and Comp source recipes/isolation. One edit is one undo step; shared physical boundaries move together. Native validation returns a v6_job handle. dry_run certifies the candidate without writing or rendering. Retired duration sample selection is not executable. Requires the write lease except for dry_run.")]
    public Task<McpBridgeResult> EditTiming(McpServerInstance server, string project_id, long expected_revision,
        string client_request_id, int track_index, int part_index, TimingEdit edit,
        bool dry_run = false, string? instance_id = null, CancellationToken cancellationToken = default)
    {
        try { edit.Validate(); }
        catch (ArgumentException e) { return Task.FromResult(McpBridgeResult.Failure("invalid_request", e.Message)); }
        return _gateway.InvokeAsync(server, "v6_edit_timing", instance_id, new { project_id, expected_revision,
            client_request_id, track_index, part_index, edit, dry_run }, cancellationToken);
    }

    [McpServerTool(Name = "v6_timing_settings", Title = "Configure VOCALOID Timing", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Partially update timing/phoneme-volume/transition/Comp switches and all transition smoothing settings. Global Patcher preferences, outside project undo; query current values with v6_query_timing kind=settings. dry_run validates without changing preferences. Requires the write lease except for dry_run.")]
    public Task<McpBridgeResult> TimingSettings(McpServerInstance server, string project_id, long expected_revision,
        string client_request_id, TimingSettingsEdit settings, bool dry_run = false, string? instance_id = null,
        CancellationToken cancellationToken = default)
    {
        try { settings.Validate(); }
        catch (ArgumentException e) { return Task.FromResult(McpBridgeResult.Failure("invalid_request", e.Message)); }
        return _gateway.InvokeAsync(server, "v6_timing_settings", instance_id, new { project_id, expected_revision,
            client_request_id, settings, dry_run }, cancellationToken);
    }
}
