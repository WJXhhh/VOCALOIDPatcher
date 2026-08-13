using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

namespace VOCALOIDPatcher.McpServer;

[McpServerToolType]
public sealed class VocaloidTools
{
    private readonly BridgeGateway _gateway;

    public VocaloidTools(BridgeGateway gateway)
    {
        _gateway = gateway;
    }

    [McpServerTool(Name = "v6_session", Title = "VOCALOID Session", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List MCP-enabled VOCALOID instances or acquire/release the single-writer lease for one instance.")]
    public Task<McpBridgeResult> Session(
        McpServerInstance server,
        [Description("list, status, acquire_write, release_write, or revoke_write")] string action = "list",
        [Description("Required when multiple VOCALOID instances are running.")] string? instance_id = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new McpBridgeResult(
                true,
                JsonSerializer.SerializeToElement(new { instances = _gateway.ListInstances() })));
        }

        return _gateway.InvokeAsync(server, "v6_session", instance_id, new { action }, cancellationToken);
    }

    [McpServerTool(Name = "v6_get_state", Title = "Get VOCALOID State", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get editor, project, selection, playback, rendering, revision, permission, and capability state.")]
    public Task<McpBridgeResult> GetState(McpServerInstance server, string? instance_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_get_state", instance_id, new { }, cancellationToken);

    [McpServerTool(Name = "v6_get_catalog", Title = "Get VOCALOID Catalog", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List available voicebanks, controller types, languages, and project conversion formats.")]
    public Task<McpBridgeResult> GetCatalog(McpServerInstance server, string? instance_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_get_catalog", instance_id, new { }, cancellationToken);

    [McpServerTool(Name = "v6_query_project", Title = "Query VOCALOID Project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query project structure, tracks, parts, Audio Parts, notes, tempo, time signatures, parameters, or selection with revision-bound pagination.")]
    public Task<McpBridgeResult> QueryProject(
        McpServerInstance server,
        string kind = "summary",
        string? instance_id = null,
        int page_size = 200,
        string? page_token = null,
        JsonElement? filter = null,
        string[]? projection = null,
        long? changed_since_revision = null,
        string parameter_mode = "raw",
        int bucket_ticks = 480,
        int max_scanned_items = 25000,
        int max_response_bytes = 4194304,
        int dispatcher_budget_ms = 250,
        CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_query_project", instance_id, new
        {
            kind,
            page_size,
            page_token,
            filter,
            projection,
            changed_since_revision,
            parameter_mode,
            bucket_ticks,
            max_scanned_items,
            max_response_bytes,
            dispatcher_budget_ms,
        }, cancellationToken);

    [McpServerTool(Name = "v6_edit_structure", Title = "Edit VOCALOID Structure", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Atomically edit tracks, parts, singers, styles, tempo, and time signatures. One call produces one undo step.")]
    public Task<McpBridgeResult> EditStructure(McpServerInstance server, string project_id, long expected_revision, string client_request_id, JsonElement operations, string? instance_id = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => Mutation(server, "v6_edit_structure", instance_id, project_id, expected_revision, client_request_id, operations, dry_run, cancellationToken);

    [McpServerTool(Name = "v6_edit_notes", Title = "Edit VOCALOID Notes", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Atomically add, move, resize, copy, delete, or update notes, lyrics, phonemes, language, expression, and vibrato.")]
    public Task<McpBridgeResult> EditNotes(McpServerInstance server, string project_id, long expected_revision, string client_request_id, JsonElement operations, string? instance_id = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => Mutation(server, "v6_edit_notes", instance_id, project_id, expected_revision, client_request_id, operations, dry_run, cancellationToken);

    [McpServerTool(Name = "v6_g2pa_candidates", Title = "Query VOCALOID G2PA Candidates", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query candidate pronunciations for one note through VOCALOID's G2PA dispatch. Optionally target one language module and extension-dictionary mode.")]
    public Task<McpBridgeResult> G2paCandidates(
        McpServerInstance server,
        string project_id,
        long expected_revision,
        int track_index,
        int part_index,
        int note_index,
        string lyrics,
        string? instance_id = null,
        int? language_id = null,
        bool? use_extension_dictionary = null,
        CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_g2pa_candidates", instance_id, new
        {
            project_id,
            expected_revision,
            track_index,
            part_index,
            note_index,
            lyrics,
            language_id,
            use_extension_dictionary,
        }, cancellationToken);

    [McpServerTool(Name = "v6_g2pa_apply", Title = "Apply VOCALOID G2PA", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Apply lyrics, phonemes, candidate syllables, or a phoneme reset through VOCALOID's G2PA layer. One call produces one undo step.")]
    public Task<McpBridgeResult> ApplyG2pa(
        McpServerInstance server,
        string project_id,
        long expected_revision,
        string client_request_id,
        string action,
        int track_index,
        int part_index,
        int note_index,
        string? instance_id = null,
        string? lyrics = null,
        string? phonemes = null,
        int? language_id = null,
        JsonElement? syllables = null,
        int? end_note_index = null,
        bool reset_phonemes = true,
        bool dry_run = false,
        CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_g2pa_apply", instance_id, new
        {
            project_id,
            expected_revision,
            client_request_id,
            action,
            track_index,
            part_index,
            note_index,
            lyrics,
            phonemes,
            language_id,
            syllables,
            end_note_index,
            reset_phonemes,
            dry_run,
        }, cancellationToken);

    [McpServerTool(Name = "v6_edit_parameters", Title = "Edit VOCALOID Parameters", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Atomically edit singing controller points, direct pitch, track volume/pan, and master volume.")]
    public Task<McpBridgeResult> EditParameters(McpServerInstance server, string project_id, long expected_revision, string client_request_id, JsonElement operations, string? instance_id = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => Mutation(server, "v6_edit_parameters", instance_id, project_id, expected_revision, client_request_id, operations, dry_run, cancellationToken);

    [McpServerTool(Name = "v6_apply_operations", Title = "Apply VOCALOID Operations", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Validate and atomically apply mixed Structure, Notes, Parameters, and G2PA operations in one native transaction.")]
    public Task<McpBridgeResult> ApplyOperations(McpServerInstance server, string project_id, long expected_revision, string client_request_id, JsonElement operations, string? instance_id = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => Mutation(server, "v6_apply_operations", instance_id, project_id, expected_revision, client_request_id, operations, dry_run, cancellationToken);

    [McpServerTool(Name = "v6_wait_event", Title = "Wait for VOCALOID Event", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Wait without occupying the WPF Dispatcher for revision, document, selection, transport, rendering, job, or lease events.")]
    public Task<McpBridgeResult> WaitEvent(
        McpServerInstance server,
        long after_event_id = 0,
        string? instance_id = null,
        int timeout_ms = 30000,
        int limit = 100,
        string[]? types = null,
        CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_wait_event", instance_id, new { after_event_id, timeout_ms, limit, types }, cancellationToken);

    [McpServerTool(Name = "v6_wait_for", Title = "Wait for VOCALOID State", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Wait for a target project revision, render-idle boundary, or playback state without polling full editor state.")]
    public Task<McpBridgeResult> WaitFor(
        McpServerInstance server,
        string condition,
        string? instance_id = null,
        long after_event_id = 0,
        long? target_revision = null,
        bool? is_playing = null,
        int timeout_ms = 30000,
        CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_wait_for", instance_id, new { condition, after_event_id, target_revision, is_playing, timeout_ms }, cancellationToken);

    [McpServerTool(Name = "v6_select_view", Title = "Select and Navigate VOCALOID", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Set the active track/part, select parts or notes, and optionally navigate the editor view.")]
    public Task<McpBridgeResult> SelectView(McpServerInstance server, string project_id, long expected_revision, string client_request_id, JsonElement request, string? instance_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_select_view", instance_id, new { project_id, expected_revision, client_request_id, request }, cancellationToken);

    [McpServerTool(Name = "v6_transport", Title = "Control VOCALOID Transport", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Play, stop, seek, and configure the loop range.")]
    public Task<McpBridgeResult> Transport(McpServerInstance server, string action, string? instance_id = null, long? absolute_tick = null, JsonElement? position = null, long? loop_begin_tick = null, long? loop_end_tick = null, bool? loop_enabled = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_transport", instance_id, new { action, absolute_tick, position, loop_begin_tick, loop_end_tick, loop_enabled }, cancellationToken);

    [McpServerTool(Name = "v6_history", Title = "VOCALOID Undo and Redo", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query, undo, or redo the active project history.")]
    public Task<McpBridgeResult> History(McpServerInstance server, string action = "status", string? instance_id = null, string? project_id = null, long? expected_revision = null, string? client_request_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_history", instance_id, new { action, project_id, expected_revision, client_request_id }, cancellationToken);

    [McpServerTool(Name = "v6_run_job", Title = "Run VOCALOID Creative Job", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Run a short atomic lyric, quantize, swing, or harmony operation.")]
    public Task<McpBridgeResult> RunJob(McpServerInstance server, string project_id, long expected_revision, string client_request_id, string kind, JsonElement? options = null, string? instance_id = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_run_job", instance_id, new { project_id, expected_revision, client_request_id, kind, options, dry_run }, cancellationToken);

    [McpServerTool(Name = "v6_project_file", Title = "Manage VOCALOID Project File", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Manage native VOCALOID project lifecycle, revert, VPR/VSQX/PPSF/MIDI/tempo/audio import, recent projects, save, open, and export. File access is enforced by the V6-side allowlist.")]
    public Task<McpBridgeResult> ProjectFile(McpServerInstance server, string action, string? instance_id = null, string? project_id = null, long? expected_revision = null, string? client_request_id = null, string? path = null, JsonElement? options = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_project_file", instance_id, new { action, project_id, expected_revision, client_request_id, path, options, dry_run }, cancellationToken);

    [McpServerTool(Name = "v6_convert_project", Title = "Convert VOCALOID Project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Import or export a supported singing-project format through the existing LibreSVIP bridge.")]
    public Task<McpBridgeResult> ConvertProject(McpServerInstance server, string action, string format, string path, string? instance_id = null, string? project_id = null, long? expected_revision = null, string? client_request_id = null, JsonElement? options = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_convert_project", instance_id, new { action, format, path, project_id, expected_revision, client_request_id, options, dry_run }, cancellationToken);

    [McpServerTool(Name = "v6_mixdown", Title = "Mix Down VOCALOID Audio", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Start a master, selected-track, or part audio mixdown as a monitored job.")]
    public Task<McpBridgeResult> Mixdown(McpServerInstance server, string target, string path, string? instance_id = null, string? project_id = null, long? expected_revision = null, string? client_request_id = null, JsonElement? options = null, bool dry_run = false, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_mixdown", instance_id, new { target, path, project_id, expected_revision, client_request_id, options, dry_run }, cancellationToken);

    [McpServerTool(Name = "v6_job", Title = "Manage VOCALOID Long Job", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List, inspect, or cooperatively cancel a long-running VOCALOID operation.")]
    public Task<McpBridgeResult> ManageJob(McpServerInstance server, string action = "list", string? instance_id = null, string? job_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_job", instance_id, new { action, job_id }, cancellationToken);

    private Task<McpBridgeResult> Mutation(McpServerInstance server, string method, string? instanceId, string projectId, long expectedRevision, string requestId, JsonElement operations, bool dryRun, CancellationToken cancellationToken)
        => _gateway.InvokeAsync(server, method, instanceId, new
        {
            project_id = projectId,
            expected_revision = expectedRevision,
            client_request_id = requestId,
            operations,
            dry_run = dryRun,
        }, cancellationToken);
}
