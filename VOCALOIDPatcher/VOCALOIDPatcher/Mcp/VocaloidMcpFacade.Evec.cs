using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private static object EvecOptions(EvecVoicebankCapabilities capabilities) => new
    {
        supported = capabilities.IsSupported,
        enabled = EvecService.IsEnabled,
        colors = capabilities.Colors.Select(EvecOptionInfo).ToArray(),
        attacks = capabilities.Attacks.Select(EvecOptionInfo).ToArray(),
        releases = capabilities.Releases.Select(EvecOptionInfo).ToArray(),
        supports_consonant_extension = capabilities.HasConsonantExtension,
        consonant_extension_values = capabilities.HasConsonantExtension ? new[] { 0, 1, 2, 3 } : new[] { 0 },
        consonant_extension_semantics = "additional repeats: 0=none, 1=x2, 2=x3, 3=x4; CTop is independent",
    };

    private static object EvecOptionInfo(EvecOption option) => new
    {
        id = option.Id,
        display_key = option.DisplayKey,
        suffix = option.Suffix,
    };

    private static object EvecNoteInfo(WIVSMMidiPart part, WIVSMNote note)
    {
        var capabilities = EvecVoicebankDetector.GetCapabilities(part.VoiceBank());
        var state = EvecService.GetState(note);
        WIVSMSequence sequence = part.Sequence ?? throw Fault(McpErrorCodes.InvalidReference, "The EVEC note has no sequence.");
        WIVSMNote? next = note.Next;
        double duration = sequence.GetTimeFromTick(note.AbsPosTick, note.AbsEndTick) * 1000;
        double? gap = next == null ? null : sequence.GetTimeFromTick(note.AbsEndTick, next.AbsPosTick) * 1000;
        bool applicable = !note.IsAi && note.LangID == 0 && capabilities.IsSupported;
        IReadOnlyList<EvecRenderSegment> segments = Array.Empty<EvecRenderSegment>();
        bool planned = applicable && EvecRenderTimingPlan.TryCreate(note.Phonemes, state, duration, gap,
            next != null && next.LangID == note.LangID, out segments);
        return new
        {
            state = new { voice_color_id = state.VoiceColorId, attack_id = state.AttackId,
                release_id = state.ReleaseId, consonant_extension = state.ConsonantExtension },
            has_evec = state.HasAnyEvec,
            base_phonemes = EvecPhonemeRecomposer.StripEvec(note.Phonemes),
            protected_phonemes = note.IsProtected,
            applicable,
            options = EvecOptions(capabilities),
            maximum_consonant_extension = capabilities.MaximumConsonantExtension(note.Phonemes, state),
            timing = new
            {
                // The editor now publishes the segmented copy as this part's rendered
                // wave/score when EVEC is enabled; host-verified 2026-09-11.
                renderer_integrated = EvecService.IsEnabled,
                status = EvecService.IsEnabled ? "editor_render_copy" : "disabled",
                plan_available = planned,
                time_origin = "logical_note_start",
                unit = "milliseconds",
                segments = segments.Select(segment => new { kind = segment.Kind.ToString().ToLowerInvariant(),
                    begin_ms = segment.BeginMs, end_ms = segment.EndMs, phonemes = segment.Phonemes }).ToArray(),
                release_omitted = planned && state.HasVoiceRelease && !segments.Any(segment => segment.Kind == EvecRenderSegmentKind.Release),
            },
        };
    }

    private static EvecEditRequest ParseEvecRequest(JsonElement operation)
    {
        try
        {
            return EvecEditRequest.Parse(Element(operation, "evec") ?? default);
        }
        catch (ArgumentException exception)
        {
            throw Fault(McpErrorCodes.InvalidRequest, exception.Message);
        }
    }

    private static void ApplyEvecOperation(WIVSMMidiPart part, WIVSMNote note, JsonElement operation, bool execute)
    {
        var request = ParseEvecRequest(operation);
        if (!EvecService.IsEnabled)
            throw Fault(McpErrorCodes.Unsupported, "EVEC is disabled in Patcher settings.");
        var capabilities = EvecVoicebankDetector.GetCapabilities(part.VoiceBank());
        if (note.IsAi || note.LangID != 0 || !capabilities.IsSupported)
            throw Fault(McpErrorCodes.Unsupported, "EVEC requires a supported traditional Japanese voicebank and note.");
        EvecNoteState state;
        try { state = EvecMcpEdit.Resolve(request, EvecService.GetState(note), note.Phonemes, capabilities); }
        catch (ArgumentException exception) { throw Fault(McpErrorCodes.InvalidRequest, exception.Message); }
        catch (NotSupportedException exception) { throw Fault(McpErrorCodes.Unsupported, exception.Message); }
        if (execute && !EvecService.SetNoteEvecInTransaction(note, state))
            throw Fault(McpErrorCodes.OperationFailed, "VOCALOID could not apply EVEC to this note.");
    }
}
