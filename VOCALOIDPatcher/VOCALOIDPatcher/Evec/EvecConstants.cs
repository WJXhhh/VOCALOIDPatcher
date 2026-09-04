using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.Evec;

internal static class EvecConstants
{
    // Voice Color IDs (CVV)
    public const int VoiceColorNone = 0;
    public const int VoiceColorWhisper = 100; // #1
    public const int VoiceColorSoft = 101;    // #2
    public const int VoiceColorHusky = 102;   // #3
    public const int VoiceColorNative = 103;  // #4
    public const int VoiceColorPower1 = 104;  // #5
    public const int VoiceColorPower = 105;   // #6 (Power 2 in Luka V4X)
    public const int VoiceColorCute = 106;    // #+
    public const int VoiceColorDark = 107;    // #-
    public const int VoiceColorFalsetto = 108;// #F

    // Consonant Attack IDs (CTop)
    public const int AttackNone = 0;
    public const int AttackAccentPlain = 301;// Rin/Len: no suffix
    public const int AttackMild = 302;        // #2
    public const int AttackAccent = 306;      // #6

    // Independent Piapro top-consonant repeat count.
    public const int MinConsonantExtension = 0;
    public const int MaxConsonantExtension = 3;

    // Voice Release IDs (VSil)
    public const int ReleaseNone = 0;
    public const int ReleaseBreathShort = 201;// *#1
    public const int ReleaseBreathLong = 202; // *#2

    // ID to Suffix mappings
    public static string GetVoiceColorSuffix(int id) => id switch
    {
        VoiceColorWhisper  => "#1",
        VoiceColorSoft     => "#2",
        VoiceColorHusky    => "#3",
        VoiceColorNative   => "#4",
        VoiceColorPower1   => "#5",
        VoiceColorPower    => "#6",
        VoiceColorCute     => "#+",
        VoiceColorDark     => "#-",
        VoiceColorFalsetto => "#F",
        _                  => string.Empty,
    };

    public static string GetAttackSuffix(int id) => id switch
    {
        AttackMild   => "#2",
        AttackAccent => "#6",
        _            => string.Empty,
    };

    public static string GetReleasePhoneme(int id) => id switch
    {
        ReleaseBreathShort => "*#1",
        ReleaseBreathLong  => "*#2",
        _                  => string.Empty,
    };

    public static int ParseVoiceColorSuffix(string suffix) => suffix switch
    {
        "#1" => VoiceColorWhisper,
        "#2" => VoiceColorSoft,
        "#3" => VoiceColorHusky,
        "#4" => VoiceColorNative,
        "#5" => VoiceColorPower1,
        "#6" => VoiceColorPower,
        "#+" => VoiceColorCute,
        "#-" => VoiceColorDark,
        "#F" or "#f" => VoiceColorFalsetto,
        _    => VoiceColorNone,
    };

    public static int ParseAttackSuffix(string suffix) => suffix switch
    {
        "#2" => AttackMild,
        "#6" => AttackAccent,
        _    => AttackNone,
    };

    public static int ParseAttackModifierSuffix(string suffix) => suffix switch
    {
        ""   => AttackAccentPlain,
        "#2" => AttackMild,
        "#6" => AttackAccent,
        _    => AttackNone,
    };

    public static int ParseReleasePhoneme(string phoneme) => phoneme switch
    {
        "*#1" => ReleaseBreathShort,
        "*#2" => ReleaseBreathLong,
        _     => ReleaseNone,
    };

    public static bool IsValidVoiceColorId(int id) => id is
        VoiceColorNone or
        VoiceColorWhisper or VoiceColorSoft or VoiceColorHusky or VoiceColorNative or
        VoiceColorPower1 or VoiceColorPower or VoiceColorCute or VoiceColorDark or
        VoiceColorFalsetto;

    public static bool IsValidAttackId(int id) => id is
        AttackNone or AttackAccentPlain or AttackMild or AttackAccent;

    public static bool IsAccentAttack(int id) => id is AttackAccentPlain or AttackAccent;

    public static bool IsValidConsonantExtension(int value) =>
        value is >= MinConsonantExtension and <= MaxConsonantExtension;

    public static bool IsValidReleaseId(int id) => id is
        ReleaseNone or ReleaseBreathShort or ReleaseBreathLong;
}
