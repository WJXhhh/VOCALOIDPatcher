using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Yamaha.VOCALOID.VDM;

namespace VOCALOIDPatcher.Evec;

internal sealed record EvecOption(int Id, string DisplayKey, string Suffix);

internal sealed class EvecVoicebankCapabilities
{
    private readonly HashSet<string> _consonantsWithoutExtensionSelfEdge;

    public static readonly EvecVoicebankCapabilities None = new(
        false,
        Array.Empty<EvecOption>(),
        Array.Empty<EvecOption>(),
        Array.Empty<EvecOption>());

    public bool IsSupported { get; }
    public IReadOnlyList<EvecOption> Colors { get; }
    public IReadOnlyList<EvecOption> Attacks { get; }
    public IReadOnlyList<EvecOption> Releases { get; }

    public bool HasColors => Colors.Count > 1;
    public bool HasAttacks => Attacks.Count > 1;
    public bool HasReleases => Releases.Count > 1;
    public bool HasConsonantExtension => HasAttacks;
    public int PlainAttackId => Attacks
        .FirstOrDefault(item =>
            item.Id != EvecConstants.AttackNone && string.IsNullOrEmpty(item.Suffix))
        ?.Id ?? EvecConstants.AttackNone;

    public EvecVoicebankCapabilities(
        bool isSupported,
        IReadOnlyList<EvecOption> colors,
        IReadOnlyList<EvecOption> attacks,
        IReadOnlyList<EvecOption> releases,
        IEnumerable<string>? consonantsWithoutExtensionSelfEdge = null)
    {
        IsSupported = isSupported;
        Colors = colors;
        Attacks = attacks;
        Releases = releases;
        _consonantsWithoutExtensionSelfEdge = consonantsWithoutExtensionSelfEdge == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(consonantsWithoutExtensionSelfEdge, StringComparer.Ordinal);
    }

    internal EvecNoteState Normalize(EvecNoteState state)
    {
        if (!IsSupported)
            return EvecNoteState.Empty.Clone();

        return new EvecNoteState(
            Colors.Any(item => item.Id == state.VoiceColorId)
                ? state.VoiceColorId
                : EvecConstants.VoiceColorNone,
            Attacks.Any(item => item.Id == state.AttackId)
                ? state.AttackId
                : EvecConstants.AttackNone,
            Releases.Any(item => item.Id == state.ReleaseId)
                ? state.ReleaseId
                : EvecConstants.ReleaseNone,
            HasConsonantExtension &&
            EvecConstants.IsValidConsonantExtension(state.ConsonantExtension)
                ? state.ConsonantExtension
                : EvecConstants.MinConsonantExtension);
    }

    internal EvecNoteState Normalize(string phonemes, EvecNoteState state)
    {
        var normalized = Normalize(state);
        normalized.ConsonantExtension = Math.Min(
            normalized.ConsonantExtension,
            MaximumConsonantExtension(phonemes, normalized));
        return normalized;
    }

    internal EvecNoteState SelectConsonantExtension(
        string phonemes,
        EvecNoteState state,
        int extension)
    {
        var selected = Normalize(state);
        selected.ConsonantExtension = Math.Clamp(
            extension,
            EvecConstants.MinConsonantExtension,
            EvecConstants.MaxConsonantExtension);

        // A small number of Rin/Len consonants can represent either one plain
        // repeat or the suffix-less CTop, but not both. Treat the control the
        // user touched last as authoritative: choosing an otherwise valid
        // extension clears the conflicting CTop instead of bouncing the
        // extension button back to its old value.
        if (selected.HasConsonantAttack &&
            selected.ConsonantExtension > MaximumConsonantExtension(phonemes, selected))
        {
            var withoutAttack = selected.Clone();
            withoutAttack.AttackId = EvecConstants.AttackNone;
            if (withoutAttack.ConsonantExtension <=
                MaximumConsonantExtension(phonemes, withoutAttack))
            {
                selected = withoutAttack;
            }
        }

        return Normalize(phonemes, selected);
    }

    internal int MaximumSelectableConsonantExtension(string phonemes)
    {
        // Availability belongs to the consonant graph, not to the currently
        // selected CTop. Keeping it independent prevents two controls from
        // disabling one another. SelectConsonantExtension resolves the one
        // real graph conflict using last-action-wins semantics.
        return MaximumConsonantExtension(phonemes, EvecNoteState.Empty);
    }

    internal int MaximumConsonantExtension(string phonemes, EvecNoteState state)
    {
        if (!HasConsonantExtension ||
            !EvecPhonemeRecomposer.TryGetConsonantBeforeNucleus(phonemes, out string consonant))
        {
            return EvecConstants.MinConsonantExtension;
        }

        if (!_consonantsWithoutExtensionSelfEdge.Contains(consonant))
            return EvecConstants.MaxConsonantExtension;

        // The direct C ^C V articulation remains valid without a C C
        // diphone. It can represent either the suffix-less CTop recording or
        // one plain repeat, but any additional copy needs the missing C C
        // self-edge and may render silent.
        return state.HasConsonantAttack
            ? EvecConstants.MinConsonantExtension
            : 1;
    }
}

