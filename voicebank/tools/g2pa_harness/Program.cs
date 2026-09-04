using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const string G2paImportName = "g2pa";
    private const string VsmImportName = "vsm";
    private const int ChineseLanguageId = 4;

    private static int Main(string[] args)
    {
        bool probeEvec = args.Length > 0 && string.Equals(args[0], "--evec-probe", StringComparison.Ordinal);
        string[] tokens = probeEvec ? args[1..] : args;
        string editorDirectory = Environment.GetEnvironmentVariable("G2PA_HARNESS_EDITOR")
            ?? @"C:\Program Files\VOCALOID6\Editor";
        string managerPath = Path.Combine(editorDirectory, "G2PAManager.dll");
        string vsmPath = Path.Combine(editorDirectory, "VSM.dll");
        if (!File.Exists(managerPath) || !File.Exists(vsmPath))
        {
            Console.Error.WriteLine("G2PAManager.dll or VSM.dll does not exist.");
            return 1;
        }

        IntPtr g2paLibrary = NativeLibrary.Load(managerPath);
        IntPtr vsmLibrary = NativeLibrary.Load(vsmPath);
        NativeLibrary.SetDllImportResolver(
            Assembly.GetExecutingAssembly(),
            (libraryName, _, _) => libraryName switch
            {
                G2paImportName => g2paLibrary,
                VsmImportName => vsmLibrary,
                _ => IntPtr.Zero,
            });

        IntPtr managerIf = Native.VIS_G2PAManager_WG2PAManagerIF_create();
        if (managerIf == IntPtr.Zero)
        {
            Console.Error.WriteLine("G2PA manager interface creation failed.");
            return 2;
        }

        IntPtr manager = IntPtr.Zero;
        IntPtr sequenceManager = IntPtr.Zero;
        IntPtr sequence = IntPtr.Zero;
        try
        {
            manager = Native.VIS_G2PAManager_WG2PAManagerIF_createManager(
                managerIf,
                ChineseLanguageId,
                editorDirectory);
            if (manager == IntPtr.Zero)
            {
                Console.Error.WriteLine("Chinese G2PA manager creation failed.");
                return 3;
            }

            sequenceManager = Native.VIS_VSM_WVSMModuleIF_createManager(
                "VOCALOID6",
                "6.13.0.1");
            if (sequenceManager == IntPtr.Zero)
            {
                Console.Error.WriteLine("VSM sequence manager creation failed.");
                return 4;
            }

            VsmSequenceData sequenceData = new()
            {
                SamplingRate = 44100,
                MaxNumTracks = 32,
                MaxUndoCount = 0,
            };
            sequence = Native.VIS_VSM_WIVSMSequenceManager_createSequence(
                sequenceManager,
                ref sequenceData);
            if (sequence == IntPtr.Zero)
            {
                Console.Error.WriteLine("VSM sequence creation failed.");
                return 5;
            }

            IntPtr track = Native.VIS_VSM_WIVSMSequence_insertMidiTrack(
                sequence,
                UIntPtr.Zero,
                "g2pa-harness");
            IntPtr part = track == IntPtr.Zero
                ? IntPtr.Zero
                : Native.VIS_VSM_WIVSMMidiTrack_insertPart(
                    track,
                    0,
                    1920,
                    "g2pa-harness");
            IntPtr note = part == IntPtr.Zero
                ? IntPtr.Zero
                : CreateProbeNote(part);
            if (note == IntPtr.Zero)
            {
                Console.Error.WriteLine("VSM probe note creation failed.");
                return 6;
            }

            if (probeEvec)
                ProbeEvecPhonemeSequences(note);

            Console.WriteLine($"default_lyric={ReadUtf16(Native.VIS_G2PAManager_WG2PAManager_defaultLyric(manager))}");
            Console.WriteLine($"tokens={tokens.Length}");
            int convertible = 0;
            int tokensWithCandidates = 0;
            ulong totalCandidates = 0;
            foreach (string token in tokens)
            {
                bool canConvert = Native.VIS_G2PAManager_WG2PAManager_canConvert(
                    manager,
                    token,
                    false,
                    false);
                if (canConvert)
                {
                    convertible++;
                }

                ulong candidateCount = Native
                    .VIS_G2PAManager_WG2PAManager_candidatePhonemesSyllablesListSize(
                        manager,
                        note,
                        token,
                        false,
                        false)
                    .ToUInt64();
                if (candidateCount > 0)
                {
                    tokensWithCandidates++;
                    totalCandidates += candidateCount;
                }

                Console.WriteLine(
                    $"token={token}\tcan_convert={canConvert}\tcandidates={candidateCount}");
                if (candidateCount > 0)
                {
                    PrintCandidates(manager, note, token, candidateCount);
                }
            }

            Console.WriteLine($"summary.convertible={convertible}");
            Console.WriteLine($"summary.tokens_with_candidates={tokensWithCandidates}");
            Console.WriteLine($"summary.total_candidates={totalCandidates}");
            return 0;
        }
        finally
        {
            if (sequence != IntPtr.Zero)
            {
                bool closed = Native.VIS_VSM_WIVSMSequence_close(sequence);
                Console.WriteLine($"vsm.sequence.closed={closed}");
            }
            if (sequenceManager != IntPtr.Zero)
            {
                bool destroyed = Native.VIS_VSM_WIVSMSequenceManager_destroy(sequenceManager);
                Console.WriteLine($"vsm.manager.destroyed={destroyed}");
            }
            if (manager != IntPtr.Zero)
            {
                Native.VIS_G2PAManager_WG2PAManager_destroy(manager);
            }
            Native.VIS_G2PAManager_WG2PAManagerIF_destroy(managerIf);
        }
    }

    private static IntPtr CreateProbeNote(IntPtr part)
    {
        VsmNoteEvent noteEvent = new()
        {
            Duration = 480,
            Number = 60,
            Velocity = 64,
        };
        VsmNoteExpression noteExpression = new()
        {
            Accent = 50,
            Decay = 50,
            BendDepth = 8,
            BendLength = 0,
            Opening = 127,
            RisePort = false,
            FallPort = false,
        };
        VsmAiNoteExpression aiNoteExpression = default;
        return Native.VIS_VSM_WIVSMMidiPart_insertNote(
            part,
            0,
            ref noteEvent,
            ref noteExpression,
            ref aiNoteExpression,
            "a",
            "a",
            true,
            ChineseLanguageId);
    }

    private static void PrintCandidates(
        IntPtr manager,
        IntPtr note,
        string token,
        ulong candidateCount)
    {
        for (ulong candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            ulong syllableCount = Native
                    .VIS_G2PAManager_WG2PAManager_candidatePhonemesSyllablesSizeByIndex(
                    manager,
                    note,
                    token,
                    checked((int)candidateIndex),
                    false,
                    false)
                .ToUInt64();
            for (ulong syllableIndex = 0; syllableIndex < syllableCount; syllableIndex++)
            {
                UIntPtr syllableLength;
                UIntPtr phonemeLength;
                bool hasLengths = Native
                    .VIS_G2PAManager_WG2PAManager_candidatePhonemesStringLengthByIndex(
                        manager,
                        note,
                        token,
                        checked((int)candidateIndex),
                        checked((int)syllableIndex),
                        out syllableLength,
                        out phonemeLength,
                        false,
                        false);
                if (!hasLengths)
                {
                    continue;
                }

                var syllable = new StringBuilder(checked((int)syllableLength.ToUInt64()));
                var phonemes = new StringBuilder(checked((int)phonemeLength.ToUInt64()));
                bool success = Native.VIS_G2PAManager_WG2PAManager_candidatePhonemesByIndex(
                    manager,
                    note,
                    token,
                    checked((int)candidateIndex),
                    checked((int)syllableIndex),
                    syllable,
                    phonemes,
                    false,
                    false);
                if (success)
                {
                    Console.WriteLine(
                        $"candidate={candidateIndex}\tsyllable={syllable}\tphonemes={phonemes}");
                }
            }
        }
    }

    private static void ProbeEvecPhonemeSequences(IntPtr note)
    {
        string[] probes =
        [
            "k a",
            "k k#2 a",
            "k k#6 a",
            "k k a",
            "k k k k#6 a",
            "k k#6 a a#6 *#2",
            "m m a a#2 *#1",
        ];

        foreach (string phonemes in probes)
        {
            bool set = Native.VIS_VSM_WIVSMNote_setPhonemes(note, phonemes, true, 0);
            string stored = ReadUtf16(Native.VIS_VSM_WIVSMNote_phonemes(note));
            bool valid = Native.VIS_VSM_WIVSMNote_isValidPhonemes(note);
            int count = Native.VIS_VSM_WIVSMNote_numPhonemePosition(note);
            var positions = new int[Math.Max(0, count)];
            for (int index = 0; index < positions.Length; index++)
                positions[index] = Native.VIS_VSM_WIVSMNote_phonemePosition(note, index);

            Console.WriteLine(
                $"evec.phonemes={phonemes}\tset={set}\tstored={stored}\tvalid={valid}" +
                $"\tpositions=[{string.Join(',', positions)}]");
        }
    }

    private static string ReadUtf16(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty;

    [StructLayout(LayoutKind.Sequential)]
    private struct VsmSequenceData
    {
        internal int SamplingRate;
        internal ulong MaxNumTracks;
        internal ulong MaxUndoCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VsmNoteEvent
    {
        internal int Duration;
        internal int Number;
        internal int Velocity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VsmNoteExpression
    {
        internal int Accent;
        internal int Decay;
        internal int BendDepth;
        internal int BendLength;
        internal int Opening;

        [MarshalAs(UnmanagedType.U1)]
        internal bool RisePort;

        [MarshalAs(UnmanagedType.U1)]
        internal bool FallPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VsmAiNoteExpression
    {
        internal float PitchFine;
        internal float PitchDriftStart;
        internal float PitchDriftEnd;
        internal float PitchScalingCenter;
        internal float PitchScalingOrigin;
        internal float PitchTransitionStart;
        internal float PitchTransitionEnd;
        internal float AmplitudeWhole;
        internal float AmplitudeStart;
        internal float AmplitudeEnd;
        internal float VibratoLeadingDepth;
        internal float VibratoFollowingDepth;
    }

    private static class Native
    {
        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_G2PAManager_WG2PAManagerIF_create();

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VIS_G2PAManager_WG2PAManagerIF_destroy(IntPtr managerIf);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_G2PAManager_WG2PAManagerIF_createManager(
            IntPtr managerIf,
            int languageId,
            [MarshalAs(UnmanagedType.LPWStr)] string directory);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VIS_G2PAManager_WG2PAManager_destroy(IntPtr manager);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_G2PAManager_WG2PAManager_defaultLyric(IntPtr manager);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VIS_G2PAManager_WG2PAManager_canConvert(
            IntPtr manager,
            [MarshalAs(UnmanagedType.LPWStr)] string lyrics,
            bool useExtensionDictionary,
            bool isAi);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr
            VIS_G2PAManager_WG2PAManager_candidatePhonemesSyllablesListSize(
                IntPtr manager,
                IntPtr note,
                [MarshalAs(UnmanagedType.LPWStr)] string lyrics,
                bool useExtensionDictionary,
                bool isAi);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr
            VIS_G2PAManager_WG2PAManager_candidatePhonemesSyllablesSizeByIndex(
                IntPtr manager,
                IntPtr note,
                [MarshalAs(UnmanagedType.LPWStr)] string lyrics,
                int candidateIndex,
                bool useExtensionDictionary,
                bool isAi);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool
            VIS_G2PAManager_WG2PAManager_candidatePhonemesStringLengthByIndex(
                IntPtr manager,
                IntPtr note,
                [MarshalAs(UnmanagedType.LPWStr)] string lyrics,
                int candidateIndex,
                int syllableIndex,
                out UIntPtr syllableLength,
                out UIntPtr phonemeLength,
                bool useExtensionDictionary,
                bool isAi);

        [DllImport(G2paImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VIS_G2PAManager_WG2PAManager_candidatePhonemesByIndex(
            IntPtr manager,
            IntPtr note,
            [MarshalAs(UnmanagedType.LPWStr)] string lyrics,
            int candidateIndex,
            int syllableIndex,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder syllable,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder phonemes,
            bool useExtensionDictionary,
            bool isAi);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_VSM_WVSMModuleIF_createManager(
            [MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string appVersion);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VIS_VSM_WIVSMSequenceManager_destroy(
            IntPtr sequenceManager);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_VSM_WIVSMSequenceManager_createSequence(
            IntPtr sequenceManager,
            ref VsmSequenceData sequenceData);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VIS_VSM_WIVSMSequence_close(IntPtr sequence);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_VSM_WIVSMSequence_insertMidiTrack(
            IntPtr sequence,
            UIntPtr index,
            [MarshalAs(UnmanagedType.LPWStr)] string name);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_VSM_WIVSMMidiTrack_insertPart(
            IntPtr track,
            int absolutePosition,
            int duration,
            [MarshalAs(UnmanagedType.LPWStr)] string name);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_VSM_WIVSMMidiPart_insertNote(
            IntPtr part,
            int relativePosition,
            ref VsmNoteEvent noteEvent,
            ref VsmNoteExpression noteExpression,
            ref VsmAiNoteExpression aiNoteExpression,
            [MarshalAs(UnmanagedType.LPWStr)] string lyric,
            [MarshalAs(UnmanagedType.LPWStr)] string phonemes,
            [MarshalAs(UnmanagedType.U1)] bool isValidPhonemes,
            int languageId);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VIS_VSM_WIVSMNote_setPhonemes(
            IntPtr note,
            [MarshalAs(UnmanagedType.LPWStr)] string phonemes,
            [MarshalAs(UnmanagedType.U1)] bool isValidPhonemes,
            int languageId);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_VSM_WIVSMNote_phonemes(IntPtr note);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VIS_VSM_WIVSMNote_isValidPhonemes(IntPtr note);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VIS_VSM_WIVSMNote_numPhonemePosition(IntPtr note);

        [DllImport(VsmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VIS_VSM_WIVSMNote_phonemePosition(IntPtr note, int index);
    }
}
