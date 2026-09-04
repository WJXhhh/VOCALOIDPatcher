using VOCALOIDPatcher.Evec;
using System.IO.Compression;

internal static class Program
{
    private static int Main()
    {
        try
        {
            Verify("miku-mild", "k a", State(attack: EvecConstants.AttackMild), "k k#2 a");
            Verify("miku-accent", "k a", State(attack: EvecConstants.AttackAccent), "k k#6 a");
            VerifyAmbiguous(
                "rin-len-accent",
                "k a",
                State(attack: EvecConstants.AttackAccentPlain),
                "k k a");
            VerifyPlainAttackFallback("k k a", attack: EvecConstants.AttackAccentPlain, extension: 0);
            VerifyPlainAttackFallback("k k k a", attack: EvecConstants.AttackAccentPlain, extension: 1);
            VerifyPlainAttackFallback("k k k k k a", attack: EvecConstants.AttackAccentPlain, extension: 3);
            Verify(
                "combined",
                "k a",
                State(EvecConstants.VoiceColorPower, EvecConstants.AttackAccent, EvecConstants.ReleaseBreathLong),
                "k k#6 a a#6 *#2");
            Verify(
                "nasal-consonant",
                "m a",
                State(EvecConstants.VoiceColorSoft, EvecConstants.AttackMild, EvecConstants.ReleaseBreathShort),
                "m m#2 a a#2 *#1");
            Verify(
                "switch-all",
                "k k#6 a a#6 *#2",
                State(EvecConstants.VoiceColorSoft, EvecConstants.AttackAccentPlain, EvecConstants.ReleaseBreathShort),
                "k k a a#2 *#1",
                expectExactParse: false);

            Verify("extension-1", "k a", State(extension: 1), "k k a");
            Verify("extension-3", "k a", State(extension: 3), "k k k k a");
            Verify(
                "miku-attack-extension-2",
                "k a",
                State(attack: EvecConstants.AttackAccent, extension: 2),
                "k k k k#6 a");
            VerifyAmbiguous(
                "rin-len-attack-extension-2",
                "k a",
                State(attack: EvecConstants.AttackAccentPlain, extension: 2),
                "k k k k a");

            Equal("legacy-strip", EvecPhonemeRecomposer.StripEvec("k#6 a a#6 *#2"), "k a");
            Equal("caret-migration", EvecPhonemeRecomposer.Recompose("k ^k#6 a", State(attack: EvecConstants.AttackAccent)), "k k#6 a");
            Equal("clear", EvecPhonemeRecomposer.Recompose("k k k#6 a a#6 *#2", EvecNoteState.Empty), "k a");
            Equal(
                "standalone-nasal-no-ctop",
                EvecPhonemeRecomposer.Recompose("N", State(attack: EvecConstants.AttackAccentPlain)),
                "N");
            if (EvecPhonemeRecomposer.CanRepresent("a", State(attack: EvecConstants.AttackAccent)))
                throw new InvalidOperationException("vowel-only attack should not be representable");
            VerifyConsonant("rin-len-h-backslash", "h\\ h\\ M", "h\\");
            VerifyConsonant("rin-len-uppercase-z", "Z Z i", "Z");
            if (EvecPhonemeRecomposer.TryGetConsonantBeforeNucleus("a", out _))
                throw new InvalidOperationException("vowel-only note should not expose an extension consonant");
            VerifyExactRealization(
                "sidecar-miku-exact",
                "k k#2 a",
                State(attack: EvecConstants.AttackMild),
                expected: true);
            VerifyExactRealization(
                "sidecar-rin-ambiguous-attack",
                "k k a",
                State(attack: EvecConstants.AttackAccentPlain),
                expected: true);
            VerifyExactRealization(
                "sidecar-rin-ambiguous-extension",
                "k k a",
                State(extension: 1),
                expected: true);
            VerifyExactRealization(
                "sidecar-stale-rejected",
                "k a",
                State(attack: EvecConstants.AttackMild),
                expected: false);

            VerifyTiming("common-89.999ms", 89.999, false, 0.0, release: false);
            VerifyTiming("common-90ms", 90.0, true, 45.0, release: false);
            VerifyTiming("common-240ms", 240.0, true, 45.0, release: false);
            VerifyTiming("release-105ms", 105.0, true, 60.0, release: true);
            VerifyTiming("release-104.999ms", 104.999, false, 0.0, release: true);
            VerifyTiming("release-240ms", 240.0, true, 60.0, release: true);
            VerifyTiming("release-1000ms", 1000.0, true, 60.0, release: true);

            VerifyTransitionMatrices();
            VerifyLyricMovePlans();
            VerifyTransitionComposition();
            VerifyProjectArchiveRoundTrip();

            Console.WriteLine("evec.logic.valid=True");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static EvecNoteState State(
        int color = 0,
        int attack = 0,
        int release = 0,
        int extension = 0) =>
        new(color, attack, release, extension);

    private static void VerifyConsonant(string name, string phonemes, string expected)
    {
        if (!EvecPhonemeRecomposer.TryGetConsonantBeforeNucleus(phonemes, out string actual))
            throw new InvalidOperationException($"{name} did not find a consonant");
        Equal($"{name}.consonant", actual, expected);
    }

    private static void VerifyExactRealization(
        string name,
        string phonemes,
        EvecNoteState state,
        bool expected)
    {
        bool actual = EvecPhonemeRecomposer.IsExactRealization(phonemes, state);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{name} expected {expected}, got {actual}");
        }
        Console.WriteLine($"{name}=passed");
    }

    private static void Verify(
        string name,
        string input,
        EvecNoteState expectedState,
        string expectedPhonemes,
        bool expectExactParse = true)
    {
        string recomposed = EvecPhonemeRecomposer.Recompose(input, expectedState);
        Equal($"{name}.recompose", recomposed, expectedPhonemes);
        if (!EvecPhonemeRecomposer.TryParseEvecFromPhonemes(recomposed, out var parsed, out var basePhonemes))
            throw new InvalidOperationException($"{name}.parse returned false");
        if (expectExactParse && !parsed.Equals(expectedState))
            throw new InvalidOperationException($"{name}.state expected {expectedState}, got {parsed}");
        Equal($"{name}.base", basePhonemes, EvecPhonemeRecomposer.StripEvec(input));
    }

    private static void VerifyAmbiguous(
        string name,
        string input,
        EvecNoteState expectedState,
        string expectedPhonemes)
    {
        Verify(name, input, expectedState, expectedPhonemes, expectExactParse: false);
        if (!EvecPhonemeRecomposer.CanRepresent(expectedPhonemes, expectedState))
            throw new InvalidOperationException($"{name}.state is not an exact physical realization");
        Equal(
            $"{name}.roundtrip",
            EvecPhonemeRecomposer.Recompose(expectedPhonemes, expectedState),
            expectedPhonemes);
    }

    private static void VerifyPlainAttackFallback(
        string phonemes,
        int attack,
        int extension)
    {
        if (!EvecPhonemeRecomposer.TryParseEvecFromPhonemes(
                phonemes,
                out var parsed,
                out _))
            throw new InvalidOperationException("plain-attack fallback parse returned false");

        var resolved = EvecPhonemeRecomposer.ResolvePlainAttackAmbiguity(
            phonemes,
            parsed,
            EvecConstants.AttackAccentPlain);
        if (resolved.AttackId != attack || resolved.ConsonantExtension != extension)
        {
            throw new InvalidOperationException(
                $"plain-attack fallback expected attack={attack}, extension={extension}; " +
                $"got attack={resolved.AttackId}, extension={resolved.ConsonantExtension}");
        }

        Console.WriteLine($"plain-attack-{extension}=passed");
    }

    private static void Equal(string name, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} expected '{expected}', got '{actual}'");
        Console.WriteLine($"{name}=passed");
    }

