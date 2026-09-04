using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VOCALOIDPatcher.Evec;

internal static class EvecPhonemeRecomposer
{
    private static readonly HashSet<string> ColorablePhonemes = new(StringComparer.Ordinal)
    {
        "a", "i", "M", "e", "o", "n", "J", "m", "m'", "N", "N'", "N\\"
    };

    private static readonly Regex SuffixRegex = new(@"#(?:[1-6]|\+|-|[Ff])$", RegexOptions.Compiled);

    internal static bool IsColorablePhoneme(string token) => ColorablePhonemes.Contains(token);

    public static string StripEvec(string phonemes) => Analyze(phonemes).BasePhonemes;

    public static bool TryParseEvecFromPhonemes(
        string phonemes,
        out EvecNoteState state,
        out string basePhonemes)
    {
        var analysis = Analyze(phonemes);
        state = analysis.State;
        basePhonemes = analysis.BasePhonemes;
        return state.HasAnyEvec;
    }

    // A suffix-less CTop and one pronunciation-extension repeat have the same
    // external spelling. Piapro still stores them as separate fields, but V6
    // only retains the recomposed phoneme string. When no sidecar/live state
    // is available, prefer one plain CTop copy and assign any remaining copies
    // to pronunciation extension. This makes the official Rin/Len Accent
    // reachable instead of importing every "C C V" as extension-only.
    internal static EvecNoteState ResolvePlainAttackAmbiguity(
        string phonemes,
        EvecNoteState state,
        int plainAttackId)
    {
        var resolved = state.Clone();
        if (plainAttackId == EvecConstants.AttackNone ||
            resolved.AttackId != EvecConstants.AttackNone)
            return resolved;

        int additions = Analyze(phonemes).PlainConsonantAdditions;
        if (additions <= 0)
            return resolved;

        resolved.AttackId = plainAttackId;
        resolved.ConsonantExtension = Math.Clamp(
            additions - 1,
            EvecConstants.MinConsonantExtension,
            EvecConstants.MaxConsonantExtension);
        return resolved;
    }

    internal static bool CanRepresent(string phonemes, EvecNoteState state)
    {
        if (!EvecConstants.IsValidVoiceColorId(state.VoiceColorId) ||
            !EvecConstants.IsValidAttackId(state.AttackId) ||
            !EvecConstants.IsValidReleaseId(state.ReleaseId) ||
            !EvecConstants.IsValidConsonantExtension(state.ConsonantExtension))
            return false;

        string basePhonemes = StripEvec(phonemes);
        if (string.IsNullOrWhiteSpace(basePhonemes))
            return !state.HasAnyEvec;

        var tokens = basePhonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int nucleusIndex = FindNucleusIndex(tokens);
        if (state.HasVoiceColor &&
            (nucleusIndex < 0 || string.IsNullOrEmpty(state.ColorSuffix)))
            return false;
        if ((state.HasConsonantAttack || state.HasConsonantExtension) && nucleusIndex <= 0)
            return false;
        if (state.HasVoiceRelease && string.IsNullOrEmpty(state.ReleasePhoneme))
            return false;

        return true;
    }

    internal static bool TryGetConsonantBeforeNucleus(string phonemes, out string consonant)
    {
        string basePhonemes = StripEvec(phonemes);
        var tokens = basePhonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int nucleusIndex = FindNucleusIndex(tokens);
        if (nucleusIndex <= 0)
        {
            consonant = string.Empty;
            return false;
        }

        consonant = tokens[nucleusIndex - 1];
        return true;
    }

    public static string Recompose(string currentPhonemes, EvecNoteState state)
    {
        if (string.IsNullOrWhiteSpace(currentPhonemes))
            return string.Empty;

        string basePhonemes = StripEvec(currentPhonemes);
        if (string.IsNullOrWhiteSpace(basePhonemes) || !state.HasAnyEvec)
            return basePhonemes;
        if (!CanRepresent(basePhonemes, state))
            return basePhonemes;

        var tokens = basePhonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int nucleusIndex = FindNucleusIndex(tokens);

        // PPS stores pronunciation extension by repeating the leading
        // consonant. CTop contributes one additional copy and appends its
        // configured suffix to that last copy. The caret spelling visible in
        // DDI ART keys is internal and must not be written to a note.
        if ((state.HasConsonantExtension || state.HasConsonantAttack) && nucleusIndex > 0)
        {
            string consonant = tokens[nucleusIndex - 1];
            for (int repeat = 0; repeat < state.ConsonantExtension; repeat++)
            {
                tokens.Insert(nucleusIndex, consonant);
                nucleusIndex++;
            }

            if (state.HasConsonantAttack)
            {
                tokens.Insert(nucleusIndex, consonant + EvecConstants.GetAttackSuffix(state.AttackId));
                nucleusIndex++;
            }
        }

        // CVV is base V followed by the colored V recording.
        if (state.HasVoiceColor && nucleusIndex >= 0)
            tokens.Insert(nucleusIndex + 1, tokens[nucleusIndex] + state.ColorSuffix);

        if (state.HasVoiceRelease)
            tokens.Add(state.ReleasePhoneme);

        return string.Join(" ", tokens);
    }

    internal static bool IsExactRealization(string phonemes, EvecNoteState state)
    {
        string basePhonemes = StripEvec(phonemes);
        string recomposed = Recompose(basePhonemes, state);
        return string.Equals(recomposed, phonemes, StringComparison.Ordinal);
    }

