using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private const long SingerConstructorRva = 0x10c5b0;
    private const long InitializeSingerChildrenRva = 0x10ccd0;
    private const long PhoneticDictionaryConstructorRva = 0x124b80;
    private const long PhoneticUnitGroupConstructorRva = 0x1278b0;
    private const long StationaryConstructorRva = 0x113960;
    private const long StationaryPhonemeConstructorRva = 0x113990;
    private const long StationaryPartConstructorRva = 0x1139d0;
    private const long AddStationaryPartRva = 0x113ac0;
    private const long ArticulationTargetConstructorRva = 0x110b30;
    private const long ArticulationPartConstructorRva = 0x110b70;
    private const long ChunkConstructorRva = 0x1067f0;
    private const long EmptyChunkVtableRva = 0x225270;
    private const long FindNamedChildRva = 0x1076b0;
    private const long SetChunkNameRva = 0x107000;
    private const long AddChildRva = 0x107570;
    private const long LoadSingerRva = 0x10d490;
    private const long CompileAndWriteRva = 0x10ddb0;
    private const long PrepareSingerSerializationRva = 0x10e060;
    private const long WriteDatRva = 0x10e1c0;
    private const long WriteTreeRva = 0x10e2c0;
    private const int SingerSize = 0x310;
    private const int PhoneticDictionarySize = 0x170;
    private const int PhoneticUnitGroupSize = 0x158;
    private const int PhonemeEntrySize = 0x40;
    private const int StationarySize = 0x160;
    private const int StationaryPhonemeSize = 0x170;
    private const int StationaryPartSize = 0x268;
    private const int EmptyChunkSize = 0x140;
    private const int ArticulationTargetSize = 0x178;
    private const int ArticulationPartSize = 0x268;
    private const int EmptyChunkMagic = 0x54504d45;

    private sealed record PlannedPhoneme(string Name, bool Unvoiced);

    private sealed record PlannedStationary(
        string Phoneme,
        int StationaryIndex,
        List<string> UnitIds);

    private sealed record PlannedArticulation(
        string Source,
        string Target,
        int TargetIndex,
        List<string> UnitIds,
        List<long> SndSourceOffsets,
        List<long> EprSourceOffsets);

    private sealed record TreePlan(
        byte LanguageId,
        List<PlannedPhoneme> Phonemes,
        List<PlannedStationary> Stationary,
        List<PlannedArticulation> Articulations);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate nint SingerConstructor(
        nint self,
        nint parent,
        [MarshalAs(UnmanagedType.LPStr)] string directory,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        float sampleRate,
        byte language);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PointerConstructor(nint self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PointerConstructorWithParent(nint self, nint parent);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PointerConstructorWithInt(nint self, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long UnaryMethod(nint self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long BinaryIntMethod(nint self, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate nint FindNamedChild(
        nint self,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void SetChunkName(
        nint self,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AddChild(nint self, nint child);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long SingerSerializationHook(nint self, nint stream, nint unused1, nint unused2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string? pathName);

    private static int Main(string[] args)
    {
        string dsePath = args.Length >= 1
            ? Path.GetFullPath(args[0])
            : @"C:\Program Files\VOCALOID6\Editor\DSE.dll";
        string outputDirectory = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : Path.GetFullPath("empty_singer");
        string singerName = args.Length >= 3 ? args[2] : "minimal";
        bool planArguments = args.Length >= 4 && args[3] == "--plan";
        if (planArguments && args.Length != 5)
        {
            Console.Error.WriteLine("--plan requires exactly one JSON path.");
            return 2;
        }
        string? diagnosticPhoneme = args.Length >= 4 && !planArguments
            ? args[3]
            : null;
        string? planPath = planArguments ? Path.GetFullPath(args[4]) : null;
        TreePlan? treePlan;
        try
        {
            treePlan = planPath is null ? null : ReadTreePlan(planPath);
        }
        catch (Exception error) when (
            error is IOException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"Unable to read tree plan: {error.Message}");
            return 2;
        }
        bool addSilA =
            Environment.GetEnvironmentVariable("TREE_HARNESS_ADD_SIL_A") == "1";
        string? stationaryPhoneme = treePlan is null
            ? diagnosticPhoneme ?? (addSilA ? "a" : null)
            : null;
        bool loadExisting =
            Environment.GetEnvironmentVariable("TREE_HARNESS_LOAD_EXISTING") == "1";
        bool initializeChildren =
            treePlan is not null ||
            !string.IsNullOrEmpty(stationaryPhoneme) ||
            Environment.GetEnvironmentVariable("TREE_HARNESS_INITIALIZE_CHILDREN") == "1";

        if (!File.Exists(dsePath))
        {
            Console.Error.WriteLine($"DSE not found: {dsePath}");
            return 2;
        }
        if (singerName.Length == 0 || singerName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Console.Error.WriteLine("Singer name must be a non-empty file name.");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);
        // CDBSinger stores <outputDirectory>\\<singerName> as its base path.
        // Its serialization hook emits sidecar text files below that base path
        // before the wrapper writes <base>.tree, and DSE does not check whether
        // fopen() succeeded.  Create the sidecar directory up front.
        Directory.CreateDirectory(Path.Combine(outputDirectory, singerName));
        string editorDirectory = Path.GetDirectoryName(dsePath)!;
        if (!SetDllDirectoryW(editorDirectory))
        {
            Console.Error.WriteLine($"SetDllDirectoryW failed: {Marshal.GetLastWin32Error()}");
            return 3;
        }

        nint module = NativeLibrary.Load(dsePath);
        nint singer = Marshal.AllocHGlobal(SingerSize);
        nint phoneticDictionary = Marshal.AllocHGlobal(PhoneticDictionarySize);
        nint phoneticUnitGroup = Marshal.AllocHGlobal(PhoneticUnitGroupSize);
        nint phonemePointers = nint.Zero;
        List<nint> phonemeEntries = new();
        Marshal.Copy(new byte[SingerSize], 0, singer, SingerSize);
        Marshal.Copy(new byte[PhoneticDictionarySize], 0, phoneticDictionary, PhoneticDictionarySize);
        Marshal.Copy(new byte[PhoneticUnitGroupSize], 0, phoneticUnitGroup, PhoneticUnitGroupSize);
        GetFunction<PointerConstructor>(module, PhoneticDictionaryConstructorRva)(phoneticDictionary);
        GetFunction<PointerConstructorWithParent>(module, PhoneticUnitGroupConstructorRva)(
            phoneticUnitGroup,
            phoneticDictionary);
        // Bit 2 serializes the PHG2 guide block.  Keep it present even in the
        // minimal dictionary because compiled DDI consumers expect PHDC to be
        // followed by PHG2.
        Marshal.WriteInt32(phoneticDictionary, 0x140, 4);
        Marshal.WriteIntPtr(phoneticDictionary, 0x158, phoneticUnitGroup);
        List<(string Name, bool Unvoiced)> phonemes = treePlan is not null
            ? treePlan.Phonemes.Select(item => (item.Name, item.Unvoiced)).ToList()
            : addSilA
                ? new() { ("Sil", true), ("a", false) }
                : !string.IsNullOrEmpty(stationaryPhoneme)
                    ? new() { (stationaryPhoneme, false) }
                    : new();
        if (phonemes.Count > 0)
        {
            phonemePointers = Marshal.AllocHGlobal(nint.Size * phonemes.Count);
            for (int index = 0; index < phonemes.Count; index++)
            {
                (string name, bool unvoiced) = phonemes[index];
                byte[] phonemeBytes = System.Text.Encoding.ASCII.GetBytes(name);
                if (phonemeBytes.Length == 0 || phonemeBytes.Length > 0x10)
                {
                    Console.Error.WriteLine(
                        "Diagnostic phonemes must contain 1 to 16 ASCII bytes.");
                    return 2;
                }
                nint phonemeEntry = Marshal.AllocHGlobal(PhonemeEntrySize);
                phonemeEntries.Add(phonemeEntry);
                Marshal.Copy(new byte[PhonemeEntrySize], 0, phonemeEntry, PhonemeEntrySize);
                Marshal.Copy(phonemeBytes, 0, phonemeEntry, phonemeBytes.Length);
                Marshal.WriteByte(phonemeEntry, 0x38, unvoiced ? (byte)1 : (byte)0);
                Marshal.WriteIntPtr(phonemePointers, index * nint.Size, phonemeEntry);
            }
            Marshal.WriteInt32(phoneticDictionary, 0x144, phonemes.Count);
            Marshal.WriteIntPtr(phoneticDictionary, 0x148, phonemePointers);
        }
        try
        {
            Console.WriteLine("stage=construct");
            nint constructed = GetFunction<SingerConstructor>(module, SingerConstructorRva)(
                singer,
                phoneticDictionary,
                outputDirectory,
                singerName,
                44100.0f,
                treePlan?.LanguageId ?? 0);
            Console.WriteLine($"stage=constructed result=0x{constructed:x}");
            if (constructed != singer)
            {
                Console.Error.WriteLine($"Unexpected singer constructor result: 0x{constructed:x}");
                return 4;
            }
            if (loadExisting)
            {
                Console.WriteLine("stage=initialize_for_load");
                long loadInitializeResult = GetFunction<UnaryMethod>(
                    module,
                    InitializeSingerChildrenRva)(singer);
                Console.WriteLine($"stage=initialized_for_load result={loadInitializeResult}");
                if (loadInitializeResult != 0)
                {
                    return 6;
                }
                return LoadExisting(module, singer, treePlan);
            }
            long initializeResult = 0;
            if (initializeChildren)
            {
                Console.WriteLine("stage=initialize_children");
                initializeResult = GetFunction<UnaryMethod>(
                    module,
                    InitializeSingerChildrenRva)(singer);
                Console.WriteLine($"stage=initialized result={initializeResult}");
            }
            else
            {
                Console.WriteLine("stage=initialize_children skipped");
            }
            if (treePlan is not null)
            {
                Console.WriteLine(
                    $"stage=add_planned_tree phonemes={treePlan.Phonemes.Count} " +
                    $"stationary_groups={treePlan.Stationary.Count} " +
                    $"articulation_edges={treePlan.Articulations.Count}");
                int stationaryParts = AddPlannedStationary(module, singer, treePlan.Stationary);
                int articulationParts = AddPlannedArticulations(
                    module,
                    singer,
                    treePlan.Articulations);
                Console.WriteLine(
                    $"stage=planned_tree_added stationary_parts={stationaryParts} " +
                    $"articulation_parts={articulationParts}");
            }
            else if (!string.IsNullOrEmpty(stationaryPhoneme))
            {
                Console.WriteLine("stage=add_stationary_phoneme");
                bool addEmptyPart =
                    Environment.GetEnvironmentVariable("TREE_HARNESS_ADD_EMPTY_STAP") == "1";
                bool addEmptyReferences =
                    Environment.GetEnvironmentVariable("TREE_HARNESS_ADD_EMPTY_REFS") == "1";
                AddStationaryPhoneme(
                    module,
                    singer,
                    stationaryPhoneme,
                    addEmptyPart || addEmptyReferences,
                    addEmptyReferences);
                Console.WriteLine("stage=stationary_phoneme_added");
                if (Environment.GetEnvironmentVariable("TREE_HARNESS_ADD_EMPTY_ARTP") == "1")
                {
                    if (addSilA)
                    {
                        Console.WriteLine("stage=add_articulation_transition Sil>a");
                        AddArticulationTransition(module, singer, "Sil", "a");
                        Console.WriteLine("stage=add_articulation_transition a>Sil");
                        AddArticulationTransition(module, singer, "a", "Sil");
                        Console.WriteLine("stage=articulation_transitions_added");
                    }
                    else
                    {
                        Console.WriteLine("stage=add_articulation_transition");
                        AddArticulationTransition(
                            module,
                            singer,
                            stationaryPhoneme,
                            stationaryPhoneme);
                        Console.WriteLine("stage=articulation_transition_added");
                    }
                }
            }
            Console.WriteLine("stage=compile_and_write");
            long compileResult = GetFunction<BinaryIntMethod>(
                module,
                CompileAndWriteRva)(singer, 0);
            Console.WriteLine($"stage=compiled result={compileResult}");
            nint diagnosticStream = Marshal.AllocHGlobal(0x80);
            Marshal.Copy(new byte[0x80], 0, diagnosticStream, 0x80);
            Console.WriteLine("stage=prepare_serialization");
            long prepareResult = GetFunction<SingerSerializationHook>(
                module,
                PrepareSingerSerializationRva)(singer, diagnosticStream, nint.Zero, nint.Zero);
            Console.WriteLine($"stage=prepared result={prepareResult}");
            Marshal.FreeHGlobal(diagnosticStream);
            Console.WriteLine("stage=write_tree");
            long writeResult = GetFunction<UnaryMethod>(module, WriteTreeRva)(singer);
            Console.WriteLine($"stage=written result={writeResult}");
            Console.WriteLine("stage=write_dat");
            long writeDatResult = GetFunction<UnaryMethod>(module, WriteDatRva)(singer);
            Console.WriteLine($"stage=dat_written result={writeDatResult}");
            string outputPath = Path.Combine(outputDirectory, singerName + ".tree");
            string datPath = Path.Combine(outputDirectory, singerName + ".dat");
            long outputBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            long datBytes = File.Exists(datPath) ? new FileInfo(datPath).Length : 0;
            Console.WriteLine($"initialize.result={initializeResult}");
            Console.WriteLine($"compile.result={compileResult}");
            Console.WriteLine($"write.result={writeResult}");
            Console.WriteLine($"output={outputPath}");
            Console.WriteLine($"output_bytes={outputBytes}");
            Console.WriteLine($"dat.result={writeDatResult}");
            Console.WriteLine($"dat={datPath}");
            Console.WriteLine($"dat_bytes={datBytes}");
            return initializeResult == 0 &&
                   compileResult == 0 &&
                   writeResult == 0 &&
                   writeDatResult == 0 &&
                   outputBytes > 0 &&
                   datBytes > 0
                ? 0
                : 5;
        }
        finally
        {
            // This diagnostic is short-lived. Avoid calling an unverified destructor;
            // DSE-owned child allocations are reclaimed when the process exits.
            // The DDI loader destroys and replaces the initial phonetic dictionary,
            // including its group.  This short-lived diagnostic must not free those
            // original pointers again after load.
            if (!loadExisting)
            {
                Marshal.FreeHGlobal(singer);
                Marshal.FreeHGlobal(phoneticDictionary);
                Marshal.FreeHGlobal(phoneticUnitGroup);
                if (phonemePointers != nint.Zero)
                {
                    Marshal.FreeHGlobal(phonemePointers);
                }
                foreach (nint phonemeEntry in phonemeEntries)
                {
                    Marshal.FreeHGlobal(phonemeEntry);
                }
            }
        }
    }

    private static T GetFunction<T>(nint module, long rva) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(module + checked((int)rva));
    }

    private static int LoadExisting(nint module, nint singer, TreePlan? treePlan)
    {
        Console.WriteLine("stage=load_existing");
        long loadResult = GetFunction<UnaryMethod>(module, LoadSingerRva)(singer);
        if (treePlan is not null)
        {
            return ValidatePlannedLoad(module, singer, treePlan, loadResult);
        }
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        nint voice = findNamedChild(singer, "voice");
        nint stationaryArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "stationary");
        int stationaryCount = ReadCount(stationaryArray);
        nint stationary = ReadChild(stationaryArray, 0);
        int phonemeCount = ReadCount(stationary);
        nint stationaryPhoneme = ReadChild(stationary, 0);
        int partCount = ReadCount(stationaryPhoneme);
        nint stationaryPart = ReadChild(stationaryPhoneme, 0);
        nint sndReference = stationaryPart == nint.Zero
            ? nint.Zero
            : findNamedChild(stationaryPart, "SND");
        nint eprReference = stationaryPart == nint.Zero
            ? nint.Zero
            : findNamedChild(stationaryPart, "EpR");

        nint articulationArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "articulation");
        int articulationCount = ReadCount(articulationArray);
        nint articulation = ReadChild(articulationArray, 0);
        int targetCount = ReadCount(articulation);
        nint articulationTarget = ReadChild(articulation, 0);
        int articulationPartCount = ReadCount(articulationTarget);
        nint articulationPart = ReadChild(articulationTarget, 0);
        nint artSndReference = articulationPart == nint.Zero
            ? nint.Zero
            : findNamedChild(articulationPart, "SND");
        nint artEprReference = articulationPart == nint.Zero
            ? nint.Zero
            : findNamedChild(articulationPart, "EpR");

        int frameCount = stationaryPart == nint.Zero
            ? -1
            : Marshal.ReadInt32(stationaryPart, 0x1b0);
        int sampleRate = stationaryPart == nint.Zero
            ? -1
            : Marshal.ReadInt32(stationaryPart, 0x1c8);
        short channels = stationaryPart == nint.Zero
            ? (short)-1
            : Marshal.ReadInt16(stationaryPart, 0x1cc);
        int pcmCount = stationaryPart == nint.Zero
            ? -1
            : Marshal.ReadInt32(stationaryPart, 0x1d0);
        long sndPointer = stationaryPart == nint.Zero
            ? -1
            : Marshal.ReadInt64(stationaryPart, 0x1c0);
        int payload0 = stationaryPart == nint.Zero
            ? 0
            : Marshal.ReadInt32(stationaryPart, 0x1f0);
        int payload1 = stationaryPart == nint.Zero
            ? 0
            : Marshal.ReadInt32(stationaryPart, 0x1f4);
        int payload2 = stationaryPart == nint.Zero
            ? 0
            : Marshal.ReadInt32(stationaryPart, 0x1f8);
        int payload3 = stationaryPart == nint.Zero
            ? 0
            : Marshal.ReadInt32(stationaryPart, 0x1fc);
        byte rootAuthenticated = Marshal.ReadByte(singer, 0x2d4);
        int artFrameCount = ReadInt32(articulationPart, 0x1b0);
        int artSampleRate = ReadInt32(articulationPart, 0x1c8);
        short artChannels = articulationPart == nint.Zero
            ? (short)-1
            : Marshal.ReadInt16(articulationPart, 0x1cc);
        int artPcmCount = ReadInt32(articulationPart, 0x1d0);
        long artSndPayloadPointer = ReadInt64(articulationPart, 0x1b8);
        long artSndCorePointer = ReadInt64(articulationPart, 0x1c0);
        nint alignmentBegin = articulationPart == nint.Zero
            ? nint.Zero
            : Marshal.ReadIntPtr(articulationPart, 0x1d8);
        nint alignmentEnd = articulationPart == nint.Zero
            ? nint.Zero
            : Marshal.ReadIntPtr(articulationPart, 0x1e0);
        long alignmentBytes = alignmentBegin == nint.Zero || alignmentEnd == nint.Zero
            ? 0
            : alignmentEnd - alignmentBegin;
        int alignmentCount = alignmentBytes >= 0 && alignmentBytes % 16 == 0
            ? checked((int)(alignmentBytes / 16))
            : -1;

        Console.WriteLine($"load.result={loadResult}");
        Console.WriteLine($"root.authenticated={rootAuthenticated}");
        Console.WriteLine($"stationary.count={stationaryCount}");
        Console.WriteLine($"phoneme.count={phonemeCount}");
        Console.WriteLine($"part.count={partCount}");
        Console.WriteLine($"part.snd_reference=0x{sndReference:x}");
        Console.WriteLine($"part.epr_reference=0x{eprReference:x}");
        Console.WriteLine($"part.frame_count={frameCount}");
        Console.WriteLine($"part.sample_rate={sampleRate}");
        Console.WriteLine($"part.channels={channels}");
        Console.WriteLine($"part.pcm_count={pcmCount}");
        Console.WriteLine($"part.snd_pointer={sndPointer}");
        Console.WriteLine($"part.integrity_payload={payload0},{payload1},{payload2},{payload3}");
        Console.WriteLine($"articulation.count={articulationCount}");
        Console.WriteLine($"articulation_target.count={targetCount}");
        Console.WriteLine($"articulation_part.count={articulationPartCount}");
        Console.WriteLine($"articulation_part.snd_reference=0x{artSndReference:x}");
        Console.WriteLine($"articulation_part.epr_reference=0x{artEprReference:x}");
        Console.WriteLine($"articulation_part.frame_count={artFrameCount}");
        Console.WriteLine($"articulation_part.sample_rate={artSampleRate}");
        Console.WriteLine($"articulation_part.channels={artChannels}");
        Console.WriteLine($"articulation_part.pcm_count={artPcmCount}");
        Console.WriteLine($"articulation_part.snd_payload_pointer={artSndPayloadPointer}");
        Console.WriteLine($"articulation_part.snd_core_pointer={artSndCorePointer}");
        Console.WriteLine($"articulation_part.alignment_count={alignmentCount}");
        for (int index = 0; index < alignmentCount; index++)
        {
            nint item = alignmentBegin + index * 16;
            Console.WriteLine(
                $"articulation_part.alignment[{index}]=" +
                $"{Marshal.ReadInt32(item, 0)}," +
                $"{Marshal.ReadInt32(item, 4)}," +
                $"{Marshal.ReadInt32(item, 8)}," +
                $"{Marshal.ReadInt32(item, 12)}");
        }

        bool expectSilA =
            Environment.GetEnvironmentVariable("TREE_HARNESS_EXPECT_SIL_A") == "1";
        bool silToAValid = true;
        bool aToSilValid = true;
        long silToAPayloadPointer = -1;
        long aToSilPayloadPointer = -1;
        if (expectSilA)
        {
            silToAValid = ValidateNamedArticulation(
                module,
                articulationArray,
                "Sil",
                "a",
                "sil_to_a",
                out silToAPayloadPointer);
            aToSilValid = ValidateNamedArticulation(
                module,
                articulationArray,
                "a",
                "Sil",
                "a_to_sil",
                out aToSilPayloadPointer);
        }

        bool expectArticulation =
            Environment.GetEnvironmentVariable("TREE_HARNESS_EXPECT_ARTP") == "1";
        bool articulationValid = expectSilA
            ? articulationCount == 2 &&
              silToAValid &&
              aToSilValid &&
              silToAPayloadPointer != aToSilPayloadPointer
            : !expectArticulation ||
                                 (articulationCount == 1 &&
                                  targetCount == 1 &&
                                  articulationPartCount == 1 &&
                                  artSndReference != nint.Zero &&
                                  artEprReference != nint.Zero &&
                                  artFrameCount > 0 &&
                                  artSampleRate == 44100 &&
                                  artChannels == 1 &&
                                  artPcmCount == artFrameCount * 256 + 2048 &&
                                  artSndPayloadPointer >= 0 &&
                                  artSndCorePointer == artSndPayloadPointer + 2048 &&
                                  alignmentCount == 2);
        bool valid = loadResult == 0 &&
                     rootAuthenticated == 1 &&
                     stationaryCount == 1 &&
                     phonemeCount == 1 &&
                     partCount == 1 &&
                     sndReference != nint.Zero &&
                     eprReference != nint.Zero &&
                     frameCount > 0 &&
                     sampleRate == 44100 &&
                     channels == 1 &&
                     pcmCount == frameCount * 256 + 2048 &&
                     sndPointer >= 0 &&
                     payload0 == -1 &&
                     payload1 == -1 &&
                     payload2 == -1 &&
                     payload3 == -1 &&
                     articulationValid;
        Console.WriteLine($"load.valid={valid}");
        return valid ? 0 : 6;
    }

    private static int ValidatePlannedLoad(
        nint module,
        nint singer,
        TreePlan plan,
        long loadResult)
    {
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        byte rootAuthenticated = Marshal.ReadByte(singer, 0x2d4);
        nint voice = findNamedChild(singer, "voice");
        nint stationaryArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "stationary");
        nint stationary = stationaryArray == nint.Zero
            ? nint.Zero
            : findNamedChild(stationaryArray, "normal");
        nint articulationArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "articulation");

        bool stationaryValid = ReadCount(stationaryArray) == (plan.Stationary.Count > 0 ? 1 : 0) &&
                               ReadCount(stationary) == plan.Stationary.Count;
        int stationaryParts = 0;
        foreach (PlannedStationary group in plan.Stationary)
        {
            nint phoneme = stationary == nint.Zero
                ? nint.Zero
                : findNamedChild(stationary, group.Phoneme);
            bool groupValid = phoneme != nint.Zero && ReadCount(phoneme) == group.UnitIds.Count;
            for (int index = 0; index < group.UnitIds.Count; index++)
            {
                groupValid &= ValidateLoadedStationaryPart(
                    module,
                    ReadChild(phoneme, index));
                stationaryParts++;
            }
            Console.WriteLine(
                $"planned.stationary[{group.Phoneme}].parts={ReadCount(phoneme)} " +
                $"valid={groupValid}");
            stationaryValid &= groupValid;
        }

        Dictionary<string, List<PlannedArticulation>> plannedBySource = plan.Articulations
            .GroupBy(item => item.Source, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.Ordinal);
        bool articulationValid = ReadCount(articulationArray) == plan.Phonemes.Count;
        int articulationParts = 0;
        int articulationEdges = 0;
        foreach (PlannedPhoneme phoneme in plan.Phonemes)
        {
            nint source = articulationArray == nint.Zero
                ? nint.Zero
                : findNamedChild(articulationArray, phoneme.Name);
            plannedBySource.TryGetValue(
                phoneme.Name,
                out List<PlannedArticulation>? plannedTargets);
            plannedTargets ??= new List<PlannedArticulation>();
            bool sourceValid = source != nint.Zero && ReadCount(source) == plannedTargets.Count;
            foreach (PlannedArticulation group in plannedTargets)
            {
                nint target = source == nint.Zero
                    ? nint.Zero
                    : findNamedChild(source, group.Target);
                bool targetValid = target != nint.Zero && ReadCount(target) == group.UnitIds.Count;
                for (int index = 0; index < group.UnitIds.Count; index++)
                {
                    targetValid &= ValidateLoadedArticulationPart(
                        module,
                        ReadChild(target, index));
                    articulationParts++;
                }
                Console.WriteLine(
                    $"planned.articulation[{group.Source}->{group.Target}].parts=" +
                    $"{ReadCount(target)} valid={targetValid}");
                sourceValid &= targetValid;
                articulationEdges++;
            }
            articulationValid &= sourceValid;
        }

        bool valid = loadResult == 0 &&
                     rootAuthenticated == 1 &&
                     voice != nint.Zero &&
                     stationaryValid &&
                     articulationValid &&
                     stationaryParts == plan.Stationary.Sum(item => item.UnitIds.Count) &&
                     articulationParts == plan.Articulations.Sum(item => item.UnitIds.Count) &&
                     articulationEdges == plan.Articulations.Count;
        Console.WriteLine($"load.result={loadResult}");
        Console.WriteLine($"root.authenticated={rootAuthenticated}");
        Console.WriteLine($"planned.phonemes={plan.Phonemes.Count}");
        Console.WriteLine($"planned.stationary_groups={plan.Stationary.Count}");
        Console.WriteLine($"planned.stationary_parts={stationaryParts}");
        Console.WriteLine($"planned.articulation_sources={ReadCount(articulationArray)}");
        Console.WriteLine($"planned.articulation_edges={articulationEdges}");
        Console.WriteLine($"planned.articulation_parts={articulationParts}");
        Console.WriteLine($"load.valid={valid}");
        return valid ? 0 : 6;
    }

    private static bool ValidateLoadedStationaryPart(nint module, nint part)
    {
        if (part == nint.Zero)
        {
            return false;
        }
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        int frameCount = ReadInt32(part, 0x1b0);
        int sampleRate = ReadInt32(part, 0x1c8);
        short channels = Marshal.ReadInt16(part, 0x1cc);
        int pcmCount = ReadInt32(part, 0x1d0);
        return findNamedChild(part, "SND") != nint.Zero &&
               findNamedChild(part, "EpR") != nint.Zero &&
               frameCount > 0 &&
               sampleRate == 44100 &&
               channels == 1 &&
               pcmCount == frameCount * 256 + 2048 &&
               ReadInt64(part, 0x1c0) >= 0 &&
               ReadInt32(part, 0x1f0) == -1 &&
               ReadInt32(part, 0x1f4) == -1 &&
               ReadInt32(part, 0x1f8) == -1 &&
               ReadInt32(part, 0x1fc) == -1;
    }

    private static bool ValidateLoadedArticulationPart(nint module, nint part)
    {
        if (part == nint.Zero)
        {
            return false;
        }
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        int frameCount = ReadInt32(part, 0x1b0);
        int sampleRate = ReadInt32(part, 0x1c8);
        short channels = Marshal.ReadInt16(part, 0x1cc);
        int pcmCount = ReadInt32(part, 0x1d0);
        long payloadPointer = ReadInt64(part, 0x1b8);
        long corePointer = ReadInt64(part, 0x1c0);
        nint alignmentBegin = Marshal.ReadIntPtr(part, 0x1d8);
        nint alignmentEnd = Marshal.ReadIntPtr(part, 0x1e0);
        long alignmentBytes = alignmentBegin == nint.Zero || alignmentEnd == nint.Zero
            ? 0
            : alignmentEnd - alignmentBegin;
        int alignmentCount = alignmentBytes >= 0 && alignmentBytes % 16 == 0
            ? checked((int)(alignmentBytes / 16))
            : -1;
        bool alignmentValid = alignmentCount == 2;
        int previousOuterEnd = 0;
        for (int index = 0; index < alignmentCount; index++)
        {
            nint alignment = alignmentBegin + index * 16;
            int outerStart = Marshal.ReadInt32(alignment, 0);
            int outerEnd = Marshal.ReadInt32(alignment, 4);
            int innerStart = Marshal.ReadInt32(alignment, 8);
            int innerEnd = Marshal.ReadInt32(alignment, 12);
            alignmentValid &= outerStart == previousOuterEnd &&
                              outerStart <= innerStart &&
                              innerStart <= innerEnd &&
                              innerEnd <= outerEnd;
            previousOuterEnd = outerEnd;
        }
        alignmentValid &= previousOuterEnd == frameCount;
        return findNamedChild(part, "SND") != nint.Zero &&
               findNamedChild(part, "EpR") != nint.Zero &&
               frameCount > 0 &&
               sampleRate == 44100 &&
               channels == 1 &&
               pcmCount == frameCount * 256 + 2048 &&
               payloadPointer >= 0 &&
               corePointer == payloadPointer + 2048 &&
               alignmentValid;
    }

    private static int ReadCount(nint chunk)
    {
        return chunk == nint.Zero ? -1 : Marshal.ReadInt32(chunk, 0x150);
    }

    private static nint ReadChild(nint chunk, int index)
    {
        int count = ReadCount(chunk);
        if (index < 0 || index >= count)
        {
            return nint.Zero;
        }
        nint children = Marshal.ReadIntPtr(chunk, 0x148);
        return children == nint.Zero
            ? nint.Zero
            : Marshal.ReadIntPtr(children, index * nint.Size);
    }

    private static int ReadInt32(nint pointer, int offset)
    {
        return pointer == nint.Zero ? -1 : Marshal.ReadInt32(pointer, offset);
    }

    private static long ReadInt64(nint pointer, int offset)
    {
        return pointer == nint.Zero ? -1 : Marshal.ReadInt64(pointer, offset);
    }

    private static bool ValidateNamedArticulation(
        nint module,
        nint articulationArray,
        string sourceName,
        string targetName,
        string outputPrefix,
        out long sndPayloadPointer)
    {
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        nint source = articulationArray == nint.Zero
            ? nint.Zero
            : findNamedChild(articulationArray, sourceName);
        nint target = source == nint.Zero
            ? nint.Zero
            : findNamedChild(source, targetName);
        nint part = ReadChild(target, 0);
        nint sndReference = part == nint.Zero
            ? nint.Zero
            : findNamedChild(part, "SND");
        nint eprReference = part == nint.Zero
            ? nint.Zero
            : findNamedChild(part, "EpR");
        int frameCount = ReadInt32(part, 0x1b0);
        int sampleRate = ReadInt32(part, 0x1c8);
        short channels = part == nint.Zero ? (short)-1 : Marshal.ReadInt16(part, 0x1cc);
        int pcmCount = ReadInt32(part, 0x1d0);
        sndPayloadPointer = ReadInt64(part, 0x1b8);
        long sndCorePointer = ReadInt64(part, 0x1c0);
        nint alignmentBegin = part == nint.Zero
            ? nint.Zero
            : Marshal.ReadIntPtr(part, 0x1d8);
        nint alignmentEnd = part == nint.Zero
            ? nint.Zero
            : Marshal.ReadIntPtr(part, 0x1e0);
        long alignmentBytes = alignmentBegin == nint.Zero || alignmentEnd == nint.Zero
            ? 0
            : alignmentEnd - alignmentBegin;
        int alignmentCount = alignmentBytes >= 0 && alignmentBytes % 16 == 0
            ? checked((int)(alignmentBytes / 16))
            : -1;
        int firstOuterStart = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin, 0) : -1;
        int firstOuterEnd = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin, 4) : -1;
        int firstInnerStart = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin, 8) : -1;
        int firstInnerEnd = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin, 12) : -1;
        int secondOuterStart = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin + 16, 0) : -1;
        int secondOuterEnd = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin + 16, 4) : -1;
        int secondInnerStart = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin + 16, 8) : -1;
        int secondInnerEnd = alignmentCount == 2 ? Marshal.ReadInt32(alignmentBegin + 16, 12) : -1;
        bool alignmentValid = alignmentCount == 2 &&
                              firstOuterStart == 0 &&
                              firstOuterEnd == secondOuterStart &&
                              secondOuterEnd == frameCount &&
                              firstOuterStart <= firstInnerStart &&
                              firstInnerStart <= firstInnerEnd &&
                              firstInnerEnd <= firstOuterEnd &&
                              secondOuterStart <= secondInnerStart &&
                              secondInnerStart <= secondInnerEnd &&
                              secondInnerEnd <= secondOuterEnd;

        Console.WriteLine($"{outputPrefix}.source_present={source != nint.Zero}");
        Console.WriteLine($"{outputPrefix}.target_count={ReadCount(source)}");
        Console.WriteLine($"{outputPrefix}.part_count={ReadCount(target)}");
        Console.WriteLine($"{outputPrefix}.frame_count={frameCount}");
        Console.WriteLine($"{outputPrefix}.sample_rate={sampleRate}");
        Console.WriteLine($"{outputPrefix}.channels={channels}");
        Console.WriteLine($"{outputPrefix}.pcm_count={pcmCount}");
        Console.WriteLine($"{outputPrefix}.snd_payload_pointer={sndPayloadPointer}");
        Console.WriteLine($"{outputPrefix}.snd_core_pointer={sndCorePointer}");
        Console.WriteLine($"{outputPrefix}.alignment_count={alignmentCount}");
        if (alignmentCount == 2)
        {
            Console.WriteLine(
                $"{outputPrefix}.alignment[0]=" +
                $"{firstOuterStart},{firstOuterEnd},{firstInnerStart},{firstInnerEnd}");
            Console.WriteLine(
                $"{outputPrefix}.alignment[1]=" +
                $"{secondOuterStart},{secondOuterEnd},{secondInnerStart},{secondInnerEnd}");
        }

        return source != nint.Zero &&
               ReadCount(source) == 1 &&
               target != nint.Zero &&
               ReadCount(target) == 1 &&
               sndReference != nint.Zero &&
               eprReference != nint.Zero &&
               frameCount > 0 &&
               sampleRate == 44100 &&
               channels == 1 &&
               pcmCount == frameCount * 256 + 2048 &&
               sndPayloadPointer >= 0 &&
               sndCorePointer == sndPayloadPointer + 2048 &&
               alignmentValid;
    }

    private static TreePlan ReadTreePlan(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            RequiredString(root, "format") != "vocaloid-ddi-tree-plan-v1")
        {
            throw new InvalidDataException("Unsupported tree-plan format.");
        }

        int language = RequiredInt(root, "language_id");
        if (language < 0 || language > 4)
        {
            throw new InvalidDataException("language_id must be in 0..4.");
        }

        List<PlannedPhoneme> phonemes = new();
        HashSet<string> phonemeNames = new(StringComparer.Ordinal);
        foreach (JsonElement item in RequiredArray(root, "phonemes").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Each planned phoneme must be an object.");
            }
            string name = RequiredString(item, "name");
            bool unvoiced = RequiredBoolean(item, "unvoiced");
            byte[] encoded = System.Text.Encoding.ASCII.GetBytes(name);
            if (encoded.Length is < 1 or > 16 ||
                System.Text.Encoding.ASCII.GetString(encoded) != name ||
                !phonemeNames.Add(name))
            {
                throw new InvalidDataException(
                    $"Invalid, non-ASCII, or duplicate phoneme {name}.");
            }
            phonemes.Add(new PlannedPhoneme(name, unvoiced));
        }
        if (phonemes.Count == 0)
        {
            throw new InvalidDataException("Tree plan has no phonemes.");
        }

        List<PlannedStationary> stationary = new();
        HashSet<string> stationaryPhonemes = new(StringComparer.Ordinal);
        HashSet<int> stationaryIndexes = new();
        HashSet<string> unitIds = new(StringComparer.Ordinal);
        List<string> stationaryOrder = new();
        foreach (JsonElement item in RequiredArray(root, "stationary").EnumerateArray())
        {
            string phoneme = RequiredString(item, "phoneme");
            int stationaryIndex = RequiredInt(item, "stationary_index");
            if (stationaryIndex < 0 ||
                !phonemeNames.Contains(phoneme) ||
                !stationaryPhonemes.Add(phoneme) ||
                !stationaryIndexes.Add(stationaryIndex))
            {
                throw new InvalidDataException(
                    $"Unknown or duplicate stationary phoneme {phoneme}.");
            }
            List<string> ids = ReadUnitIds(item, unitIds);
            stationaryOrder.AddRange(ids);
            stationary.Add(new PlannedStationary(phoneme, stationaryIndex, ids));
        }

        List<PlannedArticulation> articulations = new();
        HashSet<string> articulationEdges = new(StringComparer.Ordinal);
        List<string> articulationOrder = new();
        foreach (JsonElement item in RequiredArray(root, "articulations").EnumerateArray())
        {
            string source = RequiredString(item, "source");
            string target = RequiredString(item, "target");
            int targetIndex = RequiredInt(item, "target_index");
            if (!phonemeNames.Contains(source) || !phonemeNames.Contains(target))
            {
                throw new InvalidDataException(
                    $"Unknown articulation edge {source}->{target}.");
            }
            string key = source + "\0" + target;
            if (!articulationEdges.Add(key))
            {
                throw new InvalidDataException(
                    $"Duplicate articulation edge {source}->{target}.");
            }
            List<string> ids = ReadUnitIds(item, unitIds);
            List<long> sndSourceOffsets = ReadLongArray(item, "snd_source_offsets");
            List<long> eprSourceOffsets = ReadLongArray(item, "epr_source_offsets");
            if (targetIndex < 0 ||
                targetIndex >= phonemes.Count ||
                phonemes[targetIndex].Name != target ||
                sndSourceOffsets.Count != ids.Count ||
                eprSourceOffsets.Count != ids.Count ||
                sndSourceOffsets.Any(value => value <= 0) ||
                eprSourceOffsets.Any(value => value <= 0))
            {
                throw new InvalidDataException(
                    $"Invalid indexes or source offsets for {source}->{target}.");
            }
            articulationOrder.AddRange(ids);
            articulations.Add(
                new PlannedArticulation(
                    source,
                    target,
                    targetIndex,
                    ids,
                    sndSourceOffsets,
                    eprSourceOffsets));
        }

        JsonElement partOrder = RequiredObject(root, "part_order");
        List<string> declaredStationaryOrder = ReadStringArray(
            partOrder,
            "stationary_unit_ids");
        List<string> declaredArticulationOrder = ReadStringArray(
            partOrder,
            "articulation_unit_ids");
        if (!stationaryOrder.SequenceEqual(declaredStationaryOrder) ||
            !articulationOrder.SequenceEqual(declaredArticulationOrder))
        {
            throw new InvalidDataException(
                "part_order differs from the flattened STA/ART groups.");
        }

        JsonElement summary = RequiredObject(root, "summary");
        if (RequiredInt(summary, "phonemes") != phonemes.Count ||
            RequiredInt(summary, "stationary_phonemes") != stationary.Count ||
            RequiredInt(summary, "stationary_parts") != stationaryOrder.Count ||
            RequiredInt(summary, "articulation_edges") != articulations.Count ||
            RequiredInt(summary, "articulation_parts") != articulationOrder.Count ||
            RequiredBoolean(summary, "approval_complete"))
        {
            throw new InvalidDataException("Tree-plan summary differs from its content.");
        }

        return new TreePlan(
            checked((byte)language),
            phonemes,
            stationary,
            articulations);
    }

    private static JsonElement RequiredProperty(
        JsonElement parent,
        string name,
        JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != kind)
        {
            throw new InvalidDataException($"Tree plan requires {name} as {kind}.");
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name) =>
        RequiredProperty(parent, name, JsonValueKind.Array);

    private static JsonElement RequiredObject(JsonElement parent, string name) =>
        RequiredProperty(parent, name, JsonValueKind.Object);

    private static string RequiredString(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name, JsonValueKind.String);
        string? result = value.GetString();
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidDataException($"Tree plan requires non-empty {name}.");
        }
        return result;
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name, JsonValueKind.Number);
        if (!value.TryGetInt32(out int result))
        {
            throw new InvalidDataException($"Tree plan requires integer {name}.");
        }
        return result;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Tree plan requires boolean {name}.");
        }
        return value.GetBoolean();
    }

    private static List<string> ReadStringArray(JsonElement parent, string name)
    {
        List<string> result = new();
        foreach (JsonElement item in RequiredArray(parent, name).EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(item.GetString()))
            {
                throw new InvalidDataException($"Tree plan {name} contains an invalid ID.");
            }
            result.Add(item.GetString()!);
        }
        return result;
    }

    private static List<long> ReadLongArray(JsonElement parent, string name)
    {
        List<long> result = new();
        foreach (JsonElement item in RequiredArray(parent, name).EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out long value))
            {
                throw new InvalidDataException(
                    $"Tree plan {name} contains a non-integer offset.");
            }
            result.Add(value);
        }
        return result;
    }

    private static List<string> ReadUnitIds(
        JsonElement parent,
        HashSet<string> globalIds)
    {
        List<string> result = ReadStringArray(parent, "unit_ids");
        if (result.Count == 0)
        {
            throw new InvalidDataException("Every STA/ART group must contain a unit ID.");
        }
        foreach (string unitId in result)
        {
            if (!globalIds.Add(unitId))
            {
                throw new InvalidDataException($"Duplicate planned unit ID {unitId}.");
            }
        }
        return result;
    }

    private static int AddPlannedStationary(
        nint module,
        nint singer,
        List<PlannedStationary> groups)
    {
        if (groups.Count == 0)
        {
            return 0;
        }
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        SetChunkName setChunkName = GetFunction<SetChunkName>(module, SetChunkNameRva);
        AddChild addChild = GetFunction<AddChild>(module, AddChildRva);
        nint voice = findNamedChild(singer, "voice");
        nint stationaryArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "stationary");
        if (stationaryArray == nint.Zero)
        {
            throw new InvalidOperationException("DSE did not initialize voice/stationary.");
        }

        nint stationary = Marshal.AllocHGlobal(StationarySize);
        Marshal.Copy(new byte[StationarySize], 0, stationary, StationarySize);
        GetFunction<PointerConstructor>(module, StationaryConstructorRva)(stationary);
        setChunkName(stationary, "normal");
        if (addChild(stationaryArray, stationary) < 0)
        {
            throw new InvalidOperationException("Failed to add the planned STA object.");
        }

        int partCount = 0;
        foreach (PlannedStationary group in groups)
        {
            nint phoneme = Marshal.AllocHGlobal(StationaryPhonemeSize);
            Marshal.Copy(new byte[StationaryPhonemeSize], 0, phoneme, StationaryPhonemeSize);
            GetFunction<PointerConstructor>(module, StationaryPhonemeConstructorRva)(phoneme);
            setChunkName(phoneme, group.Phoneme);
            Marshal.WriteInt32(phoneme, 0x160, group.StationaryIndex);
            if (addChild(stationary, phoneme) < 0)
            {
                throw new InvalidOperationException(
                    $"Failed to add planned STAu {group.Phoneme}.");
            }
            foreach (string _ in group.UnitIds)
            {
                nint part = Marshal.AllocHGlobal(StationaryPartSize);
                Marshal.Copy(new byte[StationaryPartSize], 0, part, StationaryPartSize);
                GetFunction<PointerConstructor>(module, StationaryPartConstructorRva)(part);
                Marshal.WriteInt64(part, 0x178, BitConverter.DoubleToInt64Bits(1.0));
                Marshal.WriteInt16(part, 0x1a0, 1);
                if (GetFunction<AddChild>(module, AddStationaryPartRva)(phoneme, part) < 0)
                {
                    throw new InvalidOperationException(
                        $"Failed to add planned STAp {group.Phoneme}.");
                }
                AddEmptyReference(module, part, "SND", 0x3d);
                AddEmptyReference(module, part, "EpR", 0x44);
                partCount++;
            }
        }
        return partCount;
    }

    private static int AddPlannedArticulations(
        nint module,
        nint singer,
        List<PlannedArticulation> groups)
    {
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        SetChunkName setChunkName = GetFunction<SetChunkName>(module, SetChunkNameRva);
        AddChild addChild = GetFunction<AddChild>(module, AddChildRva);
        nint voice = findNamedChild(singer, "voice");
        nint articulationArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "articulation");
        if (articulationArray == nint.Zero)
        {
            throw new InvalidOperationException("DSE did not initialize voice/articulation.");
        }

        int partCount = 0;
        foreach (PlannedArticulation group in groups)
        {
            nint source = findNamedChild(articulationArray, group.Source);
            if (source == nint.Zero)
            {
                throw new InvalidOperationException(
                    $"DSE did not initialize articulation source {group.Source}.");
            }
            nint target = Marshal.AllocHGlobal(ArticulationTargetSize);
            Marshal.Copy(new byte[ArticulationTargetSize], 0, target, ArticulationTargetSize);
            GetFunction<PointerConstructor>(module, ArticulationTargetConstructorRva)(target);
            setChunkName(target, group.Target);
            Marshal.WriteInt32(target, 0x160, 0);
            Marshal.WriteInt32(target, 0x164, group.TargetIndex);
            Marshal.WriteInt32(target, 0x168, 0);
            if (addChild(source, target) < 0)
            {
                throw new InvalidOperationException(
                    $"Failed to add planned ARTu {group.Source}->{group.Target}.");
            }
            if (Environment.GetEnvironmentVariable("TREE_HARNESS_DUMP_PLAN_INDICES") == "1")
            {
                Console.WriteLine(
                    $"planned.artu={group.Source}->{group.Target} " +
                    $"requested_index={group.TargetIndex} " +
                    $"fields=" +
                    string.Join(",", Enumerable.Range(0, 18).Select(index =>
                        $"0x{0x130 + index * 4:x}:{Marshal.ReadInt32(target, 0x130 + index * 4)}")));
            }
            for (int partIndex = 0; partIndex < group.UnitIds.Count; partIndex++)
            {
                nint part = Marshal.AllocHGlobal(ArticulationPartSize);
                Marshal.Copy(new byte[ArticulationPartSize], 0, part, ArticulationPartSize);
                GetFunction<PointerConstructor>(module, ArticulationPartConstructorRva)(part);
                Marshal.WriteInt64(part, 0x10, 0x33);
                Marshal.WriteInt64(part, 0x178, BitConverter.DoubleToInt64Bits(1.0));
                Marshal.WriteInt16(part, 0x1a0, 1);
                setChunkName(part, "default");
                if (addChild(target, part) < 0)
                {
                    throw new InvalidOperationException(
                        $"Failed to add planned ARTp {group.Source}->{group.Target}.");
                }
                AddEmptyReference(
                    module,
                    part,
                    "SND",
                    group.SndSourceOffsets[partIndex]);
                AddEmptyReference(
                    module,
                    part,
                    "EpR",
                    group.EprSourceOffsets[partIndex]);
                partCount++;
            }
        }
        return partCount;
    }

    private static void AddStationaryPhoneme(
        nint module,
        nint singer,
        string phoneme,
        bool addEmptyPart,
        bool addEmptyReferences)
    {
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        SetChunkName setChunkName = GetFunction<SetChunkName>(module, SetChunkNameRva);
        AddChild addChild = GetFunction<AddChild>(module, AddChildRva);

        nint voice = findNamedChild(singer, "voice");
        nint stationaryArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "stationary");
        if (stationaryArray == nint.Zero)
        {
            throw new InvalidOperationException("DSE did not initialize voice/stationary.");
        }

        nint stationary = Marshal.AllocHGlobal(StationarySize);
        Marshal.Copy(new byte[StationarySize], 0, stationary, StationarySize);
        GetFunction<PointerConstructor>(module, StationaryConstructorRva)(stationary);
        setChunkName(stationary, "normal");
        int stationaryIndex = addChild(stationaryArray, stationary);
        if (stationaryIndex < 0)
        {
            throw new InvalidOperationException("Failed to add the STA object.");
        }

        nint stationaryPhoneme = Marshal.AllocHGlobal(StationaryPhonemeSize);
        Marshal.Copy(new byte[StationaryPhonemeSize], 0, stationaryPhoneme, StationaryPhonemeSize);
        GetFunction<PointerConstructor>(module, StationaryPhonemeConstructorRva)(stationaryPhoneme);
        setChunkName(stationaryPhoneme, phoneme);
        Marshal.WriteInt32(stationaryPhoneme, 0x160, 0);
        int phonemeIndex = addChild(stationary, stationaryPhoneme);
        if (phonemeIndex < 0)
        {
            throw new InvalidOperationException("Failed to add the STAu object.");
        }

        if (addEmptyPart)
        {
            nint stationaryPart = Marshal.AllocHGlobal(StationaryPartSize);
            Marshal.Copy(new byte[StationaryPartSize], 0, stationaryPart, StationaryPartSize);
            GetFunction<PointerConstructor>(module, StationaryPartConstructorRva)(stationaryPart);
            Marshal.WriteInt64(stationaryPart, 0x178, BitConverter.DoubleToInt64Bits(1.0));
            Marshal.WriteInt16(stationaryPart, 0x1a0, 1);
            int partIndex = GetFunction<AddChild>(
                module,
                AddStationaryPartRva)(stationaryPhoneme, stationaryPart);
            if (partIndex < 0)
            {
                throw new InvalidOperationException("Failed to add the STAp object.");
            }
            if (addEmptyReferences)
            {
                // The compact tree stores the source-unit positions before each
                // EMPT reference.  0x3d is the canonical first-child position of
                // a mode-0 STAp file (header + child name "SND").  The EpR value
                // here is diagnostic until a real source-unit writer supplies it.
                AddEmptyReference(module, stationaryPart, "SND", 0x3d);
                AddEmptyReference(module, stationaryPart, "EpR", 0x44);
            }
        }
    }

    private static void AddArticulationTransition(
        nint module,
        nint singer,
        string sourcePhoneme,
        string targetPhoneme)
    {
        FindNamedChild findNamedChild = GetFunction<FindNamedChild>(module, FindNamedChildRva);
        SetChunkName setChunkName = GetFunction<SetChunkName>(module, SetChunkNameRva);
        AddChild addChild = GetFunction<AddChild>(module, AddChildRva);

        nint voice = findNamedChild(singer, "voice");
        nint articulationArray = voice == nint.Zero
            ? nint.Zero
            : findNamedChild(voice, "articulation");
        nint sourceArticulation = articulationArray == nint.Zero
            ? nint.Zero
            : findNamedChild(articulationArray, sourcePhoneme);
        if (sourceArticulation == nint.Zero)
        {
            throw new InvalidOperationException(
                $"DSE did not initialize the articulation source {sourcePhoneme}.");
        }

        nint target = Marshal.AllocHGlobal(ArticulationTargetSize);
        Marshal.Copy(new byte[ArticulationTargetSize], 0, target, ArticulationTargetSize);
        GetFunction<PointerConstructor>(module, ArticulationTargetConstructorRva)(target);
        setChunkName(target, targetPhoneme);
        Marshal.WriteInt32(target, 0x160, 0);
        Marshal.WriteInt32(target, 0x164, 0);
        Marshal.WriteInt32(target, 0x168, 0);
        if (addChild(sourceArticulation, target) < 0)
        {
            throw new InvalidOperationException("Failed to add the ARTu object.");
        }

        nint part = Marshal.AllocHGlobal(ArticulationPartSize);
        Marshal.Copy(new byte[ArticulationPartSize], 0, part, ArticulationPartSize);
        GetFunction<PointerConstructor>(module, ArticulationPartConstructorRva)(part);
        // In a one-transition mode-0 ART source unit the ARTp magic begins at
        // 0x33, and its first child SND begins 0x39 bytes later at 0x6c.
        Marshal.WriteInt64(part, 0x10, 0x33);
        Marshal.WriteInt64(part, 0x178, BitConverter.DoubleToInt64Bits(1.0));
        Marshal.WriteInt16(part, 0x1a0, 1);
        setChunkName(part, "default");
        if (addChild(target, part) < 0)
        {
            throw new InvalidOperationException("Failed to add the ARTp object.");
        }

        AddEmptyReference(module, part, "SND", 0x6c);
        AddEmptyReference(module, part, "EpR", 0x73);
    }

    private static void AddEmptyReference(
        nint module,
        nint parent,
        string name,
        long sourceOffset)
    {
        nint reference = Marshal.AllocHGlobal(EmptyChunkSize);
        Marshal.Copy(new byte[EmptyChunkSize], 0, reference, EmptyChunkSize);
        GetFunction<PointerConstructorWithInt>(module, ChunkConstructorRva)(
            reference,
            EmptyChunkMagic);
        Marshal.WriteIntPtr(reference, module + checked((int)EmptyChunkVtableRva));
        Marshal.WriteInt64(reference, 0x10, sourceOffset);
        GetFunction<SetChunkName>(module, SetChunkNameRva)(reference, name);
        int referenceIndex = GetFunction<AddChild>(module, AddChildRva)(parent, reference);
        if (referenceIndex < 0)
        {
            throw new InvalidOperationException($"Failed to add the {name} reference.");
        }
    }
}
