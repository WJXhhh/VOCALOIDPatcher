using System;
using System.Collections.Generic;
using System.Linq;
using ToolGood.Words.Pinyin;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Formats.LibreSvip;

/// <summary>
/// Adapts Hanzi lyrics to VOCALOID's native Chinese G2PA without replacing the
/// original lyric. ToolGood selects contextual pinyin; the native V6 module is
/// still the authority that validates each syllable and produces its phonemes.
/// </summary>
internal static class ChineseHanziG2paRecognizer
{
    private const char PinyinSeparator = '\u001f';
    private const int MaximumContextNotes = 16;
    private const int ChineseLanguageId = (int)VSMLanguageID.Chinese;

    public static bool SupportsChinese(WIVSMNote? note)
    {
        try
        {
            WIVSMMidiPart? part = note?.Parent;
            if (part == null)
                return false;

            return part.IsAi
                ? part.LangIDsFromAiVoiceBank().Contains(ChineseLanguageId)
                : part.LangIDFromVoiceBank() == ChineseLanguageId;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryConvert(
        WIVSMNote note,
        string value,
        out List<ChinesePinyinSyllable> syllables)
    {
        syllables = new List<ChinesePinyinSyllable>();
        try
        {
            if (!SupportsChinese(note)
                || !TryExtractHanzi(value, out List<char> currentCharacters)
                || !TryCollectTargetNotes(note, currentCharacters.Count, out List<WIVSMNote> targetNotes)
                || !TryResolveContextualPinyin(note, currentCharacters, out List<string> pinyin))
            {
                return false;
            }

            return TryBuildNativeSyllables(targetNotes, currentCharacters, pinyin, out syllables);
        }
        catch
        {
            syllables.Clear();
            return false;
        }
    }

    /// <summary>
    /// Re-evaluates nearby unlocked Hanzi after a lyric edit. This lets a newly
    /// completed word update an earlier polyphonic character while preserving
    /// notes whose phonemes were explicitly protected by the user.
    /// </summary>
    public static void RefreshContext(WIVSMNote? anchor)
    {
        try
        {
            if (!SupportsChinese(anchor)
                || anchor == null
                || !TryGetSingleHanzi(anchor.Lyric, out _))
            {
                return;
            }

            List<WIVSMNote> notes = CollectContextNotes(anchor);
            var characters = new List<char>(notes.Count);
            foreach (WIVSMNote note in notes)
            {
                if (!TryGetSingleHanzi(note.Lyric, out char character))
                    return;
                characters.Add(character);
            }

            if (!TryGetPinyin(characters, out List<string> pinyin))
                return;

            G2PAManager? manager = App.GetG2PAManager(ChineseLanguageId);
            if (manager == null)
                return;

            for (int i = 0; i < notes.Count; i++)
            {
                WIVSMNote note = notes[i];
                if (note.IsProtected
                    || !TryGetNativePhonemes(manager, note, pinyin[i], out string phonemes))
                {
                    continue;
                }

                if (note.LangID != ChineseLanguageId && !note.SetLangID(ChineseLanguageId))
                    continue;

                if (!G2PAMultiLingualManager.SetPhonemes(note, phonemes)
                    && !note.SetPhonemes(phonemes, true, ChineseLanguageId))
                {
                    continue;
                }

                note.IsProtected = true;
            }
        }
        catch
        {
            // Keep the result of the original lyric edit if contextual refresh fails.
        }
    }

    private static bool TryResolveContextualPinyin(
        WIVSMNote note,
        IReadOnlyList<char> currentCharacters,
        out List<string> pinyin)
    {
        var previous = new List<char>();
        WIVSMNote? cursor = note.Prev;
        while (cursor != null
               && previous.Count < MaximumContextNotes
               && TryGetSingleHanzi(cursor.Lyric, out char character))
        {
            previous.Add(character);
            cursor = cursor.Prev;
        }
        previous.Reverse();

        WIVSMNote? followingNote = note;
        for (int i = 0; i < currentCharacters.Count && followingNote != null; i++)
            followingNote = followingNote.Next;

        var following = new List<char>();
        cursor = followingNote;
        while (cursor != null
               && following.Count < MaximumContextNotes
               && TryGetSingleHanzi(cursor.Lyric, out char character))
        {
            following.Add(character);
            cursor = cursor.Next;
        }

        var context = new List<char>(previous.Count + currentCharacters.Count + following.Count);
        context.AddRange(previous);
        context.AddRange(currentCharacters);
        context.AddRange(following);
        if (!TryGetPinyin(context, out List<string> contextualPinyin))
        {
            pinyin = new List<string>();
            return false;
        }

        pinyin = contextualPinyin.GetRange(previous.Count, currentCharacters.Count);
        return true;
    }

    private static bool TryBuildNativeSyllables(
        IReadOnlyList<WIVSMNote> notes,
        IReadOnlyList<char> characters,
        IReadOnlyList<string> pinyin,
        out List<ChinesePinyinSyllable> syllables)
    {
        syllables = new List<ChinesePinyinSyllable>();
        if (notes.Count == 0 || notes.Count != characters.Count || notes.Count != pinyin.Count)
            return false;

        G2PAManager? manager = App.GetG2PAManager(ChineseLanguageId);
        if (manager == null)
            return false;

        for (int i = 0; i < notes.Count; i++)
        {
            if (!TryGetNativePhonemes(manager, notes[i], pinyin[i], out string phonemes))
            {
                syllables.Clear();
                return false;
            }

            syllables.Add(new ChinesePinyinSyllable(
                characters[i].ToString(),
                pinyin[i],
                phonemes,
                true));
        }

        return true;
    }

    private static bool TryGetNativePhonemes(
        G2PAManager manager,
        WIVSMNote note,
        string pinyin,
        out string phonemes)
    {
        phonemes = string.Empty;
        foreach (bool useExtensionDictionary in new[] { false, true })
        {
            if (!manager.CanConvert(pinyin, useExtensionDictionary, note.IsAi))
                continue;

            List<List<SyllableArgs>> candidates = manager.CandidatePhonemes(
                (IntPtr)note,
                pinyin,
                useExtensionDictionary,
                note.IsAi);
            foreach (List<SyllableArgs> candidate in candidates)
            {
                if (candidate.Count != 1 || string.IsNullOrWhiteSpace(candidate[0].Phoneme))
                    continue;

                phonemes = candidate[0].Phoneme;
                return true;
            }
        }

        return false;
    }

    private static bool TryCollectTargetNotes(
        WIVSMNote first,
        int count,
        out List<WIVSMNote> notes)
    {
        notes = new List<WIVSMNote>(count);
        WIVSMNote? current = first;
        while (current != null && notes.Count < count)
        {
            notes.Add(current);
            current = current.Next;
        }
        return notes.Count == count;
    }

    private static List<WIVSMNote> CollectContextNotes(WIVSMNote anchor)
    {
        var previous = new List<WIVSMNote>();
        WIVSMNote? cursor = anchor.Prev;
        while (cursor != null
               && previous.Count < MaximumContextNotes
               && TryGetSingleHanzi(cursor.Lyric, out _))
        {
            previous.Add(cursor);
            cursor = cursor.Prev;
        }
        previous.Reverse();

        var result = new List<WIVSMNote>(previous.Count + MaximumContextNotes + 1);
        result.AddRange(previous);
        result.Add(anchor);

        cursor = anchor.Next;
        int followingCount = 0;
        while (cursor != null
               && followingCount < MaximumContextNotes
               && TryGetSingleHanzi(cursor.Lyric, out _))
        {
            result.Add(cursor);
            followingCount++;
            cursor = cursor.Next;
        }
        return result;
    }

    private static bool TryGetPinyin(IReadOnlyList<char> characters, out List<string> pinyin)
    {
        pinyin = new List<string>();
        if (characters.Count == 0)
            return false;

        string converted = WordsHelper.GetPinyin(
            new string(characters.ToArray()),
            PinyinSeparator.ToString());
        string[] values = converted.Split(PinyinSeparator, StringSplitOptions.None);
        if (values.Length != characters.Count)
            return false;

        foreach (string value in values)
        {
            string normalized = NormalizePinyin(value);
            if (normalized.Length == 0)
            {
                pinyin.Clear();
                return false;
            }
            pinyin.Add(normalized);
        }
        return true;
    }

    private static bool TryExtractHanzi(string? value, out List<char> characters)
    {
        characters = new List<char>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
                continue;
            if (!IsChinese(character))
            {
                characters.Clear();
                return false;
            }
            characters.Add(character);
        }
        return characters.Count > 0;
    }

    private static bool TryGetSingleHanzi(string? value, out char character)
    {
        character = default;
        if (!TryExtractHanzi(value, out List<char> characters) || characters.Count != 1)
            return false;
        character = characters[0];
        return true;
    }

    private static string NormalizePinyin(string value) =>
        value.Trim().ToLowerInvariant().Replace("u:", "v", StringComparison.Ordinal).Replace('ü', 'v');

    private static bool IsChinese(char value) =>
        value is >= '\u3400' and <= '\u4dbf'
            or >= '\u4e00' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff';
}
