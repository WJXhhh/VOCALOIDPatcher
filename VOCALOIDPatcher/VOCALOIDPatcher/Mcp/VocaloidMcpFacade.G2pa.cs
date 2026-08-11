using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private const int MaximumG2paLyricLength = 32;
    private const int MaximumG2paSyllables = 64;
    private const int MaximumG2paTextLength = 256;

    private static object G2paCandidates(JsonElement arguments)
    {
        (string projectId, long revision) = ValidateProject(arguments);
        (_, WIVSMSequence vsm) = Context();
        int trackIndex = Int(arguments, "track_index", -1);
        int partIndex = Int(arguments, "part_index", -1);
        int noteIndex = Int(arguments, "note_index", -1);
        WIVSMNote note = Note(vsm, trackIndex, partIndex, noteIndex);
        string lyrics = RequiredG2paText(arguments, "lyrics", MaximumG2paLyricLength);

        List<Syllables> candidates;
        if (Element(arguments, "language_id") != null)
        {
            int languageId = G2paLanguage(arguments, "language_id");
            bool useExtensionDictionary = Bool(arguments, "use_extension_dictionary");
            G2PAManager manager = App.GetG2PAManager(languageId)
                                  ?? throw Fault("v6_unavailable", "The requested G2PA language module is not loaded.", true);
            List<List<SyllableArgs>> nativeCandidates = manager.CandidatePhonemes(
                (IntPtr)note,
                lyrics,
                useExtensionDictionary,
                note.IsAi) ?? new List<List<SyllableArgs>>();
            candidates = nativeCandidates.Select(items => new Syllables(languageId, items)).ToList();
        }
        else
        {
            if (Element(arguments, "use_extension_dictionary") != null)
                throw Fault("invalid_request", "language_id is required when use_extension_dictionary is supplied.");
            candidates = note.CandidatePhonemes(lyrics);
        }

        return new
        {
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            note = new EntityRef(projectId, revision, "note", trackIndex, partIndex, noteIndex),
            is_ai = note.IsAi,
            note_language_id = note.LangID,
            candidates = candidates.Select((candidate, index) => new
            {
                index,
                language_id = candidate.LangID,
                data_size = candidate.SyllableArgs.Count,
                phonemes = string.Join(" ", candidate.SyllableArgs.Select(item => item.Phoneme)),
                syllables = candidate.SyllableArgs.Select(item => new
                {
                    syllable = item.Syllable,
                    phoneme = item.Phoneme,
                }).ToArray(),
            }).ToArray(),
        };
    }

    private static object ApplyG2pa(BridgeClientInfo client, JsonElement arguments)
    {
        (string projectId, long previousRevision) = ValidateProject(arguments);
        (_, WIVSMSequence vsm) = Context();
        string action = (String(arguments, "action") ?? throw Fault("invalid_request", "action is required.")).ToLowerInvariant();
        bool dryRun = Bool(arguments, "dry_run");
        int trackIndex = Int(arguments, "track_index", -1);
        int partIndex = Int(arguments, "part_index", -1);
        int noteIndex = Int(arguments, "note_index", -1);
        WIVSMMidiPart part = MidiPart(vsm, trackIndex, partIndex);
        WIVSMNote note = Note(vsm, trackIndex, partIndex, noteIndex);

        ValidateG2paApply(action, arguments, part);
        if (!dryRun && !McpAccessController.AuthorizeWrite(client, $"Apply G2PA operation: {action}", false, out BridgeError? error))
            throw Fault(error!);

        bool success = true;
        WIVSMNote? nextNote = null;
        if (!dryRun)
        {
            using var transaction = new Transaction(vsm);
            switch (action)
            {
                case "set_lyrics":
                {
                    string lyrics = RequiredG2paText(arguments, "lyrics", MaximumG2paLyricLength);
                    success = Element(arguments, "language_id") == null
                        ? note.SetLyricsAndResetPhonemes(lyrics)
                        : note.SetLyricsAndResetPhonemes(lyrics, G2paLanguage(arguments, "language_id"));
                    break;
                }
                case "set_phonemes":
                    success = note.SetPhonemes(RequiredG2paText(arguments, "phonemes", MaximumG2paTextLength));
                    break;
                case "set_syllables":
                {
                    int languageId = G2paLanguage(arguments, "language_id");
                    List<SyllableArgs> values = G2paSyllables(arguments);
                    using var data = new SyllablesData();
                    var nativeItems = new List<SyllableData>(values.Count);
                    try
                    {
                        data.InitializeData(values.Count);
                        for (int index = 0; index < values.Count; index++)
                        {
                            var native = new SyllableData
                            {
                                syllable = values[index].Syllable,
                                phonemes = values[index].Phoneme,
                            };
                            nativeItems.Add(native);
                            data.SetSyllableData(native, index);
                        }
                        (success, nextNote) = note.SetSyllables(data, data.DataSize(), languageId);
                        if (success && Bool(arguments, "reset_phonemes", true))
                            success = note.ResetPhonemes(nextNote);
                    }
                    finally
                    {
                        foreach (SyllableData native in nativeItems)
                            native.Dispose();
                    }
                    break;
                }
                case "reset":
                {
                    int endIndex = Int(arguments, "end_note_index", -1);
                    WIVSMNote? endNote = endIndex < 0 ? null : Note(vsm, trackIndex, partIndex, endIndex);
                    success = note.ResetPhonemes(endNote);
                    break;
                }
            }

            transaction.Result = success;
            if (!success)
                throw Fault("operation_failed", $"VOCALOID rejected the G2PA operation '{action}'.");
        }

        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        if (!dryRun)
            RefreshEditor();
        return new
        {
            dry_run = dryRun,
            valid = true,
            action,
            is_ai = note.IsAi,
            note_language_id = note.LangID,
            next_note_index = nextNote == null ? (int?)null : IndexOf(part.Notes, nextNote),
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
        };
    }

    private static void ValidateG2paApply(string action, JsonElement arguments, WIVSMMidiPart part)
    {
        switch (action)
        {
            case "set_lyrics":
                RequiredG2paText(arguments, "lyrics", MaximumG2paLyricLength);
                if (Element(arguments, "language_id") != null)
                    G2paLanguage(arguments, "language_id");
                break;
            case "set_phonemes":
                RequiredG2paText(arguments, "phonemes", MaximumG2paTextLength);
                break;
            case "set_syllables":
                G2paLanguage(arguments, "language_id");
                G2paSyllables(arguments);
                break;
            case "reset":
            {
                int endIndex = Int(arguments, "end_note_index", -1);
                if (endIndex >= part.Notes.Count)
                    throw Fault("invalid_reference", "end_note_index is out of range.");
                break;
            }
            default:
                throw Fault("invalid_request", "action must be set_lyrics, set_phonemes, set_syllables, or reset.");
        }
    }

    private static int G2paLanguage(JsonElement arguments, string name)
    {
        int languageId = Int(arguments, name, -1);
        if (languageId is < 0 or > 4)
            throw Fault("invalid_request", $"{name} must be a language ID from 0 through 4.");
        return languageId;
    }

    private static string RequiredG2paText(JsonElement arguments, string name, int maximumLength)
    {
        string value = String(arguments, name)?.Trim()
                       ?? throw Fault("invalid_request", $"{name} is required.");
        if (value.Length == 0 || value.Length > maximumLength)
            throw Fault("invalid_request", $"{name} must contain between 1 and {maximumLength} characters.");
        return value;
    }

    private static List<SyllableArgs> G2paSyllables(JsonElement arguments)
    {
        JsonElement values = Element(arguments, "syllables")
                             ?? throw Fault("invalid_request", "syllables is required.");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > MaximumG2paSyllables)
            throw Fault("invalid_request", $"syllables must be an array containing 1 through {MaximumG2paSyllables} items.");

        var result = new List<SyllableArgs>(values.GetArrayLength());
        foreach (JsonElement value in values.EnumerateArray())
        {
            string syllable = RequiredG2paText(value, "syllable", MaximumG2paTextLength);
            string phoneme = RequiredG2paText(value, "phoneme", MaximumG2paTextLength);
            result.Add(new SyllableArgs(syllable, phoneme));
        }
        return result;
    }
}
