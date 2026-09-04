using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VOCALOIDPatcher.Evec;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.PathResource;
using Yamaha.VOCALOID.Properties;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static class Program
{
    private const string EditorDirectory = @"C:\Program Files\VOCALOID6\Editor";

    private static readonly Dictionary<string, IntPtr> NativeHandles =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly RenderCase[] Cases =
    [
        new("miku_normal", "BCNFCY43LB2LZCD4", "k a"),
        new("miku_mild", "BCNFCY43LB2LZCD4", "k k#2 a"),
        new("miku_accent", "BCNFCY43LB2LZCD4", "k k#6 a"),
        new("miku_extension_1", "BCNFCY43LB2LZCD4", "k k a"),
        new("miku_accent_extension_2", "BCNFCY43LB2LZCD4", "k k k k#6 a"),
        new("miku_mild_release_short", "BCNFCY43LB2LZCD4", "k k#2 a a#2 *#1"),
        new("miku_accent_release_long", "BCNFCY43LB2LZCD4", "k k#6 a a#6 *#2"),
        new("rin_normal", "BKKP765AEHXWSKDB", "k a"),
        new("rin_attack", "BKKP765AEHXWSKDB", "k k a"),
        new("rin_extension_2", "BKKP765AEHXWSKDB", "k k k a"),
        new("rin_attack_extension_2", "BKKP765AEHXWSKDB", "k k k k a"),
        new("rin_attack_h_backslash", "BKKP765AEHXWSKDB", "h\\ h\\ M"),
        new("rin_attack_z", "BKKP765AEHXWSKDB", "z z i"),
        new("len_normal", "BKPLC6S7LH3RZKC8", "k a"),
        new("len_attack", "BKPLC6S7LH3RZKC8", "k k a"),
        new("len_extension_2", "BKPLC6S7LH3RZKC8", "k k k a"),
        new("len_attack_extension_2", "BKPLC6S7LH3RZKC8", "k k k k a"),
        new("len_attack_h_backslash", "BKPLC6S7LH3RZKC8", "h\\ h\\ M"),
        new("len_attack_z", "BKPLC6S7LH3RZKC8", "z z i"),
    ];

    private static int Main(string[] args)
    {
        bool consonantOffsetProbe = args.Contains(
            "--consonant-offset-probe",
            StringComparer.OrdinalIgnoreCase);
        bool voiceBankPathProbe = args.Contains(
            "--voicebank-paths",
            StringComparer.OrdinalIgnoreCase);
        bool mutationProbe = args.Contains(
            "--mutation-probe",
            StringComparer.OrdinalIgnoreCase);
        bool lyricsProbe = args.Contains(
            "--lyrics-probe",
            StringComparer.OrdinalIgnoreCase);
        bool clipboardProbe = args.Contains(
            "--clipboard-probe",
            StringComparer.OrdinalIgnoreCase);
        bool structureProbe = args.Contains(
            "--structure-probe",
            StringComparer.OrdinalIgnoreCase);
        bool voiceBankSwitchProbe = args.Contains(
            "--voicebank-switch-probe",
            StringComparer.OrdinalIgnoreCase);
        bool partPropertyProbe = args.Contains(
            "--part-property-probe",
            StringComparer.OrdinalIgnoreCase);
        bool lyricMoveProbe = args.Contains(
            "--lyric-move-probe",
            StringComparer.OrdinalIgnoreCase);
        bool partStructureProbe = args.Contains(
            "--part-structure-probe",
            StringComparer.OrdinalIgnoreCase);
        bool positionTimingProbe = args.Contains(
            "--position-timing-probe",
            StringComparer.OrdinalIgnoreCase);
        bool removalLifecycleProbe = args.Contains(
            "--removal-lifecycle-probe",
            StringComparer.OrdinalIgnoreCase);
        string[] positionalArguments = args
            .Where(item => !item.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
        string outputDirectory = positionalArguments.Length > 0
            ? Path.GetFullPath(positionalArguments[0])
            : Path.Combine(Path.GetTempPath(), $"v6patch-evec-render-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        if (!consonantOffsetProbe && !voiceBankPathProbe && !mutationProbe && !lyricsProbe &&
            !clipboardProbe && !structureProbe && !voiceBankSwitchProbe && !partPropertyProbe &&
            !lyricMoveProbe && !partStructureProbe && !positionTimingProbe &&
            !removalLifecycleProbe)
            Directory.CreateDirectory(outputDirectory);

        ConfigureNativeLoading();
        Console.WriteLine($"editor_directory={EditorDirectory}");
        Console.WriteLine($"expression_library={DirectoryPath.ExpressionLibrary}");
        if (!consonantOffsetProbe && !voiceBankPathProbe && !mutationProbe && !lyricsProbe &&
            !clipboardProbe && !structureProbe && !voiceBankSwitchProbe && !partPropertyProbe &&
            !lyricMoveProbe && !partStructureProbe && !positionTimingProbe &&
            !removalLifecycleProbe)
            Console.WriteLine($"output_directory={outputDirectory}");

        VDMError databaseResult = ~VDMError.None;
        using DatabaseManager? database = DatabaseManagerIF.CreateDatabaseManager(
            "VOCALOID6",
            DirectoryPath.ExpressionLibrary,
            ref databaseResult);
        Console.WriteLine($"database.result={databaseResult}");
        Console.WriteLine($"database.created={database != null}");
        if (database == null || databaseResult != VDMError.None)
        {
            return 1;
        }

        if (voiceBankPathProbe)
        {
            for (ulong index = 0; index < database.NumVoiceBanks; index++)
            {
                VoiceBank? voiceBank = database.GetVoiceBankByIndex(index);
                if (voiceBank == null ||
                    (!voiceBank.Name.Contains("_EVEC", StringComparison.OrdinalIgnoreCase) &&
                     !voiceBank.Name.Contains("LUKA_V4X", StringComparison.OrdinalIgnoreCase) &&
                     !voiceBank.Name.Contains("RIN_V4X", StringComparison.OrdinalIgnoreCase) &&
                     !voiceBank.Name.Contains("LEN_V4X", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Console.WriteLine($"voicebank.{voiceBank.CompID}.name={voiceBank.Name}");
                Console.WriteLine($"voicebank.{voiceBank.CompID}.path={voiceBank.Path}");
                EvecVoicebankCapabilities capabilities =
                    EvecVoicebankDetector.GetCapabilities(voiceBank);
                Console.WriteLine($"voicebank.{voiceBank.CompID}.evec_supported={capabilities.IsSupported}");
                Console.WriteLine($"voicebank.{voiceBank.CompID}.colors={string.Join(',', capabilities.Colors.Select(item => item.Id))}");
                Console.WriteLine($"voicebank.{voiceBank.CompID}.attacks={string.Join(',', capabilities.Attacks.Select(item => item.Id))}");
                Console.WriteLine($"voicebank.{voiceBank.CompID}.releases={string.Join(',', capabilities.Releases.Select(item => item.Id))}");
                if (capabilities.HasConsonantExtension)
                {
                    var normal = EvecNoteState.Empty.Clone();
                    var attack = EvecNoteState.Empty.Clone();
                    attack.AttackId = capabilities.Attacks.First(item =>
                        item.Id != EvecConstants.AttackNone).Id;
                    Console.WriteLine($"voicebank.{voiceBank.CompID}.extension_max_k_normal={capabilities.MaximumConsonantExtension("k a", normal)}");
                    Console.WriteLine($"voicebank.{voiceBank.CompID}.extension_max_h_backslash_normal={capabilities.MaximumConsonantExtension("h\\ M", normal)}");
                    Console.WriteLine($"voicebank.{voiceBank.CompID}.extension_max_h_backslash_attack={capabilities.MaximumConsonantExtension("h\\ M", attack)}");
                    Console.WriteLine($"voicebank.{voiceBank.CompID}.extension_max_z_normal={capabilities.MaximumConsonantExtension("z i", normal)}");
                    Console.WriteLine($"voicebank.{voiceBank.CompID}.extension_max_z_attack={capabilities.MaximumConsonantExtension("z i", attack)}");
                    VerifyInteractionPolicy(voiceBank.CompID, capabilities, attack.AttackId);
                }
            }

            return 0;
        }

        using DSEManager? dse = DSEManagerIF.CreateManager(database);
        Console.WriteLine($"dse.created={dse != null}");
        if (dse == null)
        {
            return 2;
        }

        HashSet<string> targetComponentIds = Cases
            .Select(item => item.ComponentId)
            .Append("B6D9CFB7-ECA7-4EB0-A740-7A88E375EA25")
            .ToHashSet(StringComparer.Ordinal);
        foreach (Yamaha.VOCALOID.DSE.License license in dse.GetLicenses()
                     .Where(item => targetComponentIds.Contains(item.CompID)))
        {
            Console.WriteLine($"license.{license.CompID}.name={license.CompName}");
            Console.WriteLine($"license.{license.CompID}.type={license.CompType}");
            Console.WriteLine($"license.{license.CompID}.result={license.Result}");
        }

        using WIVSMSequenceManager? sequenceManager = WVSMModuleIF.CreateManager("VOCALOID6", "6.13.0.1");
        Console.WriteLine($"vsm.created={sequenceManager != null}");
        if (sequenceManager == null)
        {
            Console.WriteLine($"vsm.module_error={WVSMModuleIF.LastError()}");
            return 3;
        }

        sequenceManager.SetDatabaseManager(database);
        sequenceManager.SetDSEManager(dse);
        sequenceManager.SetYvs(YVS.A91D77BC1F12);
        sequenceManager.SetMaxNumMidiTrack(32);
        sequenceManager.SetMaxNumAudioTrack(32);

        if (consonantOffsetProbe)
            return ProbeConsonantOffset(sequenceManager);
        if (mutationProbe)
            return ProbeEvecMutations(sequenceManager);
        if (lyricsProbe)
            return ProbeLyrics(sequenceManager);
        if (clipboardProbe)
            return ProbeClipboard(sequenceManager);
        if (structureProbe)
            return ProbeStructure(sequenceManager);
        if (voiceBankSwitchProbe)
            return ProbeVoiceBankSwitch(sequenceManager);
        if (partPropertyProbe)
            return ProbePartProperty(sequenceManager);
        if (lyricMoveProbe)
            return ProbeLyricMove(sequenceManager);
        if (partStructureProbe)
            return ProbePartStructure(sequenceManager);
        if (positionTimingProbe)
            return ProbePositionTiming(sequenceManager);
        if (removalLifecycleProbe)
            return ProbeRemovalLifecycle(sequenceManager);

        string? caseFilter = positionalArguments.Length > 1 ? positionalArguments[1] : null;
        RenderCase[] selectedCases = string.IsNullOrWhiteSpace(caseFilter)
            ? Cases
            : Cases.Where(item => item.Name.Contains(caseFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        Console.WriteLine($"case_filter={caseFilter ?? "<all>"}");

        int failed = 0;
        foreach (RenderCase testCase in selectedCases)
        {
            if (!Render(sequenceManager, outputDirectory, testCase))
            {
                failed++;
            }
        }

        Console.WriteLine($"summary.total={selectedCases.Length}");
        Console.WriteLine($"summary.failed={failed}");
        return failed == 0 ? 0 : 4;
    }

    private static void VerifyInteractionPolicy(
        string componentId,
        EvecVoicebankCapabilities capabilities,
        int attackId)
    {
        var attack = EvecNoteState.Empty.Clone();
        attack.AttackId = attackId;

        int selectable = capabilities.MaximumSelectableConsonantExtension("h\\ M");
        if (selectable == 1 &&
            capabilities.MaximumConsonantExtension("h\\ M", attack) == 0)
        {
            EvecNoteState extensionWins = capabilities.SelectConsonantExtension("h\\ M", attack, 1);
            if (extensionWins.AttackId != EvecConstants.AttackNone ||
                extensionWins.ConsonantExtension != 1)
            {
                throw new InvalidOperationException(
                    $"{componentId}: extension selection did not clear the conflicting CTop.");
            }

            extensionWins.AttackId = attackId;
            EvecNoteState attackWins = capabilities.Normalize("h\\ M", extensionWins);
            if (attackWins.AttackId != attackId || attackWins.ConsonantExtension != 0)
            {
                throw new InvalidOperationException(
                    $"{componentId}: CTop selection did not reduce the conflicting extension.");
            }
        }
        else
        {
            EvecNoteState combined = capabilities.SelectConsonantExtension("k a", attack, 2);
            if (combined.AttackId != attackId || combined.ConsonantExtension != 2)
            {
                throw new InvalidOperationException(
                    $"{componentId}: compatible CTop and extension were not preserved.");
            }
        }

        Console.WriteLine($"voicebank.{componentId}.interaction_policy=passed");
    }

    private static int ProbeEvecMutations(WIVSMSequenceManager sequenceManager)
    {
        bool miku = ProbeEvecMutationCase(
            sequenceManager,
            "miku",
            "BCNFCY43LB2LZCD4",
            [
                EvecNoteState.Empty.Clone(),
                new EvecNoteState(0, EvecConstants.AttackMild, 0),
                new EvecNoteState(0, EvecConstants.AttackAccent, 0),
                EvecNoteState.Empty.Clone(),
                new EvecNoteState(0, EvecConstants.AttackAccent, 0, 2),
                new EvecNoteState(EvecConstants.VoiceColorSoft, EvecConstants.AttackMild,
                    EvecConstants.ReleaseBreathShort, 1),
                EvecNoteState.Empty.Clone(),
            ]);
        bool rin = ProbeEvecMutationCase(
            sequenceManager,
            "rin",
            "BKKP765AEHXWSKDB",
            [
                EvecNoteState.Empty.Clone(),
                new EvecNoteState(0, EvecConstants.AttackAccentPlain, 0),
                EvecNoteState.Empty.Clone(),
                new EvecNoteState(0, 0, 0, 1),
                new EvecNoteState(0, EvecConstants.AttackAccentPlain, 0),
                EvecNoteState.Empty.Clone(),
            ]);

        Console.WriteLine($"mutation_probe.valid={miku && rin}");
        return miku && rin ? 0 : 8;
    }

    private static bool ProbeEvecMutationCase(
        WIVSMSequenceManager sequenceManager,
        string name,
        string componentId,
        IReadOnlyList<EvecNoteState> states)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 32,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return false;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(0, VSMTrackType.Midi, $"EVEC {name} mutation")
                as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                new VSMAbsTick(0),
                new VSMRelTick(1920),
                $"EVEC {name} mutation");
            bool voiceSet = part?.SetVoiceBankID(componentId) == true;
            VSMNoteExpression noteExpression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiNoteExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? note = voiceSet
                ? part?.InsertNote(
                    new VSMRelTick(240),
                    new VSMNoteEvent(960, 60, 64),
                    noteExpression,
                    aiNoteExpression,
                    "か",
                    "k a",
                    true,
                    0)
                : null;
            bool committed = note != null && sequence.Commit(false);
            if (!initialized || !committed || note == null)
                return false;

            bool passed = true;
            for (int index = 0; index < states.Count; index++)
            {
                EvecNoteState state = states[index];
                string expected = EvecPhonemeRecomposer.Recompose(note.Phonemes, state);
                bool written;
                using (var transaction = new Transaction(sequence) { Result = false })
                {
                    note.IsProtected = false;
                    written = note.SetPhonemes(expected, true, note.LangID) &&
                              string.Equals(note.Phonemes, expected, StringComparison.Ordinal);
                    note.IsProtected = state.HasAnyEvec;
                    transaction.Result = written;
                }

                bool stepPassed = written &&
                                  string.Equals(note.Phonemes, expected, StringComparison.Ordinal) &&
                                  note.IsProtected == state.HasAnyEvec;
                passed &= stepPassed;
                Console.WriteLine(
                    $"mutation_probe.{name}.{index}=state:{state.VoiceColorId}/" +
                    $"{state.AttackId}/{state.ReleaseId}/{state.ConsonantExtension};" +
                    $"phonemes:{note.Phonemes};protected:{note.IsProtected};passed:{stepPassed}");
            }

            return passed;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"mutation_probe.{name}.exception={exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            Console.WriteLine($"mutation_probe.{name}.sequence_closed={sequence.Close()}");
        }
    }

    private static int ProbeLyrics(WIVSMSequenceManager sequenceManager)
    {
        using var managerFactory = new G2PAManagerIF();
        using G2PAManager? g2pa = managerFactory.CreateManager(
            G2PAManagerLangID.JPN,
            EditorDirectory);
        Console.WriteLine($"lyrics_probe.g2pa_created={g2pa != null}");
        if (g2pa == null)
            return 9;

        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 10;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(0, VSMTrackType.Midi, "EVEC lyrics probe")
                as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                new VSMAbsTick(0),
                new VSMRelTick(1920),
                "EVEC lyrics probe");
            bool voiceSet = part?.SetVoiceBankID("BCNFCY43LB2LZCD4") == true;
            VSMNoteExpression noteExpression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiNoteExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? note = voiceSet
                ? part?.InsertNote(
                    new VSMRelTick(240),
                    new VSMNoteEvent(960, 60, 64),
                    noteExpression,
                    aiNoteExpression,
                    "か",
                    "k k#2 a",
                    true,
                    0)
                : null;
            if (!initialized || note == null || !sequence.Commit(false))
                return 11;

            note.IsProtected = true;
            bool protectedResult = g2pa.SetLyrics(
                note.CppObjPtr,
                "き",
                false,
                false,
                out int protectedLength);
            string protectedPhonemes = note.Phonemes;
            Console.WriteLine(
                $"lyrics_probe.protected=result:{protectedResult};length:{protectedLength};" +
                $"phonemes:{protectedPhonemes};protected:{note.IsProtected}");
            sequence.Rollback();

            note.IsProtected = false;
            bool unlockedResult = g2pa.SetLyrics(
                note.CppObjPtr,
                "き",
                false,
                false,
                out int unlockedLength);
            string generatedBase = note.Phonemes;
            var mild = new EvecNoteState(0, EvecConstants.AttackMild, 0);
            string recomposed = EvecPhonemeRecomposer.Recompose(generatedBase, mild);
            bool reapplied = unlockedResult &&
                             note.SetPhonemes(recomposed, true, note.LangID) &&
                             string.Equals(note.Phonemes, recomposed, StringComparison.Ordinal);
            note.IsProtected = reapplied;
            bool passed = protectedResult && unlockedResult && reapplied &&
                          string.Equals(protectedPhonemes, "k k#2 a", StringComparison.Ordinal) &&
                          !string.Equals(generatedBase, protectedPhonemes, StringComparison.Ordinal);
            Console.WriteLine(
                $"lyrics_probe.unlocked=result:{unlockedResult};length:{unlockedLength};" +
                $"base:{generatedBase};recomposed:{note.Phonemes};protected:{note.IsProtected}");
            Console.WriteLine($"lyrics_probe.valid={passed}");
            return passed ? 0 : 12;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"lyrics_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 13;
        }
        finally
        {
            Console.WriteLine($"lyrics_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static int ProbeClipboard(WIVSMSequenceManager sequenceManager)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 12;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(0, VSMTrackType.Midi, "EVEC clipboard probe")
                as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                new VSMAbsTick(0),
                new VSMRelTick(1920),
                "EVEC clipboard probe");
            bool voiceSet = part?.SetVoiceBankID("BKKP765AEHXWSKDB") == true;
            VSMNoteExpression noteExpression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiNoteExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? source = voiceSet
                ? part?.InsertNote(
                    new VSMRelTick(240),
                    new VSMNoteEvent(480, 60, 64),
                    noteExpression,
                    aiNoteExpression,
                    "か",
                    "k k a",
                    true,
                    0)
                : null;
            WIVSMNote? target = voiceSet
                ? part?.InsertNote(
                    new VSMRelTick(960),
                    new VSMNoteEvent(480, 62, 64),
                    noteExpression,
                    aiNoteExpression,
                    "き",
                    "k' i",
                    true,
                    0)
                : null;
            if (!initialized || source == null || target == null || !sequence.Commit(false))
                return 13;

            source.IsProtected = true;
            using WIVSMClipboard? clipboard = sequenceManager.CreateClipboard(sequence);
            WIVSMNote? pushed = clipboard?.PushNote(source);
            WIVSMNote? enumerated = clipboard?.GetNotes.FirstOrDefault();
            string pushedParent = pushed?.Parent?.GetType().FullName ?? "<null>";
            string enumeratedParent = enumerated?.Parent?.GetType().FullName ?? "<null>";
            string pushedVoiceBankId = (pushed?.Parent as WIVSMMidiPart)?.VoiceBankID ?? "<null>";
            Console.WriteLine($"clipboard_probe.pushed={pushed != null}");
            Console.WriteLine($"clipboard_probe.pushed_parent={pushedParent}");
            Console.WriteLine($"clipboard_probe.enumerated_parent={enumeratedParent}");
            Console.WriteLine($"clipboard_probe.pushed_parent_is_source_parent={pushed?.Parent?.Equals(source.Parent)}");
            Console.WriteLine($"clipboard_probe.pushed_voicebank_id={pushedVoiceBankId}");
            Console.WriteLine($"clipboard_probe.same_handle={pushed?.CppObjPtr == enumerated?.CppObjPtr}");

            bool copied;
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                copied = clipboard?.CopyNotePropertyTo(
                    new[] { target },
                    NoteProperty.LyricsAndPhonemes) == true;
                transaction.Result = copied;
            }

            bool valid = pushed != null &&
                         enumerated != null &&
                         pushed.Parent is WIVSMMidiPart pushedPart &&
                         enumerated.Parent is WIVSMMidiPart &&
                         pushedPart.VoiceBankID == part?.VoiceBankID &&
                         pushed.CppObjPtr == enumerated.CppObjPtr &&
                         copied &&
                         target.Phonemes == source.Phonemes &&
                         target.IsProtected == source.IsProtected;
            Console.WriteLine($"clipboard_probe.target_phonemes={target.Phonemes}");
            Console.WriteLine($"clipboard_probe.target_protected={target.IsProtected}");
            bool parsed = EvecPhonemeRecomposer.TryParseEvecFromPhonemes(
                target.Phonemes,
                out EvecNoteState parsedState,
                out _);
            parsedState = EvecPhonemeRecomposer.ResolvePlainAttackAmbiguity(
                target.Phonemes,
                parsedState,
                EvecConstants.AttackAccentPlain);
            Console.WriteLine(
                $"clipboard_probe.naive_state={parsedState.AttackId}/" +
                $"{parsedState.ConsonantExtension}");
            bool ambiguityConfirmed = parsed &&
                                      parsedState.AttackId == EvecConstants.AttackAccentPlain &&
                                      parsedState.ConsonantExtension == 0;
            Console.WriteLine($"clipboard_probe.ambiguity_confirmed={ambiguityConfirmed}");
            Console.WriteLine($"clipboard_probe.valid={valid && ambiguityConfirmed}");
            return valid && ambiguityConfirmed ? 0 : 14;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"clipboard_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 15;
        }
        finally
        {
            Console.WriteLine($"clipboard_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static int ProbeStructure(WIVSMSequenceManager sequenceManager)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 16;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(0, VSMTrackType.Midi, "EVEC structure probe")
                as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                new VSMAbsTick(0),
                new VSMRelTick(2400),
                "EVEC structure probe");
            bool voiceSet = part?.SetVoiceBankID("BKKP765AEHXWSKDB") == true;
            VSMNoteExpression noteExpression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiNoteExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? left = voiceSet
                ? part?.InsertNote(
                    new VSMRelTick(240),
                    new VSMNoteEvent(960, 60, 64),
                    noteExpression,
                    aiNoteExpression,
                    "か",
                    "k k a",
                    true,
                    0)
                : null;
            if (!initialized || left == null || !sequence.Commit(false))
                return 17;

            left.IsProtected = true;
            IntPtr originalHandle = left.CppObjPtr;
            int[] originalPositions = left.GetPhonemePositions().ToArray();
            WIVSMNote? right;
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                right = part?.DivideNote(left, new VSMRelTick(720));
                transaction.Result = right != null;
            }

            Console.WriteLine($"structure_probe.divide_created={right != null}");
            Console.WriteLine($"structure_probe.divide_left_handle_preserved={left.CppObjPtr == originalHandle}");
            Console.WriteLine($"structure_probe.divide_left={left.Phonemes};{left.IsProtected};{string.Join(',', left.GetPhonemePositions())}");
            Console.WriteLine($"structure_probe.divide_right={right?.Phonemes};{right?.IsProtected};{string.Join(',', right?.GetPhonemePositions() ?? [])}");
            Console.WriteLine($"structure_probe.divide_original_positions={string.Join(',', originalPositions)}");
            if (right == null)
                return 18;

            bool joined;
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                joined = part?.JoinNotes(new List<WIVSMNote> { left, right }) == true;
                transaction.Result = joined;
            }

            WIVSMNote[] remaining = part?.Notes.ToArray() ?? [];
            Console.WriteLine($"structure_probe.joined={joined}");
            Console.WriteLine($"structure_probe.remaining_count={remaining.Length}");
            foreach (WIVSMNote note in remaining)
            {
                Console.WriteLine(
                    $"structure_probe.remaining={note.CppObjPtr};{note.Phonemes};" +
                    $"{note.IsProtected};{note.RelPosTick.Value};{note.DurationTick.Value};" +
                    $"{string.Join(',', note.GetPhonemePositions())}");
            }

            bool valid = right.Phonemes == left.Phonemes &&
                         right.IsProtected == left.IsProtected &&
                         joined &&
                         remaining.Length == 1 &&
                         remaining[0].CppObjPtr == originalHandle &&
                         remaining[0].Phonemes == "k k a";
            Console.WriteLine($"structure_probe.valid={valid}");
            return valid ? 0 : 19;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"structure_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 20;
        }
        finally
        {
            Console.WriteLine($"structure_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static int ProbeVoiceBankSwitch(WIVSMSequenceManager sequenceManager)
    {
        using var managerFactory = new G2PAManagerIF();
        using G2PAManager? g2pa = managerFactory.CreateManager(
            G2PAManagerLangID.JPN,
            EditorDirectory);
        Console.WriteLine($"voicebank_switch_probe.g2pa_created={g2pa != null}");
        if (g2pa == null)
            return 21;

        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 22;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(
                0,
                VSMTrackType.Midi,
                "EVEC voice-bank switch probe") as WIVSMMidiTrack;
            WIVSMMidiPart? protectedPart = CreateVoiceBankSwitchPart(
                track,
                0,
                "protected");
            WIVSMMidiPart? unlockedPart = CreateVoiceBankSwitchPart(
                track,
                2400,
                "unlocked");
            WIVSMNote? protectedNote = protectedPart?.GetNote(0);
            WIVSMNote? unlockedNote = unlockedPart?.GetNote(0);
            if (!initialized || protectedNote == null || unlockedNote == null ||
                !sequence.Commit(false))
            {
                return 23;
            }

            protectedNote.IsProtected = true;
            unlockedNote.IsProtected = false;
            const string rinPowerId = "BKKP765AEHXWSKDB";
            bool protectedVoiceSet = protectedPart?.SetVoiceBankID(rinPowerId) == true;
            bool unlockedVoiceSet = unlockedPart?.SetVoiceBankID(rinPowerId) == true;
            bool protectedReset = protectedPart != null &&
                                  g2pa.ResetPhonemes((IntPtr)protectedPart, false);
            bool unlockedReset = unlockedPart != null &&
                                 g2pa.ResetPhonemes((IntPtr)unlockedPart, false);

            Console.WriteLine(
                $"voicebank_switch_probe.protected=voice_set:{protectedVoiceSet};" +
                $"reset:{protectedReset};phonemes:{protectedNote.Phonemes};" +
                $"protected:{protectedNote.IsProtected}");
            Console.WriteLine(
                $"voicebank_switch_probe.unlocked=voice_set:{unlockedVoiceSet};" +
                $"reset:{unlockedReset};phonemes:{unlockedNote.Phonemes};" +
                $"protected:{unlockedNote.IsProtected}");

            bool protectedSkipped = string.Equals(
                protectedNote.Phonemes,
                "k k#2 a",
                StringComparison.Ordinal);
            bool unlockedRegenerated = !string.Equals(
                unlockedNote.Phonemes,
                "k k#2 a",
                StringComparison.Ordinal) &&
                string.Equals(unlockedNote.Phonemes, "k a", StringComparison.Ordinal);
            bool valid = protectedVoiceSet && unlockedVoiceSet &&
                         protectedReset && unlockedReset &&
                         protectedSkipped && unlockedRegenerated;
            Console.WriteLine($"voicebank_switch_probe.protected_skipped={protectedSkipped}");
            Console.WriteLine($"voicebank_switch_probe.unlocked_regenerated={unlockedRegenerated}");
            Console.WriteLine($"voicebank_switch_probe.valid={valid}");
            return valid ? 0 : 24;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"voicebank_switch_probe.exception={exception.GetType().Name}: " +
                exception.Message);
            return 25;
        }
        finally
        {
            Console.WriteLine($"voicebank_switch_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static WIVSMMidiPart? CreateVoiceBankSwitchPart(
        WIVSMMidiTrack? track,
        long position,
        string name)
    {
        WIVSMMidiPart? part = track?.InsertPart(
            new VSMAbsTick(position),
            new VSMRelTick(1920),
            name);
        if (part?.SetVoiceBankID("BCNFCY43LB2LZCD4") != true)
            return null;

        VSMNoteExpression noteExpression = part.GetDefaultNoteExpression();
        VSMAiNoteExpression aiNoteExpression = part.GetDefaultAiNoteExpression();
        WIVSMNote? note = part.InsertNote(
            new VSMRelTick(240),
            new VSMNoteEvent(960, 60, 64),
            noteExpression,
            aiNoteExpression,
            "か",
            "k k#2 a",
            true,
            0);
        return note == null ? null : part;
    }

    private static int ProbeLyricMove(WIVSMSequenceManager sequenceManager)
    {
        using var managerFactory = new G2PAManagerIF();
        using G2PAManager? g2pa = managerFactory.CreateManager(
            G2PAManagerLangID.JPN,
            EditorDirectory);
        Console.WriteLine($"lyric_move_probe.g2pa_created={g2pa != null}");
        if (g2pa == null)
            return 31;

        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 2,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 32;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(
                0,
                VSMTrackType.Midi,
                "EVEC lyric move probe") as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                VSMAbsTick.Zero,
                new VSMRelTick(2400),
                "Rin lyric shift");
            bool voiceSet = part?.SetVoiceBankID("BKKP765AEHXWSKDB") == true;
            VSMNoteExpression expression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? target = part?.InsertNote(
                new VSMRelTick(240),
                new VSMNoteEvent(720, 60, 64),
                expression,
                aiExpression,
                "か",
                "k a",
                true,
                0);
            WIVSMNote? source = part?.InsertNote(
                new VSMRelTick(1200),
                new VSMNoteEvent(720, 62, 64),
                expression,
                aiExpression,
                "き",
                "k k a",
                true,
                0);
            if (!initialized || !voiceSet || target == null || source == null ||
                !sequence.Commit(false))
            {
                return 33;
            }

            var sourceLogical = new EvecNoteState(
                EvecConstants.VoiceColorNone,
                EvecConstants.AttackNone,
                EvecConstants.ReleaseNone,
                1);
            source.IsProtected = true;
            target.IsProtected = false;
            target.Lyric = source.Lyric;
            bool copied = target.SetPhonemes(
                source.Phonemes,
                source.IsValidPhonemes,
                source.LangID);
            target.IsProtected = source.IsProtected;
            bool reset = part != null && g2pa.ResetPhonemes((IntPtr)part, false);

            bool parsed = EvecPhonemeRecomposer.TryParseEvecFromPhonemes(
                target.Phonemes,
                out var detected,
                out _);
            var fallback = EvecPhonemeRecomposer.ResolvePlainAttackAmbiguity(
                target.Phonemes,
                detected,
                EvecConstants.AttackAccentPlain);
            bool physicalPreserved = target.Phonemes == "k k a" && target.IsProtected;
            bool naiveChangedMeaning = parsed &&
                                       fallback.AttackId == EvecConstants.AttackAccentPlain &&
                                       fallback.ConsonantExtension == 0;
            bool sidecarKeepsMeaning =
                EvecPhonemeRecomposer.IsExactRealization(target.Phonemes, sourceLogical) &&
                sourceLogical.AttackId == EvecConstants.AttackNone &&
                sourceLogical.ConsonantExtension == 1;
            bool valid = copied && reset && physicalPreserved &&
                         naiveChangedMeaning && sidecarKeepsMeaning;

            Console.WriteLine(
                $"lyric_move_probe.native=copied:{copied};reset:{reset};" +
                $"phonemes:{target.Phonemes};protected:{target.IsProtected}");
            Console.WriteLine(
                $"lyric_move_probe.naive=attack:{fallback.AttackId};" +
                $"extension:{fallback.ConsonantExtension}");
            Console.WriteLine($"lyric_move_probe.physical_preserved={physicalPreserved}");
            Console.WriteLine($"lyric_move_probe.naive_changed_meaning={naiveChangedMeaning}");
            Console.WriteLine($"lyric_move_probe.sidecar_keeps_meaning={sidecarKeepsMeaning}");
            Console.WriteLine($"lyric_move_probe.valid={valid}");
            return valid ? 0 : 34;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"lyric_move_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 35;
        }
        finally
        {
            Console.WriteLine($"lyric_move_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static int ProbePartStructure(WIVSMSequenceManager sequenceManager)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 2,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 36;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(
                0,
                VSMTrackType.Midi,
                "EVEC Part structure probe") as WIVSMMidiTrack;
            WIVSMMidiPart? left = track?.InsertPart(
                VSMAbsTick.Zero,
                new VSMRelTick(3840),
                "Rin Part");
            bool voiceSet = left?.SetVoiceBankID("BKKP765AEHXWSKDB") == true;
            VSMNoteExpression expression = left?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiExpression = left?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? first = left?.InsertNote(
                new VSMRelTick(240),
                new VSMNoteEvent(720, 60, 64),
                expression,
                aiExpression,
                "か",
                "k k a",
                true,
                0);
            WIVSMNote? second = left?.InsertNote(
                new VSMRelTick(2160),
                new VSMNoteEvent(720, 62, 64),
                expression,
                aiExpression,
                "き",
                "k k a",
                true,
                0);
            if (!initialized || !voiceSet || first == null || second == null ||
                !sequence.Commit(false) || track == null || left == null)
            {
                return 37;
            }

            first.IsProtected = true;
            second.IsProtected = true;
            IntPtr firstHandle = first.CppObjPtr;
            IntPtr secondHandle = second.CppObjPtr;
            WIVSMMidiPart? right = track.DividePart(new VSMRelTick(1920), left);
            WIVSMNote? dividedLeft = left.GetNote(0);
            WIVSMNote? dividedRight = right?.GetNote(0);
            bool divideValid = right != null && dividedLeft != null && dividedRight != null &&
                               dividedLeft.Phonemes == "k k a" && dividedLeft.IsProtected &&
                               dividedRight.Phonemes == "k k a" && dividedRight.IsProtected;

            Console.WriteLine(
                $"part_structure_probe.before={firstHandle},{secondHandle}");
            Console.WriteLine(
                $"part_structure_probe.divide=left:{dividedLeft?.CppObjPtr};" +
                $"right:{dividedRight?.CppObjPtr};valid:{divideValid}");

            WIVSMMidiPart? joined = right == null
                ? null
                : track.JoinParts(new[] { left, right });
            IntPtr[] joinedHandles = joined?.Notes.Select(note => note.CppObjPtr).ToArray() ?? [];
            bool joinPhysicalValid = joined != null && joined.NumNotes == 2 &&
                                     joined.Notes.All(note =>
                                         note.Phonemes == "k k a" && note.IsProtected);
            bool dividePreservesHandles = dividedLeft?.CppObjPtr == firstHandle &&
                                          dividedRight?.CppObjPtr == secondHandle;
            bool joinPreservesHandles = joinedHandles.SequenceEqual(
                new[] { firstHandle, secondHandle });
            bool valid = divideValid && joinPhysicalValid &&
                         !dividePreservesHandles && !joinPreservesHandles;

            Console.WriteLine(
                $"part_structure_probe.join={string.Join(',', joinedHandles)};" +
                $"physical_valid:{joinPhysicalValid}");
            Console.WriteLine(
                $"part_structure_probe.divide_preserves_handles={dividePreservesHandles}");
            Console.WriteLine(
                $"part_structure_probe.join_preserves_handles={joinPreservesHandles}");

            WIVSMMidiPart? crossingPart = track.InsertPart(
                new VSMAbsTick(4800),
                new VSMRelTick(3840),
                "Rin crossing-note Part");
            bool crossingVoiceSet = crossingPart?.SetVoiceBankID("BKKP765AEHXWSKDB") == true;
            WIVSMNote? crossingSource = crossingPart?.InsertNote(
                new VSMRelTick(1440),
                new VSMNoteEvent(1440, 64, 64),
                expression,
                aiExpression,
                "く",
                "k k a",
                true,
                0);
            if (!crossingVoiceSet || crossingSource == null || !sequence.Commit(false))
                return 38;

            crossingSource.IsProtected = true;
            IntPtr crossingSourceHandle = crossingSource.CppObjPtr;
            WIVSMMidiPart? crossingRight = track.DividePart(
                new VSMRelTick(1920),
                crossingPart!);
            WIVSMNote[] crossingLeftNotes = crossingPart?.Notes.ToArray() ?? [];
            WIVSMNote[] crossingRightNotes = crossingRight?.Notes.ToArray() ?? [];
            bool crossingValid = crossingRight != null &&
                                 crossingLeftNotes.Length == 1 &&
                                 crossingRightNotes.Length == 0 &&
                                 crossingLeftNotes[0].CppObjPtr == crossingSourceHandle &&
                                 crossingLeftNotes[0].Phonemes == "k k a" &&
                                 crossingLeftNotes[0].IsProtected;
            Console.WriteLine(
                $"part_structure_probe.crossing.source={crossingSourceHandle};" +
                $"left_count={crossingLeftNotes.Length};right_count={crossingRightNotes.Length}");
            Console.WriteLine(
                $"part_structure_probe.crossing.left={DescribeNotes(crossingLeftNotes)}");
            Console.WriteLine(
                $"part_structure_probe.crossing.right={DescribeNotes(crossingRightNotes)}");

            valid &= crossingValid;
            Console.WriteLine($"part_structure_probe.crossing.valid={crossingValid}");
            Console.WriteLine($"part_structure_probe.valid={valid}");
            return valid ? 0 : 38;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"part_structure_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 39;
        }
        finally
        {
            Console.WriteLine($"part_structure_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static string DescribeNotes(IEnumerable<WIVSMNote> notes) => string.Join(
        '|',
        notes.Select(note =>
            $"{note.CppObjPtr}@{note.AbsPosTick.Value}+{note.DurationTick.Value}:" +
            $"{note.Phonemes}:protected={note.IsProtected}"));

    private static int ProbeRemovalLifecycle(WIVSMSequenceManager sequenceManager)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 2,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 44;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(
                0,
                VSMTrackType.Midi,
                "EVEC removal lifecycle probe") as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                VSMAbsTick.Zero,
                new VSMRelTick(3840),
                "Rin removal Part");
            bool voiceSet = part?.SetVoiceBankID("BKKP765AEHXWSKDB") == true;
            VSMNoteExpression expression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? first = part?.InsertNote(
                new VSMRelTick(240),
                new VSMNoteEvent(720, 60, 64),
                expression,
                aiExpression,
                "か",
                "k k a",
                true,
                0);
            WIVSMNote? second = part?.InsertNote(
                new VSMRelTick(1200),
                new VSMNoteEvent(720, 62, 64),
                expression,
                aiExpression,
                "き",
                "k k a",
                true,
                0);
            if (!initialized || !voiceSet || first == null || second == null ||
                track == null || part == null || !sequence.Commit(false))
            {
                return 45;
            }

            first.IsProtected = true;
            second.IsProtected = true;
            IntPtr[] originalHandles = [first.CppObjPtr, second.CppObjPtr];

            bool partRemoved;
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                partRemoved = track.RemovePart(part);
                transaction.Result = partRemoved;
            }
            bool partUndo = sequence.CanUndo();
            if (partUndo)
                sequence.Undo();
            WIVSMMidiPart? restoredPart = track.GetPart(0);
            IntPtr[] partUndoHandles = restoredPart?.Notes
                .Select(note => note.CppObjPtr)
                .ToArray() ?? [];
            bool partHandlesRestored = partUndo &&
                                       partUndoHandles.SequenceEqual(originalHandles);
            bool partRedo = sequence.CanRedo();
            if (partRedo)
                sequence.Redo();
            bool partRedoRemoved = partRedo && track.NumParts == 0;
            bool partRestoreForTrackTest = sequence.CanUndo();
            if (partRestoreForTrackTest)
                sequence.Undo();

            bool trackRemoved;
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                trackRemoved = sequence.RemoveTrack(track);
                transaction.Result = trackRemoved;
            }
            bool trackUndo = sequence.CanUndo();
            if (trackUndo)
                sequence.Undo();
            WIVSMMidiTrack? restoredTrack = sequence.GetTrack(0) as WIVSMMidiTrack;
            WIVSMMidiPart? trackUndoPart = restoredTrack?.GetPart(0);
            IntPtr[] trackUndoHandles = trackUndoPart?.Notes
                .Select(note => note.CppObjPtr)
                .ToArray() ?? [];
            bool trackHandlesRestored = trackUndo &&
                                        trackUndoHandles.SequenceEqual(originalHandles);
            bool trackRedo = sequence.CanRedo();
            if (trackRedo)
                sequence.Redo();
            bool trackRedoRemoved = trackRedo && sequence.NumTrack == 0;

            bool valid = partRemoved && partHandlesRestored && partRedoRemoved &&
                         partRestoreForTrackTest && trackRemoved && trackHandlesRestored &&
                         trackRedoRemoved;
            Console.WriteLine(
                $"removal_lifecycle_probe.original={string.Join(',', originalHandles)}");
            Console.WriteLine(
                $"removal_lifecycle_probe.part=removed:{partRemoved};undo:{partUndo};" +
                $"handles:{string.Join(',', partUndoHandles)};same:{partHandlesRestored};" +
                $"redo_removed:{partRedoRemoved}");
            Console.WriteLine(
                $"removal_lifecycle_probe.track=removed:{trackRemoved};undo:{trackUndo};" +
                $"handles:{string.Join(',', trackUndoHandles)};same:{trackHandlesRestored};" +
                $"redo_removed:{trackRedoRemoved}");
            Console.WriteLine($"removal_lifecycle_probe.valid={valid}");
            return valid ? 0 : 46;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"removal_lifecycle_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 47;
        }
        finally
        {
            Console.WriteLine($"removal_lifecycle_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static int ProbePositionTiming(WIVSMSequenceManager sequenceManager)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 1,
            MaxUndoCount = 4,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 40;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, 12000) != null &&
                               sequence.InsertTempo(new VSMRelTick(1920), 6000) != null &&
                               sequence.Commit(false);
            if (!initialized)
                return 41;

            const double requestedSeconds = 0.060;
            var fastOrigin = new VSMAbsTick(240);
            var slowOrigin = new VSMAbsTick(2160);
            long fastTicks = sequence.GetTickFromTime(fastOrigin, requestedSeconds).Value -
                             fastOrigin.Value;
            long slowTicks = sequence.GetTickFromTime(slowOrigin, requestedSeconds).Value -
                             slowOrigin.Value;
            double fastMilliseconds = sequence.GetTimeFromTick(
                fastOrigin,
                new VSMAbsTick(fastOrigin.Value + fastTicks)) * 1000.0;
            double slowCorrectedMilliseconds = sequence.GetTimeFromTick(
                slowOrigin,
                new VSMAbsTick(slowOrigin.Value + slowTicks)) * 1000.0;
            double slowStaleMilliseconds = sequence.GetTimeFromTick(
                slowOrigin,
                new VSMAbsTick(slowOrigin.Value + fastTicks)) * 1000.0;

            bool tickCountChanges = fastTicks != slowTicks;
            bool correctedIsFixed = Math.Abs(fastMilliseconds - 60.0) < 1.0 &&
                                    Math.Abs(slowCorrectedMilliseconds - 60.0) < 2.0;
            bool staleDrifts = Math.Abs(slowStaleMilliseconds - 60.0) > 20.0;
            bool valid = tickCountChanges && correctedIsFixed && staleDrifts;
            Console.WriteLine(
                $"position_timing_probe.ticks=fast:{fastTicks};slow:{slowTicks}");
            Console.WriteLine(
                $"position_timing_probe.ms=fast:{fastMilliseconds:F3};" +
                $"slow_corrected:{slowCorrectedMilliseconds:F3};" +
                $"slow_stale:{slowStaleMilliseconds:F3}");
            Console.WriteLine($"position_timing_probe.tick_count_changes={tickCountChanges}");
            Console.WriteLine($"position_timing_probe.corrected_is_fixed={correctedIsFixed}");
            Console.WriteLine($"position_timing_probe.stale_drifts={staleDrifts}");
            Console.WriteLine($"position_timing_probe.valid={valid}");
            return valid ? 0 : 42;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"position_timing_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 43;
        }
        finally
        {
            Console.WriteLine($"position_timing_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static int ProbePartProperty(WIVSMSequenceManager sequenceManager)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
            return 26;

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(
                0,
                VSMTrackType.Midi,
                "EVEC part-property probe") as WIVSMMidiTrack;
            WIVSMMidiPart? source = CreateProbePart(
                track,
                0,
                "BKKP765AEHXWSKDB",
                "source",
                "k k a");
            WIVSMMidiPart? voiceOnlyTarget = CreateProbePart(
                track,
                2400,
                "BCNFCY43LB2LZCD4",
                "voice-only target",
                "k k#2 a");
            WIVSMMidiPart? noteAndVoiceTarget = CreateProbePart(
                track,
                4800,
                "BCNFCY43LB2LZCD4",
                "note-and-voice target",
                "k k#2 a");
            WIVSMNote? sourceNote = source?.GetNote(0);
            WIVSMNote? voiceOnlyNote = voiceOnlyTarget?.GetNote(0);
            WIVSMNote? noteAndVoiceNote = noteAndVoiceTarget?.GetNote(0);
            if (!initialized || sourceNote == null || voiceOnlyNote == null ||
                noteAndVoiceNote == null || !sequence.Commit(false))
            {
                return 27;
            }

            sourceNote.IsProtected = true;
            voiceOnlyNote.IsProtected = true;
            noteAndVoiceNote.IsProtected = true;
            IntPtr noteAndVoiceBeforeHandle = noteAndVoiceNote.CppObjPtr;
            using WIVSMClipboard? clipboard = sequenceManager.CreateClipboard(sequence);
            WIVSMMidiPart? pushed = source == null ? null : clipboard?.PushMidiPart(source);
            WIVSMNote? pushedNote = pushed?.GetNote(0);

            bool voiceOnlyCopied;
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                voiceOnlyCopied = clipboard?.CopyPartPropertyTo(
                    new WIVSMPart[] { voiceOnlyTarget! },
                    PartProperty.VoiceBank) == true;
                transaction.Result = voiceOnlyCopied;
            }

            bool noteAndVoiceCopied;
            using (var transaction = new Transaction(sequence) { Result = false })
            {
                noteAndVoiceCopied = clipboard?.CopyPartPropertyTo(
                    new WIVSMPart[] { noteAndVoiceTarget! },
                    PartProperty.Note | PartProperty.VoiceBank) == true;
                transaction.Result = noteAndVoiceCopied;
            }

            WIVSMNote? copiedNote = noteAndVoiceTarget?.GetNote(0);
            IntPtr noteAndVoiceAfterHandle = copiedNote?.CppObjPtr ?? IntPtr.Zero;
            Console.WriteLine(
                $"part_property_probe.pushed=voice:{pushed?.VoiceBankID};" +
                $"phonemes:{pushedNote?.Phonemes};protected:{pushedNote?.IsProtected}");
            Console.WriteLine(
                $"part_property_probe.voice_only=result:{voiceOnlyCopied};" +
                $"voice:{voiceOnlyTarget?.VoiceBankID};phonemes:{voiceOnlyNote.Phonemes};" +
                $"protected:{voiceOnlyNote.IsProtected}");
            Console.WriteLine(
                $"part_property_probe.note_and_voice=result:{noteAndVoiceCopied};" +
                $"voice:{noteAndVoiceTarget?.VoiceBankID};phonemes:{copiedNote?.Phonemes};" +
                $"protected:{copiedNote?.IsProtected}");

            bool pushedPreserved = pushed?.VoiceBankID == "BKKP765AEHXWSKDB" &&
                                   pushedNote?.Phonemes == "k k a" &&
                                   pushedNote.IsProtected;
            bool voiceOnlyLeavesOldTokens = voiceOnlyCopied &&
                                             voiceOnlyTarget?.VoiceBankID == "BKKP765AEHXWSKDB" &&
                                             voiceOnlyNote.Phonemes == "k k#2 a" &&
                                             voiceOnlyNote.IsProtected;
            bool noteAndVoiceCopiesPhysical = noteAndVoiceCopied &&
                                              noteAndVoiceTarget?.VoiceBankID == "BKKP765AEHXWSKDB" &&
                                              copiedNote?.Phonemes == "k k a" &&
                                              copiedNote.IsProtected;
            bool undo = sequence.CanUndo();
            if (undo)
                sequence.Undo();
            WIVSMNote? undoNote = noteAndVoiceTarget?.GetNote(0);
            bool undoRestoresBeforeHandle = undo &&
                                            undoNote?.CppObjPtr == noteAndVoiceBeforeHandle &&
                                            undoNote.Phonemes == "k k#2 a";
            bool redo = sequence.CanRedo();
            if (redo)
                sequence.Redo();
            WIVSMNote? redoNote = noteAndVoiceTarget?.GetNote(0);
            bool redoRestoresAfterHandle = redo &&
                                           redoNote?.CppObjPtr == noteAndVoiceAfterHandle &&
                                           redoNote.Phonemes == "k k a";
            bool valid = pushedPreserved && voiceOnlyLeavesOldTokens &&
                         noteAndVoiceCopiesPhysical && undoRestoresBeforeHandle &&
                         redoRestoresAfterHandle;
            Console.WriteLine($"part_property_probe.pushed_preserved={pushedPreserved}");
            Console.WriteLine($"part_property_probe.voice_only_leaves_old_tokens={voiceOnlyLeavesOldTokens}");
            Console.WriteLine($"part_property_probe.note_and_voice_copies_physical={noteAndVoiceCopiesPhysical}");
            Console.WriteLine(
                $"part_property_probe.undo=success:{undo};" +
                $"handle:{undoNote?.CppObjPtr};phonemes:{undoNote?.Phonemes};" +
                $"restores_before_handle:{undoRestoresBeforeHandle}");
            Console.WriteLine(
                $"part_property_probe.redo=success:{redo};" +
                $"handle:{redoNote?.CppObjPtr};phonemes:{redoNote?.Phonemes};" +
                $"restores_after_handle:{redoRestoresAfterHandle}");
            Console.WriteLine($"part_property_probe.valid={valid}");
            return valid ? 0 : 28;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"part_property_probe.exception={exception.GetType().Name}: " +
                exception.Message);
            return 29;
        }
        finally
        {
            Console.WriteLine($"part_property_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static WIVSMMidiPart? CreateProbePart(
        WIVSMMidiTrack? track,
        long position,
        string voiceBankId,
        string name,
        string phonemes)
    {
        WIVSMMidiPart? part = track?.InsertPart(
            new VSMAbsTick(position),
            new VSMRelTick(1920),
            name);
        if (part?.SetVoiceBankID(voiceBankId) != true)
            return null;

        WIVSMNote? note = part.InsertNote(
            new VSMRelTick(240),
            new VSMNoteEvent(960, 60, 64),
            part.GetDefaultNoteExpression(),
            part.GetDefaultAiNoteExpression(),
            "か",
            phonemes,
            true,
            0);
        return note == null ? null : part;
    }

    private static int ProbeConsonantOffset(WIVSMSequenceManager sequenceManager)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 16,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
        {
            Console.WriteLine("offset_probe.create_sequence=false");
            return 5;
        }

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(0, VSMTrackType.Midi, "Consonant offset probe")
                as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                new VSMAbsTick(0),
                new VSMRelTick(1920),
                "Consonant offset probe");
            bool voiceSet = part?.SetVoiceBankID("BCNFCY43LB2LZCD4") == true;
            VSMNoteExpression noteExpression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiNoteExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? note = voiceSet
                ? part?.InsertNote(
                    new VSMRelTick(240),
                    new VSMNoteEvent(960, 60, 64),
                    noteExpression,
                    aiNoteExpression,
                    "か",
                    "k a",
                    true,
                    0)
                : null;
            bool committed = note != null && sequence.Commit(false);
            Console.WriteLine($"offset_probe.initialized={initialized}");
            Console.WriteLine($"offset_probe.voice_set={voiceSet}");
            Console.WriteLine($"offset_probe.note_inserted={note != null}");
            Console.WriteLine($"offset_probe.committed={committed}");
            if (!initialized || !committed || note == null)
                return 6;

            PrintNoteVirtualFunction("consonant_offset_getter", note, 0x2A8);
            PrintNoteVirtualFunction("consonant_offset_setter", note, 0x2B0);
            PrintOffsetSnapshot("baseline", sequence, note);
            int[] requestedOffsets = [-1000, -100, -10, -1, 0, 1, 10, 100, 1000];
            foreach (int requested in requestedOffsets)
            {
                note.ConsonantOffset = requested;
                PrintOffsetSnapshot($"set_{requested}", sequence, note);
                sequence.Rollback();
                PrintOffsetSnapshot($"rollback_{requested}", sequence, note);
            }

            note.ConsonantOffset = 100;
            bool offsetCommitted = sequence.Commit();
            PrintOffsetSnapshot("commit_100", sequence, note);
            bool canUndo = sequence.CanUndo();
            if (canUndo)
                sequence.Undo();
            PrintOffsetSnapshot("undo_100", sequence, note);
            bool canRedo = sequence.CanRedo();
            if (canRedo)
                sequence.Redo();
            PrintOffsetSnapshot("redo_100", sequence, note);
            Console.WriteLine($"offset_probe.offset_commit={offsetCommitted}");
            Console.WriteLine($"offset_probe.can_undo={canUndo}");
            Console.WriteLine($"offset_probe.can_redo={canRedo}");
            Console.WriteLine("offset_probe.valid=True");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"offset_probe.exception={exception.GetType().Name}: {exception.Message}");
            return 7;
        }
        finally
        {
            Console.WriteLine($"offset_probe.sequence_closed={sequence.Close()}");
        }
    }

    private static void PrintOffsetSnapshot(
        string name,
        WIVSMSequence sequence,
        WIVSMNote note)
    {
        Console.WriteLine(
            $"offset_probe.{name}=value:{note.ConsonantOffset};" +
            $"staged:{sequence.IsStaged};" +
            $"positions:[{string.Join(',', note.GetPhonemePositions())}];" +
            $"original:[{string.Join(',', note.GetOriginalPhonemePositions())}]");
    }

    private static void PrintNoteVirtualFunction(
        string name,
        WIVSMNote note,
        int vtableOffset)
    {
        IntPtr objectPointer = (IntPtr)note;
        IntPtr vtable = Marshal.ReadIntPtr(objectPointer);
        IntPtr function = Marshal.ReadIntPtr(vtable, vtableOffset);
        IntPtr moduleBase = NativeHandles["vsm"];
        long rva = function.ToInt64() - moduleBase.ToInt64();
        Console.WriteLine(
            $"offset_probe.{name}=vtable+0x{vtableOffset:X};rva:0x{rva:X};" +
            $"absolute:0x{function.ToInt64():X}");
    }

    private static bool Render(
        WIVSMSequenceManager sequenceManager,
        string outputDirectory,
        RenderCase testCase)
    {
        VSMSequenceData sequenceData = new()
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 32,
            MaxUndoCount = 0,
        };
        WIVSMSequence? sequence = sequenceManager.CreateSequence(sequenceData);
        if (sequence == null)
        {
            Console.WriteLine($"case.{testCase.Name}.create_sequence=false");
            Console.WriteLine($"case.{testCase.Name}.manager_error={sequenceManager.LastError}");
            return false;
        }

        try
        {
            bool initialized = sequence.InterpolatedMasterVolume() &&
                               sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) != null &&
                               sequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) != null &&
                               sequence.Commit(false);
            WIVSMMidiTrack? track = sequence.InsertTrackEx(0, VSMTrackType.Midi, "EVEC probe")
                as WIVSMMidiTrack;
            WIVSMMidiPart? part = track?.InsertPart(
                new VSMAbsTick(0),
                new VSMRelTick(1920),
                testCase.Name);
            bool voiceSet = part?.SetVoiceBankID(testCase.ComponentId) == true;
            VSMNoteExpression noteExpression = part?.GetDefaultNoteExpression() ?? default;
            VSMAiNoteExpression aiNoteExpression = part?.GetDefaultAiNoteExpression() ?? default;
            WIVSMNote? note = voiceSet
                ? part?.InsertNote(
                    new VSMRelTick(240),
                    new VSMNoteEvent(960, 60, 64),
                    noteExpression,
                    aiNoteExpression,
                    "か",
                    testCase.Phonemes,
                    true,
                    0)
                : null;
            bool hmmReset = note?.ResetHmmWeightDefault() == true;
            bool vibratoReset = note?.ResetAiVibratoDefault() == true;

            string outputPath = Path.Combine(outputDirectory, testCase.Name + ".wav");
            bool committed = note != null && hmmReset && vibratoReset && sequence.Commit(false);
            VSMResult renderResult = !initialized || !committed || part == null
                ? sequence.LastError
                : part.Render(outputPath);
            using VSMScoreList? scoreList = part?.HoldingScoreList ?? part?.RenderingScoreList;
            WaveMetrics? metrics = renderResult == VSMResult.NoError && File.Exists(outputPath)
                ? ReadWaveMetrics(outputPath)
                : null;
            bool passed = voiceSet && note != null && renderResult == VSMResult.NoError &&
                          metrics is { DataBytes: > 0, Peak: > 0.000001 };

            Console.WriteLine($"case.{testCase.Name}.phonemes={testCase.Phonemes}");
            Console.WriteLine($"case.{testCase.Name}.sequence_initialized={initialized}");
            Console.WriteLine($"case.{testCase.Name}.voice_set={voiceSet}");
            Console.WriteLine($"case.{testCase.Name}.part_lang_id={part?.LangID}");
            Console.WriteLine($"case.{testCase.Name}.note_inserted={note != null}");
            Console.WriteLine($"case.{testCase.Name}.note_phonemes={note?.Phonemes}");
            Console.WriteLine($"case.{testCase.Name}.note_valid_phonemes={note?.IsValidPhonemes}");
            Console.WriteLine($"case.{testCase.Name}.hmm_reset={hmmReset}");
            Console.WriteLine($"case.{testCase.Name}.hmm_weights={note?.PreHmmWeight}/{note?.PostHmmWeight}/{note?.HmmWeightDuration}");
            Console.WriteLine($"case.{testCase.Name}.vibrato_reset={vibratoReset}");
            Console.WriteLine($"case.{testCase.Name}.committed={committed}");
            Console.WriteLine($"case.{testCase.Name}.render_result={renderResult}");
            Console.WriteLine($"case.{testCase.Name}.sequence_error={sequence.LastError}");
            PrintScoreSummary(testCase.Name, scoreList);
            Console.WriteLine($"case.{testCase.Name}.file_exists={File.Exists(outputPath)}");
            if (metrics != null)
            {
                Console.WriteLine($"case.{testCase.Name}.format={metrics.FormatTag}/{metrics.BitsPerSample}bit/{metrics.Channels}ch/{metrics.SampleRate}Hz");
                Console.WriteLine($"case.{testCase.Name}.frames={metrics.Frames}");
                Console.WriteLine($"case.{testCase.Name}.data_bytes={metrics.DataBytes}");
                Console.WriteLine($"case.{testCase.Name}.peak={metrics.Peak:R}");
                Console.WriteLine($"case.{testCase.Name}.rms={metrics.Rms:R}");
                Console.WriteLine($"case.{testCase.Name}.sha256={metrics.Sha256}");
            }
            Console.WriteLine($"case.{testCase.Name}.passed={passed}");
            return passed;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"case.{testCase.Name}.exception={exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            Console.WriteLine($"case.{testCase.Name}.sequence_closed={sequence.Close()}");
        }
    }

    private static void PrintScoreSummary(string caseName, VSMScoreList? scores)
    {
        long frameCount = scores?.NumScores ?? 0;
        Console.WriteLine($"case.{caseName}.score_available={scores != null}");
        Console.WriteLine($"case.{caseName}.score_frames={frameCount}");
        if (scores == null || frameCount <= 0)
            return;

        const long maximumScannedFrames = 1_000_000;
        const int maximumPrintedRuns = 64;
        long scanCount = Math.Min(frameCount, maximumScannedFrames);
        long nonZeroFrames = 0;
        long runStart = 0;
        int runCount = 0;
        VSMPhoneme? previous = null;
        var printedRuns = new List<string>();

        for (long index = 0; index < scanCount; index++)
        {
            VSMPhoneme current = scores.ScoreAtIndex(index).PhnDur;
            if (!current.IsZero)
                nonZeroFrames++;

            if (previous is not VSMPhoneme prior || SamePhoneme(prior, current))
            {
                previous = current;
                continue;
            }

            AddScoreRun(printedRuns, maximumPrintedRuns, runStart, index, prior);
            runCount++;
            runStart = index;
            previous = current;
        }

        if (previous is VSMPhoneme final)
        {
            AddScoreRun(printedRuns, maximumPrintedRuns, runStart, scanCount, final);
            runCount++;
        }

        Console.WriteLine($"case.{caseName}.score_scanned_frames={scanCount}");
        Console.WriteLine($"case.{caseName}.score_scan_truncated={scanCount != frameCount}");
        Console.WriteLine($"case.{caseName}.score_nonzero_frames={nonZeroFrames}");
        Console.WriteLine($"case.{caseName}.score_runs={runCount}");
        Console.WriteLine($"case.{caseName}.score_run_data={string.Join('|', printedRuns)}");
        Console.WriteLine($"case.{caseName}.score_runs_truncated={runCount > maximumPrintedRuns}");
    }

    private static bool SamePhoneme(VSMPhoneme left, VSMPhoneme right) =>
        left.FwIdx == right.FwIdx &&
        left.BwIdx == right.BwIdx &&
        left.LeftDur == right.LeftDur &&
        left.RightDur == right.RightDur &&
        left.FromPhU == right.FromPhU &&
        left.ToPhU == right.ToPhU;

    private static void AddScoreRun(
        List<string> runs,
        int maximumRuns,
        long start,
        long end,
        VSMPhoneme value)
    {
        if (runs.Count >= maximumRuns)
            return;

        runs.Add(
            $"{start}-{end}:fw={value.FwIdx},bw={value.BwIdx}," +
            $"left={value.LeftDur},right={value.RightDur}," +
            $"from=0x{value.FromPhU.ToInt64():X},to=0x{value.ToPhU.ToInt64():X}");
    }

    private static WaveMetrics ReadWaveMetrics(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long chunkEnd = checked(stream.Position + chunkSize);
            if (chunkEnd > stream.Length)
            {
                throw new InvalidDataException("Truncated WAVE chunk.");
            }

            if (chunkId == "fmt ")
            {
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(checked((int)chunkSize));
            }

            stream.Position = chunkEnd + (chunkSize & 1);
        }

        if (data == null || blockAlign == 0 || channels == 0)
        {
            throw new InvalidDataException("WAVE format or data chunk is missing.");
        }

        double peak = 0;
        double sumSquares = 0;
        long samples = 0;
        int bytesPerSample = bitsPerSample / 8;
        for (int offset = 0; offset + bytesPerSample <= data.Length; offset += bytesPerSample)
        {
            double sample = ReadSample(data, offset, formatTag, bitsPerSample);
            peak = Math.Max(peak, Math.Abs(sample));
            sumSquares += sample * sample;
            samples++;
        }

        return new WaveMetrics(
            formatTag,
            bitsPerSample,
            channels,
            sampleRate,
            data.Length / blockAlign,
            data.Length,
            peak,
            samples > 0 ? Math.Sqrt(sumSquares / samples) : 0,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static double ReadSample(byte[] data, int offset, ushort formatTag, ushort bitsPerSample)
    {
        if (formatTag == 3 && bitsPerSample == 32)
        {
            return BitConverter.ToSingle(data, offset);
        }
        if (formatTag != 1)
        {
            throw new InvalidDataException($"Unsupported WAVE format {formatTag}/{bitsPerSample}.");
        }

        return bitsPerSample switch
        {
            8 => (data[offset] - 128) / 128.0,
            16 => BitConverter.ToInt16(data, offset) / 32768.0,
            24 => ReadInt24(data, offset) / 8388608.0,
            32 => BitConverter.ToInt32(data, offset) / 2147483648.0,
            _ => throw new InvalidDataException($"Unsupported PCM depth {bitsPerSample}.")
        };
    }

    private static int ReadInt24(byte[] data, int offset)
    {
        int value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static void ConfigureNativeLoading()
    {
        IntPtr cookie = NativeMethods.AddDllDirectory(EditorDirectory);
        if (cookie == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not add the VOCALOID Editor DLL directory.");
        }

        NativeLibrary.SetDllImportResolver(
            typeof(DatabaseManagerIF).Assembly,
            (libraryName, _, _) => ResolveEditorLibrary(libraryName));
    }

    private static IntPtr ResolveEditorLibrary(string libraryName)
    {
        string baseName = Path.GetFileNameWithoutExtension(libraryName);
        if (!baseName.Equals("vdm", StringComparison.OrdinalIgnoreCase) &&
            !baseName.Equals("dse", StringComparison.OrdinalIgnoreCase) &&
            !baseName.Equals("vsm", StringComparison.OrdinalIgnoreCase) &&
            !baseName.Equals("g2pamanager", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        if (NativeHandles.TryGetValue(baseName, out IntPtr handle))
        {
            return handle;
        }

        handle = NativeLibrary.Load(Path.Combine(EditorDirectory, baseName.ToUpperInvariant() + ".dll"));
        NativeHandles.Add(baseName, handle);
        return handle;
    }

    private sealed record RenderCase(string Name, string ComponentId, string Phonemes);

    private sealed record WaveMetrics(
        ushort FormatTag,
        ushort BitsPerSample,
        ushort Channels,
        uint SampleRate,
        long Frames,
        int DataBytes,
        double Peak,
        double Rms,
        string Sha256);

    private static class NativeMethods
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr AddDllDirectory(string newDirectory);
    }
}
