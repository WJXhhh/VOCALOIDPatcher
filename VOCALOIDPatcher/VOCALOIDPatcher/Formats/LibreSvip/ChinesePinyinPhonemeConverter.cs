using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VOCALOIDPatcher.Formats.LibreSvip.Plugins.Vsqx;

namespace VOCALOIDPatcher.Formats.LibreSvip;

internal readonly record struct ChinesePinyinSyllable(
    string Lyric,
    string NormalizedLyric,
    string Phonemes,
    bool RequiresOverride);

internal static class ChinesePinyinPhonemeConverter
{
    private static readonly Dictionary<string, string> Initials = new(StringComparer.Ordinal)
    {
        ["b"] = "p",
        ["p"] = "p_h",
        ["m"] = "m",
        ["f"] = "f",
        ["d"] = "t",
        ["t"] = "t_h",
        ["n"] = "n",
        ["l"] = "l",
        ["g"] = "k",
        ["k"] = "k_h",
        ["h"] = "x",
        ["j"] = "ts\\",
        ["q"] = "ts\\_h",
        ["x"] = "s\\",
        ["zh"] = "ts`",
        ["ch"] = "ts`_h",
        ["sh"] = "s`",
        ["r"] = "z`",
        ["z"] = "ts",
        ["c"] = "ts_h",
        ["s"] = "s",
        ["y"] = "i",
        ["w"] = "u",
    };

    private static readonly Dictionary<string, string> Finals = new(StringComparer.Ordinal)
    {
        ["a"] = "a",
        ["o"] = "o",
        ["e"] = "7",
        ["i"] = "i",
        ["u"] = "u",
        ["v"] = "y",
        ["er"] = "@`",
        ["ai"] = "aI",
        ["ei"] = "ei",
        ["ao"] = "AU",
        ["ou"] = "@U",
        ["ia"] = "ia",
        ["ie"] = "iE_r",
        ["ua"] = "ua",
        ["uo"] = "uo",
        ["ve"] = "yE_r",
        ["iao"] = "iAU",
        ["iu"] = "i@U",
        ["uai"] = "uaI",
        ["ui"] = "uei",
        ["an"] = "a_n",
        ["en"] = "@_n",
        ["in"] = "i_n",
        ["ian"] = "iE_n",
        ["uan"] = "ua_n",
        ["un"] = "u@_n",
        ["vn"] = "y_n",
        ["van"] = "y{_n",
        ["ang"] = "AN",
        ["eng"] = "@N",
        ["ing"] = "iN",
        ["iang"] = "iAN",
        ["uang"] = "uAN",
        ["ueng"] = "u@N",
        ["ong"] = "UN",
        ["iong"] = "iUN",
    };

    private static readonly string[] InitialMatchOrder =
    {
        "zh", "ch", "sh",
        "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h",
        "j", "q", "x", "r", "z", "c", "s", "y", "w",
    };

    public static bool TryConvertToken(string token, out ChinesePinyinSyllable syllable)
    {
        syllable = default;
        if (!TryNormalizeToken(token, out string normalized, out bool wasNormalized))
            return false;

        if (normalized == "asp")
        {
            syllable = new ChinesePinyinSyllable(token, normalized, "Asp", true);
            return true;
        }

        if (normalized == "sil")
        {
            syllable = new ChinesePinyinSyllable(token, normalized, "Sil", true);
            return true;
        }

        if (normalized == "ng")
        {
            syllable = new ChinesePinyinSyllable(token, normalized, "@N", true);
            return true;
        }

        if (normalized == "hm")
        {
            syllable = new ChinesePinyinSyllable(token, normalized, "x m", true);
            return true;
        }

        if (normalized == "hng")
        {
            syllable = new ChinesePinyinSyllable(token, normalized, "x @N", true);
            return true;
        }

        if (VsqxPhonemeMaps.Pinyin2Xsampa.TryGetValue(normalized, out string? exactPhonemes))
        {
            bool isStandalone = Initials.ContainsKey(normalized) || Finals.ContainsKey(normalized);
            syllable = new ChinesePinyinSyllable(token, normalized, exactPhonemes, wasNormalized || isStandalone);
            return true;
        }

        if (Initials.TryGetValue(normalized, out string? initialPhoneme))
        {
            syllable = new ChinesePinyinSyllable(token, normalized, initialPhoneme, true);
            return true;
        }

        if (Finals.TryGetValue(normalized, out string? finalPhoneme))
        {
            syllable = new ChinesePinyinSyllable(token, normalized, finalPhoneme, true);
            return true;
        }

        foreach (string initial in InitialMatchOrder)
        {
            if (!normalized.StartsWith(initial, StringComparison.Ordinal))
                continue;

            string final = normalized[initial.Length..];
            if (final.Length == 0 || !Finals.TryGetValue(final, out finalPhoneme))
                continue;

            syllable = new ChinesePinyinSyllable(
                token,
                normalized,
                $"{Initials[initial]} {finalPhoneme}",
                true);
            return true;
        }

        return false;
    }

    public static bool TryConvertSequence(
        string value,
        out List<ChinesePinyinSyllable> syllables,
        out bool requiresOverride)
    {
        var convertedSyllables = new List<ChinesePinyinSyllable>();
        bool convertedRequiresOverride = false;
        syllables = convertedSyllables;
        requiresOverride = convertedRequiresOverride;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var token = new StringBuilder();
        bool normalizedBoundary = false;

        bool FlushToken()
        {
            if (token.Length == 0)
                return true;

            string current = token.ToString();
            token.Clear();
            if (!TryConvertToken(current, out var converted))
                return false;

            convertedSyllables.Add(converted);
            convertedRequiresOverride |= converted.RequiresOverride;
            return true;
        }

        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || character is '\'' or '\u2019')
            {
                if (!FlushToken())
                {
                    convertedSyllables.Clear();
                    syllables = convertedSyllables;
                    requiresOverride = false;
                    return false;
                }

                normalizedBoundary |= character is '\'' or '\u2019';
                continue;
            }

            token.Append(character);
        }

        if (!FlushToken() || convertedSyllables.Count == 0)
        {
            convertedSyllables.Clear();
            syllables = convertedSyllables;
            requiresOverride = false;
            return false;
        }

        convertedRequiresOverride |= normalizedBoundary;
        syllables = convertedSyllables;
        requiresOverride = convertedRequiresOverride;
        return true;
    }

    private static bool TryNormalizeToken(string token, out string normalized, out bool wasNormalized)
    {
        normalized = string.Empty;
        wasNormalized = false;
        string trimmed = token.Trim();
        if (trimmed.Length == 0)
            return false;

        string lower = trimmed.ToLowerInvariant();
        string decomposed = lower.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);

        foreach (char character in decomposed)
        {
            switch (character)
            {
                case '\u0304': // macron
                case '\u0301': // acute accent
                case '\u030c': // caron
                case '\u0300': // grave accent
                    continue;
                case '\u0308' when result.Length > 0 && result[^1] == 'u':
                    result[^1] = 'v';
                    continue;
                default:
                    result.Append(character);
                    break;
            }
        }

        normalized = result.ToString().Normalize(NormalizationForm.FormC).Replace("u:", "v", StringComparison.Ordinal);
        if (normalized.Length > 0 && normalized[^1] is >= '1' and <= '5')
            normalized = normalized[..^1];

        wasNormalized = !string.Equals(normalized, trimmed, StringComparison.Ordinal);
        return normalized.Length > 0;
    }
}