    private static void VerifyTiming(
        string name,
        double availableDurationMs,
        bool expectedSuccess,
        double expectedDivideMs,
        bool release)
    {
        bool success = EvecTimingMath.TryCalculateDivide(
            availableDurationMs,
            release ? EvecTimingMath.VoiceReleaseDivideStartMs : EvecTimingMath.CommonDivideStartMs,
            release ? EvecTimingMath.VoiceReleaseDivideEndMs : EvecTimingMath.CommonDivideEndMs,
            release ? EvecTimingMath.VoiceReleaseLimitStartMs : EvecTimingMath.CommonLimitStartMs,
            release ? EvecTimingMath.VoiceReleaseLimitEndMs : EvecTimingMath.CommonLimitEndMs,
            EvecTimingMath.MinVowelDurationMs,
            out double divideMs);

        if (success != expectedSuccess || Math.Abs(divideMs - expectedDivideMs) > 0.0001)
        {
            throw new InvalidOperationException(
                $"{name} expected success={expectedSuccess}, divide={expectedDivideMs}, " +
                $"got success={success}, divide={divideMs}");
        }

        Console.WriteLine($"{name}=passed");
    }

    private static void VerifyTransitionMatrices()
    {
        var mikuStates = BuildStates(
            new[]
            {
                EvecConstants.VoiceColorNone,
                EvecConstants.VoiceColorSoft,
                EvecConstants.VoiceColorPower
            },
            new[]
            {
                EvecConstants.AttackNone,
                EvecConstants.AttackMild,
                EvecConstants.AttackAccent
            },
            new[]
            {
                EvecConstants.ReleaseNone,
                EvecConstants.ReleaseBreathShort,
                EvecConstants.ReleaseBreathLong
            },
            Enumerable.Range(
                EvecConstants.MinConsonantExtension,
                EvecConstants.MaxConsonantExtension - EvecConstants.MinConsonantExtension + 1));
        VerifyTransitionMatrix("miku", mikuStates, requireExactParse: true);

        var rinLenStates = BuildStates(
            new[]
            {
                EvecConstants.VoiceColorNone,
                EvecConstants.VoiceColorSoft,
                EvecConstants.VoiceColorPower
            },
            new[]
            {
                EvecConstants.AttackNone,
                EvecConstants.AttackAccentPlain
            },
            new[]
            {
                EvecConstants.ReleaseNone,
                EvecConstants.ReleaseBreathShort,
                EvecConstants.ReleaseBreathLong
            },
            Enumerable.Range(
                EvecConstants.MinConsonantExtension,
                EvecConstants.MaxConsonantExtension - EvecConstants.MinConsonantExtension + 1));
        VerifyTransitionMatrix("rin-len", rinLenStates, requireExactParse: false);

        var lukaStates = BuildStates(
            new[]
            {
                EvecConstants.VoiceColorNone,
                EvecConstants.VoiceColorWhisper,
                EvecConstants.VoiceColorSoft,
                EvecConstants.VoiceColorHusky,
                EvecConstants.VoiceColorNative,
                EvecConstants.VoiceColorPower1,
                EvecConstants.VoiceColorPower,
                EvecConstants.VoiceColorCute,
                EvecConstants.VoiceColorDark,
                EvecConstants.VoiceColorFalsetto
            },
            new[] { EvecConstants.AttackNone },
            new[]
            {
                EvecConstants.ReleaseNone,
                EvecConstants.ReleaseBreathShort,
                EvecConstants.ReleaseBreathLong
            },
            new[] { EvecConstants.MinConsonantExtension });
        VerifyTransitionMatrix("luka", lukaStates, requireExactParse: true);
    }

