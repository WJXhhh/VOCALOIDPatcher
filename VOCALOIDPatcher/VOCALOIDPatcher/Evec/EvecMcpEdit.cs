using System;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.Evec;

internal static class EvecMcpEdit
{
    internal static EvecNoteState Resolve(EvecEditRequest request, EvecNoteState current,
        string phonemes, EvecVoicebankCapabilities capabilities)
    {
        var state = request.Clear ? EvecNoteState.Empty.Clone() : current.Clone();
        state.VoiceColorId = request.VoiceColorId ?? state.VoiceColorId;
        state.AttackId = request.AttackId ?? state.AttackId;
        state.ReleaseId = request.ReleaseId ?? state.ReleaseId;
        state.ConsonantExtension = request.ConsonantExtension ?? state.ConsonantExtension;
        if (!EvecConstants.IsValidVoiceColorId(state.VoiceColorId) ||
            !EvecConstants.IsValidAttackId(state.AttackId) ||
            !EvecConstants.IsValidReleaseId(state.ReleaseId) ||
            !EvecConstants.IsValidConsonantExtension(state.ConsonantExtension))
            throw new ArgumentException("Unknown EVEC option ID; query the voicebank's evec options.");
        if (!capabilities.IsSupported || !capabilities.Normalize(phonemes, state).Equals(state) ||
            !EvecPhonemeRecomposer.CanRepresent(phonemes, state))
            throw new NotSupportedException("The EVEC combination is not supported by this voicebank or phoneme layout.");
        return state;
    }
}
