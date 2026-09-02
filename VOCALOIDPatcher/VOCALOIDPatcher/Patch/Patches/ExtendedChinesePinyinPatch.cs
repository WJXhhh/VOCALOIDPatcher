using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Formats.LibreSvip;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public class ExtendedChinesePinyinSetLyricsPatch : PatchBase
{
    public override string PatchName        => "ExtendedChinesePinyinSetLyricsPatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "SetLyrics";
    public override Type[] ArgumentTypes    =>
        new[] { typeof(WIVSMNote), typeof(string), typeof(int), typeof(bool), typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMNote note,
        out List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.ReleaseManagedProtection(note);
        __state = Settings.ExtendedChinesePinyin
            ? ExtendedChinesePinyinResetHelper.CaptureAdjacentContext(note)
            : new List<ExtendedChinesePinyinResetHelper.ProtectedNoteState>();
    }

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMNote note,
        string lyrics,
        int langID,
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state,
        ref (bool IsSuccess, WIVSMNote? NextNote) __result)
    {
        bool hanziApplied = false;
        try
        {
            if (!Settings.ExtendedChinesePinyin)
                return;

            if (!__result.IsSuccess
                && langID == (int)VSMLanguageID.Chinese
                && ChineseHanziG2paRecognizer.TryConvert(note, lyrics, out var hanziSyllables))
            {
                if (!ChinesePinyinSyllableApplicator.TrySetSyllables(
                        note,
                        hanziSyllables,
                        (int)VSMLanguageID.Chinese,
                        out var hanziResult))
                {
                    return;
                }

                ExtendedChinesePinyinResetHelper.ProtectAppliedSyllables(
                    note,
                    hanziResult.NextNote,
                    hanziSyllables);
                ExtendedChinesePinyinResetHelper.DiscardAffectedContext(
                    __state,
                    note,
                    hanziResult.NextNote);
                __result = hanziResult;
                hanziApplied = true;
                return;
            }

            if (!ChinesePinyinPhonemeConverter.TryConvertSequence(
                    lyrics,
                    out var syllables,
                    out bool requiresOverride))
            {
                return;
            }

            bool isSpecial = ChinesePinyinPhonemeConverter.IsVocaloidSpecialSequence(syllables);
            if (!isSpecial && !requiresOverride && __result.IsSuccess)
            {
                return;
            }

            int targetLangId = isSpecial ? langID : (int)VSMLanguageID.Chinese;
            if (!ChinesePinyinSyllableApplicator.TrySetSyllables(note, syllables, targetLangId, out var result))
                return;

            ExtendedChinesePinyinResetHelper.ProtectAppliedSyllables(
                note,
                result.NextNote,
                syllables);
            ExtendedChinesePinyinResetHelper.DiscardAffectedContext(__state, note, result.NextNote);
            __result = result;
        }
        catch
        {
            // Keep the failure returned by VOCALOID's native G2PA path.
        }
        finally
        {
            ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
            if (hanziApplied)
                ChineseHanziG2paRecognizer.RefreshContext(note);
            ExtendedChinesePinyinResetHelper.EnsureManagedProtection(note);
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        WIVSMNote note,
        Exception? __exception,
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
        ExtendedChinesePinyinResetHelper.EnsureManagedProtection(note);
        return __exception;
    }
}

