using System.ComponentModel;
using ModelContextProtocol.Server;
using VOCALOIDPatcher.McpBridge;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

namespace VOCALOIDPatcher.McpServer;

public sealed partial class VocaloidTools
{
    [McpServerTool(Name = "v6_query_voice_comp", Title = "Query VOCALOID Voice Comp", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query single-track long-slice / consonant-vowel splice / independent-sustain recipes, bypass, render state, output boundaries, source durations, phonemes and source waveform peaks. Does not render. Explicit note_indices (max 128) or offset/limit (max 100); include_sources returns at most 128 waveform peaks per cached source. Samples include Part presend.")]
    public Task<McpBridgeResult> QueryVoiceComp(McpServerInstance server, string project_id, long expected_revision,
        int track_index, int part_index, int[]? note_indices = null, bool include_sources = false,
        int offset = 0, int limit = 50, string? instance_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_query_voice_comp", instance_id, new { project_id, expected_revision, track_index,
            part_index, note_indices, include_sources, offset, limit }, cancellationToken);

    [McpServerTool(Name = "v6_edit_voice_comp", Title = "Edit VOCALOID Voice Comp", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Apply the three single-track split-render actions, replace full recipes or source recipes, capture a same-bank donor note's pronunciation/EVEC/expression snapshot, bypass, or reset. Preserves melody/position/duration. Explicit batch targets share one atomic undo step. dry_run validates every target without writing/rendering. Composition rebuild is asynchronous; query render state before preview/export. copy_source stores a snapshot, never a live dependency.")]
    public Task<McpBridgeResult> EditVoiceComp(McpServerInstance server, string project_id, long expected_revision,
        string client_request_id, int track_index, int part_index, VoiceCompEdit edit, bool dry_run = false,
        string? instance_id = null, CancellationToken cancellationToken = default)
    {
        try { edit.Validate(); }
        catch (ArgumentException e) { return Task.FromResult(McpBridgeResult.Failure("invalid_request", e.Message)); }
        return _gateway.InvokeAsync(server, "v6_edit_voice_comp", instance_id, new { project_id, expected_revision,
            client_request_id, track_index, part_index, edit, dry_run }, cancellationToken);
    }

    [McpServerTool(Name = "v6_preview_voice_comp", Title = "Preview VOCALOID Voice Comp", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Listen on the Editor computer: action=source/result generates a bounded temporary clip from current completed audio and returns a cancellable job; phrase starts native Part playback; stop stops this preview. source_role=body/consonant/vowel/isolated. play=false prepares/verifies a source/result clip without sounding it. Requires the write lease, creates no project history. Stale/failed/pending audio is refused; query voice-comp state first. No file path input or project export.")]
    public Task<McpBridgeResult> PreviewVoiceComp(McpServerInstance server, string project_id, long expected_revision,
        string client_request_id, string action, int track_index = -1, int part_index = -1, int note_index = -1,
        string source_role = "body", bool play = true, string? instance_id = null, CancellationToken cancellationToken = default)
        => _gateway.InvokeAsync(server, "v6_preview_voice_comp", instance_id, new { project_id, expected_revision,
            client_request_id, action, track_index, part_index, note_index, source_role, play }, cancellationToken);
}
