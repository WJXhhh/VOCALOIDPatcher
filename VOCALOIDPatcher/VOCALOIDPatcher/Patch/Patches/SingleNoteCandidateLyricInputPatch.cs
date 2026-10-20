using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Patch.Patches;

public class SingleNoteCandidateLyricInputPatch : PatchBase
{
    private static readonly FieldInfo? TextBoxField =
        AccessTools.Field(typeof(FloatingInputField), "xTextBox");

    private static readonly FieldInfo? ListBoxField =
        AccessTools.Field(typeof(FloatingInputField), "xListBox");

    private static readonly FieldInfo? SyllablesListField =
        AccessTools.Field(typeof(FloatingInputField), "syllablesList");

    private static readonly FieldInfo? MusicalEditorDivField =
        AccessTools.Field(typeof(MainWindow), "xMusicalEditorDiv");

    public override string PatchName        => nameof(SingleNoteCandidateLyricInputPatch);
    public override Type TargetClass        => typeof(FloatingInputField);
    public override string TargetMethodName => "OnPreviewMouseLeftButtonDown";
    public override Type[] ArgumentTypes    => new[] { typeof(object), typeof(MouseButtonEventArgs) };

    [HarmonyPrefix]
    private static bool Prefix(FloatingInputField __instance, object sender, MouseButtonEventArgs e)
    {
        if (!Settings.SingleNoteCandidateLyricInput)
            return true;

        try
        {
            if (sender is not ListBoxItem listBoxItem)
                return true;

            if (__instance.Note is not { } note)
                return true;

            var xListBox = ListBoxField?.GetValue(__instance) as ListBox;
            var syllablesList = SyllablesListField?.GetValue(__instance) as List<Syllables>;
            var textBox = TextBoxField?.GetValue(__instance) as TextBox;

            if (xListBox == null || syllablesList == null)
                return true;

            int selectedIndex = -1;
            for (int i = 0; i < xListBox.Items.Count; i++)
            {
                if (xListBox.Items[i]?.Equals(listBoxItem.Content) == true)
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (selectedIndex < 0 || selectedIndex >= syllablesList.Count)
                return true;

            xListBox.SelectedIndex = selectedIndex;
            Syllables candidate = syllablesList[selectedIndex];
            if (candidate.SyllableArgs == null || candidate.SyllableArgs.Count == 0)
                return true;

            MusicalEditorViewModel? dataContext = GetMusicalEditorViewModel(__instance);
            if (dataContext == null)
                return true;

            Yamaha.VOCALOID.Sequence? sequence = dataContext.Sequence;
            WIVSMSequence? vsmSequence = dataContext.VSMSequence;
            if (sequence == null || vsmSequence == null)
                return true;

            string fullPhonemes = string.Join(" ", candidate.SyllableArgs
                .Select(a => a.Phoneme?.Trim())
                .Where(p => !string.IsNullOrEmpty(p)));

            string inputText = textBox?.Text?
                .Replace("\r", " ", StringComparison.InvariantCulture)
                .Replace("\n", " ", StringComparison.InvariantCulture)
                .Trim() ?? string.Empty;

            string lyric = !string.IsNullOrEmpty(inputText)
                ? inputText
                : string.Join(" ", candidate.SyllableArgs
                    .Select(a => a.Syllable?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s)));

            if (string.IsNullOrEmpty(lyric))
                lyric = note.Lyric;

            if (lyric.Length > Yamaha.VOCALOID.Design.Sequence.maxLyricTextLength)
            {
                lyric = lyric.Substring(0, Yamaha.VOCALOID.Design.Sequence.maxLyricTextLength);
                MessageBoxDeliverer.GeneralWarning(string.Format(
                    CultureInfo.InvariantCulture,
                    Yamaha.VOCALOID.Properties.Resources.MsgBox_TooManyCharacters_Warning,
                    Yamaha.VOCALOID.Design.Sequence.maxLyricTextLength));
            }

            using (new SelectionNotifier(sequence))
            {
                using var transaction = new Transaction(vsmSequence);

                if (note.IsProtected)
                    note.IsProtected = false;

                bool applied = false;
                WIVSMNote? nextNote = note.Next;

                // 优先尝试以单音节 SyllablesData 形式写入，使底层 G2PA 上下文保持完整
                try
                {
                    using var syllablesData = new SyllablesData();
                    syllablesData.InitializeData(1);
                    using var syllableData = new SyllableData
                    {
                        syllable = lyric,
                        phonemes = fullPhonemes
                    };
                    syllablesData.SetSyllableData(syllableData, 0);

                    var tuple = G2PAMultiLingualManager.SetSyllables(note, syllablesData, 1, candidate.LangID);
                    if (tuple.IsSuccess)
                    {
                        applied = true;
                        nextNote = tuple.NextNote ?? note.Next;
                    }
                }
                catch
                {
                    applied = false;
                }

                // 如果底层 SetSyllables 拒绝带空格或特殊词项，回退为直接对音符设置 Lyric 和 Phonemes
                if (!applied)
                {
                    try
                    {
                        note.Lyric = lyric;
                        note.SetLangID(candidate.LangID);
                        if (note.SetPhonemes(fullPhonemes, true, candidate.LangID))
                        {
                            applied = true;
                            nextNote = note.Next;
                        }
                    }
                    catch
                    {
                        applied = false;
                    }
                }

                if (applied)
                {
                    // 保护当前音符的音标，防止带空格歌词后续被自动 G2PA 覆盖重写
                    note.IsProtected = true;
                    transaction.Result = note.ResetPhonemes(nextNote);
                    if (transaction.Result)
                    {
                        WIVSMMidiPart? parent = note.Parent;
                        if (parent != null)
                        {
                            WIVSMNote? managerTargetNote = parent.G2paManagerTargetNote;
                            if (managerTargetNote != null)
                            {
                                sequence.SelectNoteAndDeselectOtherNotes(parent, managerTargetNote);
                            }
                        }
                    }
                }
                else
                {
                    transaction.Result = false;
                    MessageBoxDeliverer.GeneralError(Yamaha.VOCALOID.Properties.Resources.MsgBox_Edit_Note_Error);
                }
            }

            dataContext.DoUpdateView(__instance, Yamaha.VOCALOID.MusicalEditor.UpdateViewTypeFlag.NoteSelectionChanged);
            __instance.Hide();
            e.Handled = true;
            return false;
        }
        catch (Exception ex)
        {
            Debug.Print($"SingleNoteCandidateLyricInputPatch error: {ex}");
            return true;
        }
    }

    private static MusicalEditorViewModel? GetMusicalEditorViewModel(FloatingInputField inputField)
    {
        if (inputField.DataContext is MusicalEditorViewModel vm)
            return vm;

        if (Application.Current?.MainWindow is MainWindow mainWindow)
        {
            if (MusicalEditorDivField?.GetValue(mainWindow) is FrameworkElement div &&
                div.DataContext is MusicalEditorViewModel divVm)
            {
                return divVm;
            }
        }

        return null;
    }
}