public class VocaloidSpecialPhonemeAiSetLyricsPatch : PatchBase
{
    public override string PatchName        => "VocaloidSpecialPhonemeAiSetLyricsPatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "SetLyrics";
    public override Type[] ArgumentTypes    => new[] { typeof(WIVSMNote), typeof(string) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMNote note,
        out List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.ReleaseManagedProtection(note);
        __state = Settings.ExtendedChinesePinyin && note.IsAi
            ? ExtendedChinesePinyinResetHelper.CaptureAdjacentContext(note)
            : new List<ExtendedChinesePinyinResetHelper.ProtectedNoteState>();
    }

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMNote note,
        string lyrics,
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state,
        ref (bool IsSuccess, WIVSMNote? NextNote) __result)
    {
        bool hanziApplied = false;
        try
        {
            if (!Settings.ExtendedChinesePinyin
                || !note.IsAi)
            {
                return;
            }

            if (!__result.IsSuccess
                && ChineseHanziG2paRecognizer.TryConvert(note, lyrics, out var hanziSyllables))
            {
                if (!ChinesePinyinSyllableApplicator.TrySetSyllables(
                        note,
                        hanziSyllables,
                        (int)VSMLanguageID.Chinese,
                        out var hanziResult))
                {
                    return;
                }

                ExtendedChinesePinyinResetHelper.ProtectAppliedSyllables(
                    note,
                    hanziResult.NextNote,
                    hanziSyllables);
                ExtendedChinesePinyinResetHelper.DiscardAffectedContext(
                    __state,
                    note,
                    hanziResult.NextNote);
                __result = hanziResult;
                hanziApplied = true;
                return;
            }

            if (!ChinesePinyinPhonemeConverter.TryConvertSequence(
                    lyrics,
                    out var syllables,
                    out bool requiresOverride))
            {
                return;
            }

            bool isSpecial = ChinesePinyinPhonemeConverter.IsVocaloidSpecialSequence(syllables);
            if (!isSpecial && !requiresOverride && __result.IsSuccess)
            {
                return;
            }

            int targetLangId = isSpecial ? note.LangID : (int)VSMLanguageID.Chinese;
            if (ChinesePinyinSyllableApplicator.TrySetSyllables(
                    note,
                    syllables,
                    targetLangId,
                    out var result))
            {
                ExtendedChinesePinyinResetHelper.ProtectAppliedSyllables(
                    note,
                    result.NextNote,
                    syllables);
                ExtendedChinesePinyinResetHelper.DiscardAffectedContext(__state, note, result.NextNote);
                __result = result;
            }
        }
        catch
        {
            // Keep the result returned by VOCALOID's native G2PA path.
        }
        finally
        {
            ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
            if (hanziApplied)
                ChineseHanziG2paRecognizer.RefreshContext(note);
            ExtendedChinesePinyinResetHelper.EnsureManagedProtection(note);
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        WIVSMNote note,
        Exception? __exception,
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
        ExtendedChinesePinyinResetHelper.EnsureManagedProtection(note);
        return __exception;
    }
}

public class ExtendedChinesePinyinSetSyllablesContextPatch : PatchBase
{
    public override string PatchName        => "ExtendedChinesePinyinSetSyllablesContextPatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "SetSyllables";
    public override Type[] ArgumentTypes    =>
        new[] { typeof(WIVSMNote), typeof(SyllablesData), typeof(int), typeof(int), typeof(bool) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMNote note,
        int syllablesSize,
        out List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        __state = Settings.ExtendedChinesePinyin
            ? ExtendedChinesePinyinResetHelper.CaptureAdjacentContext(note, syllablesSize)
            : new List<ExtendedChinesePinyinResetHelper.ProtectedNoteState>();
    }

    [HarmonyPostfix]
    private static void Postfix(
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
        return __exception;
    }
}

public class ExtendedChinesePinyinCandidatePatch : PatchBase
{
    public override string PatchName        => "ExtendedChinesePinyinCandidatePatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "CandidatePhonemes";
    public override Type[] ArgumentTypes    =>
        new[] { typeof(WIVSMNote), typeof(string), typeof(int), typeof(bool), typeof(bool) };