    private static void VerifyLyricMovePlans()
    {
        VerifyLyricMovePlan(
            "lyric-left-single",
            noteCount: 5,
            firstSelected: 2,
            lastSelected: 2,
            singleSelection: true,
            moveRight: false,
            "1<-2", "2<-3", "3<-4", "4<-empty");
        VerifyLyricMovePlan(
            "lyric-left-multi",
            noteCount: 5,
            firstSelected: 1,
            lastSelected: 3,
            singleSelection: false,
            moveRight: false,
            "0<-1", "1<-2", "2<-3", "3<-empty");
        VerifyLyricMovePlan(
            "lyric-right-single",
            noteCount: 5,
            firstSelected: 2,
            lastSelected: 2,
            singleSelection: true,
            moveRight: true,
            "2<-empty", "3<-2", "4<-3");
        VerifyLyricMovePlan(
            "lyric-right-multi",
            noteCount: 5,
            firstSelected: 1,
            lastSelected: 3,
            singleSelection: false,
            moveRight: true,
            "1<-empty", "2<-1", "3<-2", "4<-3");
        VerifyLyricMovePlan(
            "lyric-left-edge",
            noteCount: 3,
            firstSelected: 0,
            lastSelected: 1,
            singleSelection: false,
            moveRight: false,
            "0<-1", "1<-empty");
        VerifyLyricMovePlan(
            "lyric-right-edge",
            noteCount: 3,
            firstSelected: 1,
            lastSelected: 2,
            singleSelection: false,
            moveRight: true,
            "1<-empty", "2<-1");
    }

