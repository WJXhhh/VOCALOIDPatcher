using System;

namespace VOCALOIDPatcher.Evec;

internal sealed class EvecNoteState : IEquatable<EvecNoteState>
{
    public static readonly EvecNoteState Empty = new();

    public int VoiceColorId { get; set; } = EvecConstants.VoiceColorNone;
    public int AttackId { get; set; } = EvecConstants.AttackNone;
    public int ReleaseId { get; set; } = EvecConstants.ReleaseNone;
    public int ConsonantExtension { get; set; } = EvecConstants.MinConsonantExtension;

    public bool HasVoiceColor => VoiceColorId != EvecConstants.VoiceColorNone;
    public bool HasConsonantAttack => AttackId != EvecConstants.AttackNone;
    public bool HasVoiceRelease => ReleaseId != EvecConstants.ReleaseNone;
    public bool HasConsonantExtension => ConsonantExtension > EvecConstants.MinConsonantExtension;

    public bool HasAnyEvec =>
        HasVoiceColor || HasConsonantAttack || HasVoiceRelease || HasConsonantExtension;

    public string ColorSuffix => EvecConstants.GetVoiceColorSuffix(VoiceColorId);
    public string AttackDescription => AttackId == EvecConstants.AttackAccentPlain
        ? "plain"
        : EvecConstants.GetAttackSuffix(AttackId);
    public string ReleasePhoneme => EvecConstants.GetReleasePhoneme(ReleaseId);

    public EvecNoteState() { }

    public EvecNoteState(
        int voiceColorId,
        int attackId,
        int releaseId,
        int consonantExtension = EvecConstants.MinConsonantExtension)
    {
        VoiceColorId = voiceColorId;
        AttackId = attackId;
        ReleaseId = releaseId;
        ConsonantExtension = consonantExtension;
    }

    public EvecNoteState Clone() =>
        new(VoiceColorId, AttackId, ReleaseId, ConsonantExtension);

    public bool Equals(EvecNoteState? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return VoiceColorId == other.VoiceColorId &&
               AttackId == other.AttackId &&
               ReleaseId == other.ReleaseId &&
               ConsonantExtension == other.ConsonantExtension;
    }

    public override bool Equals(object? obj) => Equals(obj as EvecNoteState);

    public override int GetHashCode() =>
        HashCode.Combine(VoiceColorId, AttackId, ReleaseId, ConsonantExtension);

    public override string ToString() =>
        $"EvecNoteState(Color={VoiceColorId}:{ColorSuffix}, Attack={AttackId}:{AttackDescription}, Release={ReleaseId}:{ReleasePhoneme}, Extension={ConsonantExtension})";
}