    [HarmonyPostfix]
    private static void Postfix(
        WIVSMNote note,
        string lyrics,
        int langID,
        ref List<Syllables> __result)
    {
        if (!Settings.ExtendedChinesePinyin)
            return;

        if (__result.Count == 0
            && langID == (int)VSMLanguageID.Chinese
            && ChineseHanziG2paRecognizer.TryConvert(note, lyrics, out var hanziSyllables))
        {
            try
            {
                __result = new List<Syllables>
                {
                    ChinesePinyinSyllableApplicator.CreateCandidate(
                        hanziSyllables,
                        (int)VSMLanguageID.Chinese),
                };
            }
            catch
            {
                // Keep the empty result returned by VOCALOID's native G2PA path.
            }
            return;
        }

        if (!ChinesePinyinPhonemeConverter.TryConvertSequence(
                lyrics,
                out var syllables,
                out bool requiresOverride))
        {
            return;
        }

        bool isSpecial = ChinesePinyinPhonemeConverter.IsVocaloidSpecialSequence(syllables);
        if (!isSpecial && !requiresOverride && __result is { Count: > 0 })
        {
            return;
        }

        try
        {
            int targetLangId = isSpecial ? langID : (int)VSMLanguageID.Chinese;
            __result = new List<Syllables>
            {
                ChinesePinyinSyllableApplicator.CreateCandidate(syllables, targetLangId),
            };
        }
        catch
        {
            // Keep the empty result returned by VOCALOID's native G2PA path.
        }
    }
}

public class ExtendedChinesePinyinFloatingInputRepairPatch : PatchBase
{
    private static readonly FieldInfo? TextBoxField =
        AccessTools.Field(typeof(FloatingInputField), "xTextBox");

    public override string PatchName        => nameof(ExtendedChinesePinyinFloatingInputRepairPatch);
    public override Type TargetClass        => typeof(FloatingInputField);
    public override string TargetMethodName => "OnPreviewKeyDown";
    public override Type[] ArgumentTypes    => new[] { typeof(object), typeof(KeyEventArgs) };

    [HarmonyPrefix]
    private static void Prefix(FloatingInputField __instance, KeyEventArgs e)
    {
        try
        {
            if (!Settings.ExtendedChinesePinyin
                || e.Key is not (Key.Return or Key.Tab)
                || TextBoxField?.GetValue(__instance) is not TextBox textBox
                || !textBox.IsFocused
                || __instance.Note is not { } note
                || !string.Equals(textBox.Text, note.Lyric, StringComparison.Ordinal)
                || !ExtendedChinesePinyinResetHelper.NeedsManagedRepair(note)
                || note.Parent?.Sequence is not { } sequence)
            {
                return;
            }

            using var transaction = new Transaction(sequence);
            transaction.Result = note.SetLyricsAndResetPhonemes(textBox.Text);
            ExtendedPinyinDiagnosticLog.Write(
                "g2pa-repair",
                $"floating-input=true; result={transaction.Result}");
        }
        catch
        {
            // Keep VOCALOID's original Return/Tab handling path available.
        }
    }
}

public class ExtendedChinesePinyinRangeResetPatch : PatchBase
{
    public override string PatchName        => "ExtendedChinesePinyinRangeResetPatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "ResetPhonemes";
    public override Type[] ArgumentTypes    => new[] { typeof(WIVSMNote), typeof(WIVSMNote) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMNote? beginNote,
        WIVSMNote? endNote,
        out List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        __state = Settings.ExtendedChinesePinyin
            ? ExtendedChinesePinyinResetHelper.PrepareRange(beginNote, endNote)
            : new List<ExtendedChinesePinyinResetHelper.ProtectedNoteState>();
    }

    [HarmonyPostfix]
    private static void Postfix(List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
        return __exception;
    }
}

public class ExtendedChinesePinyinPartResetPatch : PatchBase
{
    public override string PatchName        => "ExtendedChinesePinyinPartResetPatch";
    public override Type TargetClass        => typeof(G2PAMultiLingualManager);
    public override string TargetMethodName => "ResetPhonemes";
    public override Type[] ArgumentTypes    => new[] { typeof(WIVSMMidiPart) };

    [HarmonyPrefix]
    private static void Prefix(
        WIVSMMidiPart? part,
        out List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        __state = Settings.ExtendedChinesePinyin
            ? ExtendedChinesePinyinResetHelper.PreparePart(part)
            : new List<ExtendedChinesePinyinResetHelper.ProtectedNoteState>();
    }

    [HarmonyPostfix]
    private static void Postfix(List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        List<ExtendedChinesePinyinResetHelper.ProtectedNoteState> __state)
    {
        ExtendedChinesePinyinResetHelper.RestoreProtection(__state);
        return __exception;
    }
}