internal static class EvecVoicebankDetector
{
    private static readonly ConcurrentDictionary<string, EvecVoicebankCapabilities> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MikuEvecDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIKU_V4X_Original_EVEC", "MIKU_V4X_Soft_EVEC", "MIKU_V4X_Solid_EVEC"
    };

    private static readonly HashSet<string> MikuBetaDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIKU_V4X_Beta_EVEC"
    };

    private static readonly HashSet<string> RinLenEvecDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "RIN_V4X_Power_EVEC", "LEN_V4X_Power_EVEC"
    };

    private static readonly HashSet<string> LukaEvecDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "LUKA_V4X_Hard_EVEC", "LUKA_V4X_Soft_EVEC", "LUKA_V4X_Hard_EVEC_DEMO"
    };

    private static readonly HashSet<string> VoiceReleaseOnlyDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "LUKA_V4X_Hard", "LUKA_V4X_Soft", "LUKA_V4X_Hard_DEMO",
        "RIN_V4X_Warm", "RIN_V4X_Sweet", "LEN_V4X_Serious", "LEN_V4X_Cold"
    };

    public static EvecVoicebankCapabilities GetCapabilities(VoiceBank? voiceBank)
    {
        if (voiceBank == null)
            return EvecVoicebankCapabilities.None;

        string key = voiceBank.CompID;
        if (string.IsNullOrEmpty(key))
            key = $"{voiceBank.Name}|{voiceBank.Path}";
        if (string.IsNullOrEmpty(key))
            return EvecVoicebankCapabilities.None;

        return Cache.GetOrAdd(key, _ => DetectCapabilities(voiceBank));
    }

    public static bool SupportsEvec(VoiceBank? voiceBank) => GetCapabilities(voiceBank).IsSupported;

    private static EvecVoicebankCapabilities DetectCapabilities(VoiceBank voiceBank)
    {
        try
        {
            string? databaseName = ResolveDatabaseName(voiceBank);
            if (databaseName == null)
                return EvecVoicebankCapabilities.None;

            // These profiles mirror Piapro Studio's installed articulation
            // definitions. PHDC membership alone is deliberately not used:
            // Rin/Len physically contain all nine colored vowel token sets,
            // while their official product profile exposes only Soft/Power.
            if (MikuEvecDatabases.Contains(databaseName))
                return BuildMikuEvecCapabilities();
            if (MikuBetaDatabases.Contains(databaseName))
                return BuildMikuBetaCapabilities();
            if (RinLenEvecDatabases.Contains(databaseName))
                return BuildRinLenCapabilities();
            if (LukaEvecDatabases.Contains(databaseName))
                return BuildLukaCapabilities();
            if (VoiceReleaseOnlyDatabases.Contains(databaseName))
                return BuildReleaseOnlyCapabilities();
        }
        catch
        {
            // Detection is an editor UI boundary. Unknown products degrade to
            // no EVEC panel instead of exposing a sequence that may not render.
        }

        return EvecVoicebankCapabilities.None;
    }

    private static string? ResolveDatabaseName(VoiceBank voiceBank)
    {
        // The entity database path is more authoritative than the VDM display
        // name. Installed descriptors can be stale or wrong (for example,
        // LEN_V4X_Serious is reported as RIN_V4X_Serious, while a component
        // named MIKU_V4X_Beta_EVEC points to MIKU_V4_Chinese.ddi).
        string? ddiPath = ResolveDdiPath(voiceBank);
        if (ddiPath != null)
        {
            string databaseName = Path.GetFileNameWithoutExtension(ddiPath);
            return AllKnownDatabaseNames().Contains(databaseName, StringComparer.OrdinalIgnoreCase)
                ? databaseName
                : null;
        }

        string name = voiceBank.Name ?? string.Empty;
        foreach (string databaseName in AllKnownDatabaseNames())
        {
            if (name.Contains(databaseName, StringComparison.OrdinalIgnoreCase))
                return databaseName;
        }

        return null;
    }

    private static IEnumerable<string> AllKnownDatabaseNames() =>
        MikuEvecDatabases
            .Concat(MikuBetaDatabases)
            .Concat(RinLenEvecDatabases)
            .Concat(LukaEvecDatabases)
            .Concat(VoiceReleaseOnlyDatabases);

    private static string? ResolveDdiPath(VoiceBank voiceBank)
    {
        string path = voiceBank.Path;
        if (string.IsNullOrEmpty(path))
            return null;

        if (path.EndsWith(".ddi", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return path;
        if (path.EndsWith(".ddb", StringComparison.OrdinalIgnoreCase))
        {
            string siblingDdi = Path.ChangeExtension(path, ".ddi");
            return File.Exists(siblingDdi) ? siblingDdi : null;
        }
        if (!Directory.Exists(path))
            return null;

        if (!string.IsNullOrEmpty(voiceBank.CompID))
        {
            string componentDirectory = Path.Combine(path, voiceBank.CompID);
            if (Directory.Exists(componentDirectory))
            {
                string[] componentFiles = Directory.GetFiles(componentDirectory, "*.ddi", SearchOption.TopDirectoryOnly);
                if (componentFiles.Length == 1)
                    return componentFiles[0];
            }
        }

        // Never select the first file from a shared product directory. Only a
        // unique DDI is safe when the VDM name was unavailable.
        string[] files = Directory.GetFiles(path, "*.ddi", SearchOption.AllDirectories);
        return files.Length == 1 ? files[0] : null;
    }

    private static EvecVoicebankCapabilities BuildMikuEvecCapabilities() => new(
        true,
        StandardColors(),
        new EvecOption[]
        {
            AttackNone(),
            new(EvecConstants.AttackMild, "VOCALOIDPatcher_Evec_Attack_Mild", "#2"),
            new(EvecConstants.AttackAccent, "VOCALOIDPatcher_Evec_Attack_Accent", "#6")
        },
        StandardReleases());

    private static EvecVoicebankCapabilities BuildMikuBetaCapabilities() => new(
        true,
        StandardColors(),
        new[] { AttackNone() },
        new[] { ReleaseNone() });

    private static EvecVoicebankCapabilities BuildRinLenCapabilities() => new(
        true,
        StandardColors(),
        new EvecOption[]
        {
            AttackNone(),
            new(EvecConstants.AttackAccentPlain, "VOCALOIDPatcher_Evec_Attack_Accent", string.Empty)
        },
        StandardReleases(),
        new[] { "Z", "h\\", "z" });

    private static EvecVoicebankCapabilities BuildLukaCapabilities() => new(
        true,
        new EvecOption[]
        {
            ColorNone(),
            new(EvecConstants.VoiceColorWhisper, "VOCALOIDPatcher_Evec_Color_Whisper", "#1"),
            new(EvecConstants.VoiceColorSoft, "VOCALOIDPatcher_Evec_Color_Soft", "#2"),
            new(EvecConstants.VoiceColorHusky, "VOCALOIDPatcher_Evec_Color_Husky", "#3"),
            new(EvecConstants.VoiceColorNative, "VOCALOIDPatcher_Evec_Color_Native", "#4"),
            new(EvecConstants.VoiceColorPower1, "VOCALOIDPatcher_Evec_Color_Power1", "#5"),
            new(EvecConstants.VoiceColorPower, "VOCALOIDPatcher_Evec_Color_Power", "#6"),
            new(EvecConstants.VoiceColorCute, "VOCALOIDPatcher_Evec_Color_Cute", "#+"),
            new(EvecConstants.VoiceColorDark, "VOCALOIDPatcher_Evec_Color_Dark", "#-"),
            new(EvecConstants.VoiceColorFalsetto, "VOCALOIDPatcher_Evec_Color_Falsetto", "#F")
        },
        new[] { AttackNone() },
        StandardReleases());

    private static EvecVoicebankCapabilities BuildReleaseOnlyCapabilities() => new(
        true,
        new[] { ColorNone() },
        new[] { AttackNone() },
        StandardReleases());

    private static EvecOption[] StandardColors() =>
    [
        ColorNone(),
        new(EvecConstants.VoiceColorSoft, "VOCALOIDPatcher_Evec_Color_Soft", "#2"),
        new(EvecConstants.VoiceColorPower, "VOCALOIDPatcher_Evec_Color_Power", "#6")
    ];

    private static EvecOption[] StandardReleases() =>
    [
        ReleaseNone(),
        new(EvecConstants.ReleaseBreathShort, "VOCALOIDPatcher_Evec_Release_Short", "*#1"),
        new(EvecConstants.ReleaseBreathLong, "VOCALOIDPatcher_Evec_Release_Long", "*#2")
    ];

    private static EvecOption ColorNone() =>
        new(EvecConstants.VoiceColorNone, "VOCALOIDPatcher_Evec_Color_Normal", string.Empty);

    private static EvecOption AttackNone() =>
        new(EvecConstants.AttackNone, "VOCALOIDPatcher_Evec_Attack_Normal", string.Empty);

    private static EvecOption ReleaseNone() =>
        new(EvecConstants.ReleaseNone, "VOCALOIDPatcher_Evec_Release_None", string.Empty);
}