    private static EvecAnalysis Analyze(string phonemes)
    {
        var state = new EvecNoteState();
        if (string.IsNullOrWhiteSpace(phonemes))
            return new EvecAnalysis(state, string.Empty, 0);

        var tokens = phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int plainConsonantAdditions = 0;

        for (int index = tokens.Count - 1; index >= 0; index--)
        {
            int releaseId = EvecConstants.ParseReleasePhoneme(tokens[index]);
            if (releaseId == EvecConstants.ReleaseNone)
                continue;

            state.ReleaseId = releaseId;
            tokens.RemoveAt(index);
        }

        // Migrate the short-lived caret spelling used by earlier builds. PPS
        // never writes it externally, but recognizing it lets users clear or
        // rewrite affected notes without losing the base phonemes.
        for (int index = tokens.Count - 1; index >= 0; index--)
        {
            string token = tokens[index];
            if (!token.StartsWith('^') || token.Length <= 1)
                continue;

            string body = token[1..];
            var match = SuffixRegex.Match(body);
            string consonant = match.Success ? body[..match.Index] : body;
            string suffix = match.Success ? match.Value : string.Empty;
            if (index > 0 &&
                string.Equals(tokens[index - 1], consonant, StringComparison.Ordinal))
            {
                int attackId = EvecConstants.ParseAttackModifierSuffix(suffix);
                if (attackId != EvecConstants.AttackNone)
                    state.AttackId = attackId;
            }
            tokens.RemoveAt(index);
        }

        int nucleusIndex = FindNucleusIndex(tokens);

        // CVV is the only EVEC suffix pair after the logical nucleus.
        if (nucleusIndex >= 0 && nucleusIndex + 1 < tokens.Count &&
            TrySplitSuffix(tokens[nucleusIndex + 1], out string coloredBase, out string colorSuffix) &&
            string.Equals(tokens[nucleusIndex], coloredBase, StringComparison.Ordinal))
        {
            int colorId = EvecConstants.ParseVoiceColorSuffix(colorSuffix);
            if (colorId != EvecConstants.VoiceColorNone)
            {
                state.VoiceColorId = colorId;
                tokens.RemoveAt(nucleusIndex + 1);
            }
        }
        else if (nucleusIndex < 0)
        {
            // Legacy migration for an old single suffixed vowel form.
            for (int index = tokens.Count - 1; index >= 0; index--)
            {
                if (!TrySplitSuffix(tokens[index], out string basePart, out string suffix) ||
                    !IsColorablePhoneme(basePart))
                    continue;

                int colorId = EvecConstants.ParseVoiceColorSuffix(suffix);
                if (colorId == EvecConstants.VoiceColorNone)
                    continue;

                state.VoiceColorId = colorId;
                tokens[index] = basePart;
                break;
            }
        }

        nucleusIndex = FindNucleusIndex(tokens);
        if (nucleusIndex > 0 &&
            TrySplitSuffix(tokens[nucleusIndex - 1], out string attackBase, out string attackSuffix))
        {
            int attackId = EvecConstants.ParseAttackSuffix(attackSuffix);
            if (attackId != EvecConstants.AttackNone)
            {
                state.AttackId = attackId;
                int attackIndex = nucleusIndex - 1;
                int precedingCopies = CountPrecedingCopies(tokens, attackIndex, attackBase);
                state.ConsonantExtension = Math.Clamp(
                    precedingCopies - 1,
                    EvecConstants.MinConsonantExtension,
                    EvecConstants.MaxConsonantExtension);

                tokens[attackIndex] = attackBase;
                int totalCopies = precedingCopies + 1;
                int firstCopy = attackIndex - precedingCopies;
                if (totalCopies > 1)
                    tokens.RemoveRange(firstCopy + 1, totalCopies - 1);
            }
        }

        nucleusIndex = FindNucleusIndex(tokens);
        if (nucleusIndex > 0)
        {
            string consonant = tokens[nucleusIndex - 1];
            int copies = CountPrecedingCopies(tokens, nucleusIndex, consonant);
            if (copies > 1)
            {
                plainConsonantAdditions = copies - 1;
                state.ConsonantExtension = Math.Clamp(
                    Math.Max(state.ConsonantExtension, plainConsonantAdditions),
                    EvecConstants.MinConsonantExtension,
                    EvecConstants.MaxConsonantExtension);
                tokens.RemoveRange(nucleusIndex - copies + 1, copies - 1);
            }
        }

        return new EvecAnalysis(
            state,
            string.Join(" ", tokens),
            plainConsonantAdditions);
    }

    private static int CountPrecedingCopies(IReadOnlyList<string> tokens, int endExclusive, string value)
    {
        int count = 0;
        for (int index = endExclusive - 1; index >= 0; index--)
        {
            if (!string.Equals(tokens[index], value, StringComparison.Ordinal))
                break;
            count++;
        }
        return count;
    }

    private static bool TrySplitSuffix(string token, out string basePart, out string suffix)
    {
        var match = SuffixRegex.Match(token);
        if (!match.Success || match.Index == 0)
        {
            basePart = token;
            suffix = string.Empty;
            return false;
        }

        basePart = token[..match.Index];
        suffix = match.Value;
        return true;
    }

    private static int FindNucleusIndex(IReadOnlyList<string> tokens)
    {
        for (int index = tokens.Count - 1; index >= 0; index--)
        {
            if (IsColorablePhoneme(tokens[index]))
                return index;
        }
        return -1;
    }

    private sealed record EvecAnalysis(
        EvecNoteState State,
        string BasePhonemes,
        int PlainConsonantAdditions);
}