internal static class ExtendedChinesePinyinResetHelper
{
    internal sealed record ProtectedNoteState(
        WIVSMNote Note,
        string Lyric,
        string ExpectedPhonemes,
        int ExpectedLangId,
        bool OriginalProtection);

    public static void ReleaseManagedProtection(WIVSMNote? note)
    {
        try
        {
            if (note?.IsProtected == true && IsManagedExtendedPhoneme(note))
                note.IsProtected = false;
        }
        catch
        {
        }
    }

    public static void EnsureManagedProtection(WIVSMNote? note)
    {
        try
        {
            if (note != null && !note.IsProtected && IsManagedExtendedPhoneme(note))
                note.IsProtected = true;
        }
        catch
        {
        }
    }

    public static void ProtectAppliedSyllables(
        WIVSMNote? beginNote,
        WIVSMNote? endNote,
        IReadOnlyList<ChinesePinyinSyllable> syllables)
    {
        int appliedCount = 0;
        int protectedCount = 0;
        for (WIVSMNote? note = beginNote;
             note != null && !note.Equals(endNote) && appliedCount < syllables.Count;
             note = note.Next)
        {
            try
            {
                if (syllables[appliedCount].RequiresOverride)
                {
                    note.IsProtected = true;
                    protectedCount++;
                }

                appliedCount++;
            }
            catch
            {
                break;
            }
        }

        if (protectedCount != 0)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "g2pa-protect",
                $"protected={protectedCount}; applied={appliedCount}; requested={syllables.Count}");
        }
    }

    public static List<ProtectedNoteState> CaptureAdjacentContext(
        WIVSMNote? note,
        int affectedNoteCount = 1)
    {
        var captured = new List<ProtectedNoteState>();
        if (note?.Prev != null)
            TryCaptureNote(note.Prev, captured);

        WIVSMNote? following = note;
        for (int i = 0; i < Math.Max(affectedNoteCount, 1) && following != null; i++)
            following = following.Next;
        if (following != null)
            TryCaptureNote(following, captured);
        return captured;
    }

    public static void DiscardAffectedContext(
        List<ProtectedNoteState>? captured,
        WIVSMNote? beginNote,
        WIVSMNote? endNote)
    {
        if (captured == null || captured.Count == 0 || beginNote == null)
            return;

        for (WIVSMNote? note = beginNote;
             note != null && !note.Equals(endNote);
             note = note.Next)
        {
            WIVSMNote affected = note;
            captured.RemoveAll(state => state.Note.Equals(affected));
        }
    }

    public static List<ProtectedNoteState> PrepareRange(WIVSMNote? beginNote, WIVSMNote? endNote)
    {
        var prepared = new List<ProtectedNoteState>();
        if (beginNote?.Prev != null)
            TryPrepareNote(beginNote.Prev, prepared);
        for (WIVSMNote? note = beginNote; note != null && !note.Equals(endNote); note = note.Next)
            TryPrepareNote(note, prepared);
        if (endNote != null)
            TryPrepareNote(endNote, prepared);
        return prepared;
    }

    public static List<ProtectedNoteState> PreparePart(WIVSMMidiPart? part)
    {
        var prepared = new List<ProtectedNoteState>();
        if (part == null)
            return prepared;

        foreach (var note in part.Notes)
            TryPrepareNote(note, prepared);
        return prepared;
    }

    public static void RestoreProtection(List<ProtectedNoteState>? prepared)
    {
        if (prepared == null)
            return;

        int restored = 0;
        int repaired = 0;
        foreach (ProtectedNoteState state in prepared)
        {
            try
            {
                WIVSMNote note = state.Note;
                note.IsProtected = false;
                if (!string.Equals(note.Lyric, state.Lyric, StringComparison.Ordinal))
                    continue;

                if (note.LangID != state.ExpectedLangId)
                {
                    note.SetLangID(state.ExpectedLangId);
                }

                if (!string.Equals(note.Phonemes, state.ExpectedPhonemes, StringComparison.Ordinal))
                {
                    if (!G2PAMultiLingualManager.SetPhonemes(note, state.ExpectedPhonemes)
                        && !note.SetPhonemes(state.ExpectedPhonemes, true, note.LangID))
                    {
                        continue;
                    }

                    repaired++;
                }

                restored++;
            }
            catch
            {
                // The reset result is more important than restoring a transient guard on a stale note.
            }
            finally
            {
                try
                {
                    state.Note.IsProtected = state.OriginalProtection
                                             || IsManagedExtendedPhoneme(state.Note);
                }
                catch
                {
                }
            }
        }

        if (prepared.Count != 0)
        {
            ExtendedPinyinDiagnosticLog.Write(
                "g2pa-guard",
                $"prepared={prepared.Count}; restored={restored}; repaired={repaired}");
        }
        prepared.Clear();
    }

    private static void TryPrepareNote(WIVSMNote note, ICollection<ProtectedNoteState> prepared)
    {
        try
        {
            if (note.IsProtected
                || !TryGetExpectedPhonemes(note, out string lyric, out string phonemes, out int expectedLangId))
            {
                return;
            }

            if (note.LangID != expectedLangId)
            {
                note.SetLangID(expectedLangId);
            }

            if (!G2PAMultiLingualManager.SetPhonemes(note, phonemes)
                && !note.SetPhonemes(phonemes, true, note.LangID))
            {
                return;
            }

            note.IsProtected = true;
            prepared.Add(new ProtectedNoteState(note, lyric, phonemes, expectedLangId, false));
        }
        catch
        {
            // Let VOCALOID keep its native reset result for this note.
        }
    }

    private static void TryCaptureNote(WIVSMNote note, ICollection<ProtectedNoteState> captured)
    {
        try
        {
            if (!TryGetExpectedPhonemes(note, out string lyric, out string phonemes, out int expectedLangId))
                return;

            captured.Add(new ProtectedNoteState(
                note,
                lyric,
                phonemes,
                expectedLangId,
                note.IsProtected && !IsManagedExtendedPhoneme(note)));
        }
        catch
        {
        }
    }

    private static bool TryGetExpectedPhonemes(
        WIVSMNote note,
        out string lyric,
        out string phonemes,
        out int expectedLangId)
    {
        lyric = note.Lyric;
        phonemes = string.Empty;
        expectedLangId = note.LangID;
        if (ChineseHanziG2paRecognizer.TryConvert(note, lyric, out var hanziSyllables)
            && hanziSyllables.Count == 1)
        {
            phonemes = hanziSyllables[0].Phonemes;
            expectedLangId = (int)VSMLanguageID.Chinese;
            return true;
        }

        if (!ChinesePinyinPhonemeConverter.TryConvertToken(lyric, out var syllable))
            return false;

        bool isSpecial = syllable.IsVocaloidSpecialPhoneme;
        if (!isSpecial)
        {
            if (!syllable.RequiresOverride)
            {
                var chineseManager = App.GetG2PAManager((int)VSMLanguageID.Chinese);
                if (chineseManager?.CanConvert(lyric, false, note.IsAi) == true
                    || chineseManager?.CanConvert(lyric, true, note.IsAi) == true)
                {
                    return false;
                }
            }

            expectedLangId = (int)VSMLanguageID.Chinese;
        }

        phonemes = syllable.Phonemes;
        return true;
    }

    private static bool IsManagedExtendedPhoneme(WIVSMNote note)
    {
        return TryGetExpectedPhonemes(
                   note,
                   out _,
                   out string expectedPhonemes,
                   out int expectedLangId)
               && note.LangID == expectedLangId
               && string.Equals(note.Phonemes, expectedPhonemes, StringComparison.Ordinal);
    }

    public static bool NeedsManagedRepair(WIVSMNote? note)
    {
        try
        {
            return note != null
                   && TryGetExpectedPhonemes(
                       note,
                       out _,
                       out string expectedPhonemes,
                       out int expectedLangId)
                   && (note.LangID != expectedLangId
                       || !string.Equals(note.Phonemes, expectedPhonemes, StringComparison.Ordinal)
                       || !note.IsProtected);
        }
        catch
        {
            return false;
        }
    }
}