    private static void VerifyLyricMovePlan(
        string name,
        int noteCount,
        int firstSelected,
        int lastSelected,
        bool singleSelection,
        bool moveRight,
        params string[] expected)
    {
        string[] actual = EvecLyricMovePlanner.Build(
                noteCount,
                firstSelected,
                lastSelected,
                singleSelection,
                moveRight)
            .Select(item =>
                $"{item.TargetIndex}<-{(item.SourceIndex?.ToString() ?? "empty")}")
            .ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"{name} expected [{string.Join(',', expected)}], " +
                $"got [{string.Join(',', actual)}]");
        }
        Console.WriteLine($"{name}=passed");
    }

    private static void VerifyTransitionComposition()
    {
        var accumulator = new EvecTransitionAccumulator<string, string>();
        accumulator.Apply(
            new[] { "A:old", "B:old" },
            new[] { "A:divided", "C:divided" },
            Key);
        accumulator.Apply(
            new[] { "A:divided", "C:divided" },
            new[] { "A:joined", "D:joined" },
            Key);
        accumulator.Apply(
            new[] { "E:old", "F:old" },
            new[] { "E:divided", "G:divided" },
            Key);

        string[] before = accumulator.Before.Values.OrderBy(value => value).ToArray();
        string[] after = accumulator.After.Values.OrderBy(value => value).ToArray();
        string[] expectedBefore = ["A:old", "B:old", "E:old", "F:old"];
        string[] expectedAfter = ["A:joined", "D:joined", "E:divided", "G:divided"];
        if (!before.SequenceEqual(expectedBefore) || !after.SequenceEqual(expectedAfter))
        {
            throw new InvalidOperationException(
                $"transition composition expected before=[{string.Join(',', expectedBefore)}], " +
                $"after=[{string.Join(',', expectedAfter)}]; got " +
                $"before=[{string.Join(',', before)}], after=[{string.Join(',', after)}]");
        }

        Console.WriteLine("transition-composition=passed");

        static string Key(string value) => value[..value.IndexOf(':')];
    }

    private static List<EvecNoteState> BuildStates(
        IEnumerable<int> colors,
        IEnumerable<int> attacks,
        IEnumerable<int> releases,
        IEnumerable<int> extensions) =>
        (from color in colors
         from attack in attacks
         from release in releases
         from extension in extensions
         select State(color, attack, release, extension)).ToList();

    private static void VerifyTransitionMatrix(
        string name,
        IReadOnlyList<EvecNoteState> states,
        bool requireExactParse)
    {
        const string basePhonemes = "k a";
        var canonical = states
            .Select(state => EvecPhonemeRecomposer.Recompose(basePhonemes, state))
            .ToArray();
        int transitions = 0;

        for (int sourceIndex = 0; sourceIndex < states.Count; sourceIndex++)
        {
            for (int targetIndex = 0; targetIndex < states.Count; targetIndex++)
            {
                string actual = EvecPhonemeRecomposer.Recompose(
                    canonical[sourceIndex],
                    states[targetIndex]);
                EqualSilent(
                    $"{name}.transition[{sourceIndex},{targetIndex}]",
                    actual,
                    canonical[targetIndex]);
                EqualSilent(
                    $"{name}.base[{sourceIndex},{targetIndex}]",
                    EvecPhonemeRecomposer.StripEvec(actual),
                    basePhonemes);

                bool hasParsed = EvecPhonemeRecomposer.TryParseEvecFromPhonemes(
                    actual,
                    out var parsed,
                    out _);
                if (states[targetIndex].HasAnyEvec != hasParsed)
                {
                    throw new InvalidOperationException(
                        $"{name}.transition[{sourceIndex},{targetIndex}] parse presence mismatch");
                }
                if (requireExactParse && hasParsed && !parsed.Equals(states[targetIndex]))
                {
                    throw new InvalidOperationException(
                        $"{name}.transition[{sourceIndex},{targetIndex}] expected " +
                        $"{states[targetIndex]}, got {parsed}");
                }

                transitions++;
            }
        }

        Console.WriteLine($"{name}.transition-count={transitions}");
    }

    private static void VerifyProjectArchiveRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"evec-archive-{Guid.NewGuid():N}");
        string projectPath = Path.Combine(directory, "roundtrip.vpr");
        Directory.CreateDirectory(directory);
        try
        {
            using (var archive = ZipFile.Open(projectPath, ZipArchiveMode.Create))
            {
                var marker = archive.CreateEntry("Project/marker.txt");
                using var writer = new StreamWriter(marker.Open());
                writer.Write("preserve");
            }

            var expected = new EvecProjectData
            {
                Entries =
                {
                    new EvecProjectEntry
                    {
                        Track = 2,
                        Part = 3,
                        Note = 4,
                        RelPosTick = 960,
                        NoteNumber = 64,
                        Occurrence = 1,
                        VoiceColorId = EvecConstants.VoiceColorPower,
                        AttackId = EvecConstants.AttackAccentPlain,
                        ReleaseId = EvecConstants.ReleaseBreathLong,
                        ConsonantExtension = 2
                    }
                }
            };

            EvecProjectArchive.Write(projectPath, expected);
            EvecProjectData actual = EvecProjectArchive.Read(projectPath);
            if (actual.Entries.Count != 1)
                throw new InvalidOperationException("archive round-trip entry count mismatch");
            EvecProjectEntry entry = actual.Entries[0];
            if (entry.Track != 2 || entry.Part != 3 || entry.Note != 4 ||
                entry.RelPosTick != 960 || entry.NoteNumber != 64 || entry.Occurrence != 1 ||
                entry.VoiceColorId != EvecConstants.VoiceColorPower ||
                entry.AttackId != EvecConstants.AttackAccentPlain ||
                entry.ReleaseId != EvecConstants.ReleaseBreathLong ||
                entry.ConsonantExtension != 2)
            {
                throw new InvalidOperationException("archive round-trip payload mismatch");
            }

            using (var archive = ZipFile.OpenRead(projectPath))
            {
                if (archive.GetEntry("Project/marker.txt") == null ||
                    archive.GetEntry(EvecProjectArchive.EntryPath) == null)
                {
                    throw new InvalidOperationException("archive did not preserve project entries");
                }
            }

            EvecProjectArchive.Write(projectPath, new EvecProjectData());
            if (EvecProjectArchive.Read(projectPath).Entries.Count != 0)
                throw new InvalidOperationException("archive empty-state removal failed");

            Console.WriteLine("archive-roundtrip=passed");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void EqualSilent(string name, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} expected '{expected}', got '{actual}'");
    }
}
