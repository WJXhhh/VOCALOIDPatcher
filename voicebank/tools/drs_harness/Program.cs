using System.Globalization;
using System.Runtime.InteropServices;

internal static class Program
{
    private const long EnvelopeSetValueRva = 0x3af0;
    private const long ConfigConstructorRva = 0x90de0;
    private const long ConfigGetValueRva = 0x2b050;
    private const long CollectionConstructorRva = 0x938c0;
    private const long AnalysisConstructorRva = 0xcdde0;
    private const long RegionAnalysisConstructorRva = 0x956b0;
    private const long AnalyzeBatchRva = 0xd9b10;
    private const long DeriveVoicebankFieldsRva = 0x71e00;
    private const long AllocateFrameFieldsRva = 0x7f1a0;
    private const long ConfigureEnvelopeRva = 0x611e0;
    private const long EvaluateEnvelopeRva = 0x2b30;
    private const long ReconstructFrameFieldsRva = 0x64c30;
    private const long ChunkWriteRva = 0x92f40;
    private const long ChunkReadRva = 0x93000;
    private const long Dse5FrameConstructorRva = 0x12a670;
    private const long Dse5ChunkWriteRva = 0x107060;
    private const long Dse5ChunkReadRva = 0x106e20;

    private const int ConfigSize = 0x35f0;
    private const int CollectionSize = 0x68;
    private const int AnalysisSize = 0xce0;
    private const int StreamSize = 0x80;
    private const int Dse5FrameSize = 0x328;
    private const int DynamicParameterBase = 0x5d0;
    private const int DynamicParameterStride = 0xa0;
    private const ulong MainVoicebankMask = 0x0000002000e00207;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint UnaryConstructor(nint self);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AnalysisConstructor(nint self, int sampleRate, nint config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float ConfigGetValue(nint config, int parameterId, float timeSeconds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long EnvelopeSetValue(nint envelope, double timeSeconds, float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long AnalyzeBatch(
        nint analysis,
        nint soundIo,
        nint collection,
        long unused,
        nint floatPcmDescriptor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long DeriveVoicebankFields(
        nint frame,
        byte setIndex,
        float maximumFrequency,
        float fundamentalFrequency);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long AllocateFrameFields(nint frame, nint descriptor, int mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ConfigureEnvelope(
        nint envelope,
        byte type,
        byte interpolation,
        byte boundaryMode,
        float minimum,
        float maximum,
        float defaultValue,
        int initialCount,
        int autoGrid,
        int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float EvaluateEnvelope(nint envelope, double position);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ReconstructFrameFields(
        nint frame,
        ulong operations,
        int spectrumSize,
        float unusedScale,
        nint mapping,
        float envelopeFrequencyScale,
        float maximumFrequency,
        float frequencyOffset,
        nint warpDescriptor,
        float pitchAttenuation,
        int sampleEnvelopeNearest,
        float lowFrequencyBoundary,
        float highFrequencyBoundary);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ChunkWrite(nint chunk, nint stream);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ChunkRead(nint chunk, nint stream);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string? pathName);

    [DllImport("ucrtbase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint _wfopen(string fileName, string mode);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int fclose(nint stream);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int fseek(nint stream, long offset, int origin);

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        string dsePath = args.Length >= 1
            ? Path.GetFullPath(args[0])
            : @"C:\Program Files\VOCALOID6\Editor\DSE.dll";
        string outputPath = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : Path.GetFullPath("drs_harmonic.sms2");
        double seconds = args.Length >= 3 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 2.0;
        double frequency = args.Length >= 4 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 220.0;
        string pitchMode = args.Length >= 5 ? args[4].ToLowerInvariant() : "auto";
        float externalF0 = args.Length >= 6
            ? float.Parse(args[5], CultureInfo.InvariantCulture)
            : checked((float)frequency);
        string? inputWavePath = args.Length >= 7
            ? Path.GetFullPath(args[6])
            : Environment.GetEnvironmentVariable("DRS_HARNESS_INPUT_WAV") is { Length: > 0 } value
                ? Path.GetFullPath(value)
                : null;
        bool buildMainFields =
            Environment.GetEnvironmentVariable("DRS_HARNESS_BUILD_MAIN_FIELDS") == "1";
        bool deriveFinal = buildMainFields ||
            Environment.GetEnvironmentVariable("DRS_HARNESS_DERIVE_FINAL") == "1";
        bool regionAnalysis =
            Environment.GetEnvironmentVariable("DRS_HARNESS_REGION_ANALYSIS") == "1";
        string? f0BoundaryText =
            Environment.GetEnvironmentVariable("DRS_HARNESS_F0_BOUNDARY_SECONDS");
        double? f0BoundarySeconds = f0BoundaryText is { Length: > 0 }
            ? double.Parse(f0BoundaryText, CultureInfo.InvariantCulture)
            : null;
        string f0BoundaryDirection =
            Environment.GetEnvironmentVariable("DRS_HARNESS_F0_BOUNDARY_DIRECTION")
            ?.ToLowerInvariant() ?? "sil-to-voiced";

        if (!File.Exists(dsePath))
        {
            Console.Error.WriteLine($"DSE not found: {dsePath}");
            return 2;
        }

        if (pitchMode is not ("auto" or "external" or "unvoiced"))
        {
            Console.Error.WriteLine(
                "Pitch mode must be 'auto', 'external', or 'unvoiced'.");
            return 2;
        }

        if (externalF0 is < 40.0f or > 1000.0f)
        {
            Console.Error.WriteLine("External F0 must be between 40 and 1000 Hz.");
            return 2;
        }
        if (f0BoundarySeconds is not null && pitchMode != "external")
        {
            Console.Error.WriteLine("An F0 boundary requires external pitch mode.");
            return 2;
        }
        if (f0BoundaryDirection is not ("sil-to-voiced" or "voiced-to-sil"))
        {
            Console.Error.WriteLine(
                "DRS_HARNESS_F0_BOUNDARY_DIRECTION must be sil-to-voiced or voiced-to-sil.");
            return 2;
        }

        int sampleRate;
        float[] samples;
        if (inputWavePath is null)
        {
            sampleRate = 44100;
            int generatedSampleCount = checked((int)Math.Round(seconds * sampleRate));
            samples = CreateHarmonicSignal(generatedSampleCount, sampleRate, frequency);
            Console.WriteLine(
                $"input=generated_harmonic frequency={frequency:R}Hz samples={samples.Length}");
        }
        else
        {
            try
            {
                (samples, sampleRate) = ReadWaveAsMonoFloatPcm(inputWavePath);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or OverflowException)
            {
                Console.Error.WriteLine($"Unable to read input WAV: {error.Message}");
                return 2;
            }
            seconds = samples.Length / (double)sampleRate;
            Console.WriteLine(
                $"input={inputWavePath} sample_rate={sampleRate} samples={samples.Length}");
        }

        if (sampleRate != 44100)
        {
            Console.Error.WriteLine("Traditional voicebank input must be 44100 Hz.");
            return 2;
        }
        if (seconds <= 0.25 || seconds > 30.0)
        {
            Console.Error.WriteLine("Duration must be greater than 0.25 and at most 30 seconds.");
            return 2;
        }
        if (f0BoundarySeconds is double durationBoundary &&
            (!double.IsFinite(durationBoundary) ||
             durationBoundary <= 0.0 ||
             durationBoundary >= seconds))
        {
            Console.Error.WriteLine(
                $"F0 boundary must be inside the recording duration 0..{seconds:R} seconds.");
            return 2;
        }

        int sampleCount = samples.Length;

        string editorDirectory = Path.GetDirectoryName(dsePath)!;
        if (!SetDllDirectoryW(editorDirectory))
        {
            Console.Error.WriteLine($"SetDllDirectoryW failed: {Marshal.GetLastWin32Error()}");
            return 3;
        }

        nint module = NativeLibrary.Load(dsePath);
        Console.WriteLine($"module=0x{module:x}");

        nint config = AllocateZeroed(ConfigSize);
        nint collection = AllocateZeroed(CollectionSize);
        nint analysis = AllocateZeroed(AnalysisSize);
        nint pcm = Marshal.AllocHGlobal(checked(sampleCount * sizeof(float)));
        nint descriptor = AllocateZeroed(0x10);

        try
        {
            Marshal.Copy(samples, 0, pcm, samples.Length);

            GetFunction<UnaryConstructor>(module, ConfigConstructorRva)(config);
            GetFunction<UnaryConstructor>(module, CollectionConstructorRva)(collection);

            ConfigGetValue getConfigValue = GetFunction<ConfigGetValue>(module, ConfigGetValueRva);
            EnvelopeSetValue setEnvelopeValue = GetFunction<EnvelopeSetValue>(module, EnvelopeSetValueRva);
            if (Environment.GetEnvironmentVariable("DRS_HARNESS_DUMP_CONFIG") == "1")
            {
                for (int id = 0; id <= 0x87; id++)
                {
                    Console.WriteLine($"config[0x{id:x2}]={getConfigValue(config, id, 0.0f):R}");
                }
            }

            float defaultFrameRate = ReadSingle(config, 0x18);
            int defaultHop = (int)Math.Round(sampleRate / defaultFrameRate);
            Console.WriteLine($"config.default_frame_rate={defaultFrameRate:R} default_hop={defaultHop}");

            const float referenceFrameRate = 172.265625f;
            WriteSingle(config, 0x18, referenceFrameRate);
            Console.WriteLine($"config.frame_rate={ReadSingle(config, 0x18):R} hop={Math.Round(sampleRate / referenceFrameRate)}");

            float defaultRegionSplit = getConfigValue(config, 0x2f, 0.0f);
            if (regionAnalysis)
            {
                SetDynamicParameterConstant(config, setEnvelopeValue, 0x2f, 1.0f);
            }
            Console.WriteLine(
                $"config.region_split.default={defaultRegionSplit:R} " +
                $"active={getConfigValue(config, 0x2f, 0.0f):R}");

            if (pitchMode is "external" or "unvoiced")
            {
                SetDynamicParameterConstant(config, setEnvelopeValue, 0x14, 0.0f);
                if (pitchMode == "unvoiced")
                {
                    SetDynamicParameterConstant(config, setEnvelopeValue, 0x0d, 0.0f);
                }
                else if (f0BoundarySeconds is double f0Boundary)
                {
                    SetDynamicParameterStep(
                        config,
                        setEnvelopeValue,
                        0x0d,
                        f0Boundary,
                        seconds,
                        externalF0,
                        f0BoundaryDirection == "sil-to-voiced");
                    const float probeEpsilon = 0.000002f;
                    float boundaryPosition = checked((float)(f0Boundary / seconds));
                    Console.WriteLine(
                        $"config.f0_boundary={f0Boundary:R}s position={boundaryPosition:R} " +
                        $"direction={f0BoundaryDirection} " +
                        $"before={getConfigValue(config, 0x0d, boundaryPosition - probeEpsilon):R} " +
                        $"at={getConfigValue(config, 0x0d, boundaryPosition):R} " +
                        $"after={getConfigValue(config, 0x0d, boundaryPosition + probeEpsilon):R}");
                }
                else
                {
                    SetDynamicParameterConstant(config, setEnvelopeValue, 0x0d, externalF0);
                }
            }
            if (buildMainFields)
            {
                SetDynamicParameterConstant(config, setEnvelopeValue, 0x04, 350.0f);
            }

            Console.WriteLine(
                $"config.pitch_mode={pitchMode} use_auto_f0={getConfigValue(config, 0x14, 0.0f):R} " +
                $"f0_or_seed={getConfigValue(config, 0x0d, 0.0f):R} " +
                $"harmonic_slots={getConfigValue(config, 0x04, 0.0f):R}");

            long analysisConstructorRva = regionAnalysis
                ? RegionAnalysisConstructorRva
                : AnalysisConstructorRva;
            GetFunction<AnalysisConstructor>(module, analysisConstructorRva)(
                analysis,
                sampleRate,
                config);
            Console.WriteLine(
                $"analysis.type={(regionAnalysis ? "region" : "base")} " +
                $"vtable=0x{Marshal.ReadIntPtr(analysis):x}");

            Marshal.WriteIntPtr(descriptor, 0x00, pcm);
            Marshal.WriteInt32(descriptor, 0x08, sampleCount);
            Marshal.WriteInt32(descriptor, 0x0c, sampleRate);

            long analyzeResult = GetFunction<AnalyzeBatch>(module, AnalyzeBatchRva)(
                analysis,
                nint.Zero,
                collection,
                0,
                descriptor);
            Console.WriteLine($"analyze.result={analyzeResult}");

            PrintCollectionSummary(analysis, collection);
            if (deriveFinal)
            {
                DeriveFinalFields(module, analysis, sampleRate);
            }
            if (buildMainFields)
            {
                BuildMainVoicebankFields(module, analysis, sampleRate);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            nint file = _wfopen(outputPath, "wb");
            if (file == nint.Zero)
            {
                Console.Error.WriteLine($"Unable to open output: {outputPath}");
                return 4;
            }

            nint stream = AllocateZeroed(StreamSize);
            try
            {
                Marshal.WriteIntPtr(stream, 0x08, file);
                // CSMSFrame's writer intersects its field mask with stream+0x20.
                // Preserve every field produced by the analyzer in this diagnostic output.
                Marshal.WriteInt64(
                    stream,
                    0x20,
                    buildMainFields ? unchecked((long)MainVoicebankMask) : -1);
                long writeResult = GetFunction<ChunkWrite>(module, ChunkWriteRva)(collection, stream);
                long bytesWritten = Marshal.ReadInt64(stream, 0x00);
                Console.WriteLine($"write.result={writeResult} stream_bytes={bytesWritten}");
            }
            finally
            {
                fclose(file);
                Marshal.FreeHGlobal(stream);
            }

            long fileBytes = new FileInfo(outputPath).Length;
            Console.WriteLine($"output={outputPath}");
            Console.WriteLine($"output_bytes={fileBytes}");
            bool readbackValid = ReadBackCollection(module, outputPath);
            bool dse5InteropValid;
            if (deriveFinal)
            {
                // DSE5 accepts a raw DRS frame, but trying to write a DRS-owned frame
                // after the in-place final-field derivation grows without bound. Keep
                // that unsafe cross-class experiment out of the normal derived run.
                Console.WriteLine("dse5.frame_interop=skipped_for_derived_drs_frame");
                dse5InteropValid = true;
            }
            else
            {
                dse5InteropValid = RoundTripFirstFrameThroughDse5(module, outputPath);
            }
            return analyzeResult == 0 && fileBytes > 0 && readbackValid && dse5InteropValid ? 0 : 5;
        }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(pcm);
            // The reverse-engineered destructors are deliberately not invoked yet.
            // This harness is a short-lived process, so the OS reclaims DSE-owned allocations.
            Marshal.FreeHGlobal(analysis);
            Marshal.FreeHGlobal(collection);
            Marshal.FreeHGlobal(config);
        }
    }

    private static float[] CreateHarmonicSignal(int sampleCount, int sampleRate, double frequency)
    {
        var result = new float[sampleCount];
        int harmonicCount = Math.Min(80, (int)Math.Floor((sampleRate * 0.48) / frequency));
        double peak = 0.0;
        for (int i = 0; i < result.Length; i++)
        {
            double value = 0.0;
            for (int harmonic = 1; harmonic <= harmonicCount; harmonic++)
            {
                double harmonicFrequency = harmonic * frequency;
                double formantWeight =
                    0.15 +
                    2.0 * Gaussian(harmonicFrequency, 700.0, 120.0) +
                    1.3 * Gaussian(harmonicFrequency, 1200.0, 180.0) +
                    0.8 * Gaussian(harmonicFrequency, 2600.0, 300.0);
                double amplitude = formantWeight / harmonic;
                value += amplitude * Math.Sin(
                    2.0 * Math.PI * harmonicFrequency * i / sampleRate + harmonic * 0.31);
            }

            result[i] = (float)value;
            peak = Math.Max(peak, Math.Abs(value));
        }

        int fadeSamples = Math.Min(sampleCount / 4, sampleRate / 10);
        for (int i = 0; i < result.Length; i++)
        {
            double fadeIn = Math.Min(1.0, (double)i / fadeSamples);
            double fadeOut = Math.Min(1.0, (double)(result.Length - 1 - i) / fadeSamples);
            double envelope = Math.Min(fadeIn, fadeOut);
            result[i] = (float)(12000.0 * envelope * result[i] / peak);
        }

        return result;
    }

    private static (float[] Samples, int SampleRate) ReadWaveAsMonoFloatPcm(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12 ||
            new string(reader.ReadChars(4)) != "RIFF" ||
            reader.ReadUInt32() + 8 > stream.Length ||
            new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("expected a complete RIFF/WAVE file");
        }

        ushort formatTag = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        byte[]? audioBytes = null;
        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long chunkStart = stream.Position;
            long chunkEnd = checked(chunkStart + chunkSize);
            if (chunkEnd > stream.Length)
            {
                throw new InvalidDataException($"WAV chunk '{chunkId}' extends past EOF");
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("WAV fmt chunk is shorter than 16 bytes");
                }
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
            }
            else if (chunkId == "data")
            {
                if (chunkSize > int.MaxValue)
                {
                    throw new InvalidDataException("WAV data chunk is too large");
                }
                audioBytes = reader.ReadBytes(checked((int)chunkSize));
                if (audioBytes.Length != chunkSize)
                {
                    throw new InvalidDataException("WAV data chunk is truncated");
                }
            }

            stream.Position = chunkEnd + (chunkSize & 1);
        }

        if (audioBytes is null || sampleRate <= 0 || channels == 0)
        {
            throw new InvalidDataException("WAV is missing fmt or data");
        }
        int bytesPerSample = bitsPerSample / 8;
        if (blockAlign != channels * bytesPerSample ||
            (formatTag, bitsPerSample) is not ((1, 16) or (3, 32)))
        {
            throw new InvalidDataException(
                $"supported WAV formats are PCM16 and IEEE-float32; got " +
                $"format={formatTag}, channels={channels}, bits={bitsPerSample}");
        }
        if (audioBytes.Length % blockAlign != 0)
        {
            throw new InvalidDataException("WAV data length is not frame-aligned");
        }

        int frameCount = audioBytes.Length / blockAlign;
        var samples = new float[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            double sum = 0.0;
            int frameOffset = frame * blockAlign;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = frameOffset + channel * bytesPerSample;
                sum += formatTag == 1
                    ? BitConverter.ToInt16(audioBytes, offset)
                    : BitConverter.ToSingle(audioBytes, offset) * 32768.0;
            }

            double value = sum / channels;
            if (!double.IsFinite(value))
            {
                throw new InvalidDataException($"WAV contains a non-finite sample at frame {frame}");
            }
            samples[frame] = checked((float)value);
        }
        return (samples, sampleRate);
    }

    private static double Gaussian(double x, double center, double width)
    {
        double normalized = (x - center) / width;
        return Math.Exp(-0.5 * normalized * normalized);
    }

    private static void PrintCollectionSummary(nint analysis, nint collection)
    {
        int genericCount = Marshal.ReadInt32(collection, 0x44);
        nint track = Marshal.ReadIntPtr(analysis, 0xcd0);
        Console.WriteLine($"collection.generics={genericCount} track=0x{track:x}");
        if (track == nint.Zero)
        {
            return;
        }

        int regionCount = Marshal.ReadInt32(track, 0x140);
        Console.WriteLine($"track.regions={regionCount}");
        nint regionArray = Marshal.ReadIntPtr(track, 0x148);
        long totalFrames = 0;
        int? minimumHarmonicCount = null;
        int? maximumHarmonicCount = null;
        for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
        {
            nint region = Marshal.ReadIntPtr(regionArray, regionIndex * nint.Size);
            int frameCount = Marshal.ReadInt32(region, 0x18);
            totalFrames += frameCount;
            byte regionType = Marshal.ReadByte(region, 0x84);
            Console.WriteLine(
                $"region[{regionIndex}].type={regionType} frames={frameCount}");
            nint frameArray = Marshal.ReadIntPtr(region, 0x20);
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                nint frame = Marshal.ReadIntPtr(frameArray, frameIndex * nint.Size);
                ulong mask = unchecked((ulong)Marshal.ReadInt64(frame, 0x50));
                if ((mask & 0x7) == 0)
                {
                    continue;
                }

                int harmonicCount = Marshal.ReadInt32(frame, 0x58);
                minimumHarmonicCount = minimumHarmonicCount is null
                    ? harmonicCount
                    : Math.Min(minimumHarmonicCount.Value, harmonicCount);
                maximumHarmonicCount = maximumHarmonicCount is null
                    ? harmonicCount
                    : Math.Max(maximumHarmonicCount.Value, harmonicCount);
            }
        }

        Console.WriteLine($"track.total_frames={totalFrames}");
        Console.WriteLine(
            $"track.harmonic_count_range={minimumHarmonicCount?.ToString() ?? "none"}.." +
            $"{maximumHarmonicCount?.ToString() ?? "none"}");
    }

    private static void DeriveFinalFields(nint module, nint analysis, int sampleRate)
    {
        nint track = Marshal.ReadIntPtr(analysis, 0xcd0);
        if (track == nint.Zero)
        {
            Console.WriteLine("derive_final.skipped=no_track");
            return;
        }

        DeriveVoicebankFields derive = GetFunction<DeriveVoicebankFields>(
            module,
            DeriveVoicebankFieldsRva);
        var beforeMasks = new Dictionary<ulong, int>();
        var afterMasks = new Dictionary<ulong, int>();
        int attempted = 0;
        int skipped = 0;
        int failed = 0;

        int regionCount = Marshal.ReadInt32(track, 0x140);
        nint regionArray = Marshal.ReadIntPtr(track, 0x148);
        for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
        {
            nint region = Marshal.ReadIntPtr(regionArray, regionIndex * nint.Size);
            int frameCount = Marshal.ReadInt32(region, 0x18);
            nint frameArray = Marshal.ReadIntPtr(region, 0x20);
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                nint frame = Marshal.ReadIntPtr(frameArray, frameIndex * nint.Size);
                ulong beforeMask = unchecked((ulong)Marshal.ReadInt64(frame, 0x50));
                IncrementMaskCount(beforeMasks, beforeMask);

                float storedPitch = ReadSingle(frame, 0xf0);
                double fundamentalFrequency = (beforeMask & (1UL << 31)) != 0
                    ? 440.0 * Math.Pow(2.0, storedPitch / 1200.0)
                    : storedPitch;
                if (!double.IsFinite(fundamentalFrequency) ||
                    fundamentalFrequency <= 0.0 ||
                    fundamentalFrequency > sampleRate / 2.0)
                {
                    skipped++;
                    IncrementMaskCount(afterMasks, beforeMask);
                    continue;
                }

                long result = derive(
                    frame,
                    0,
                    sampleRate / 2.0f,
                    checked((float)fundamentalFrequency));
                attempted++;
                if (result != 0)
                {
                    failed++;
                }

                ulong afterMask = unchecked((ulong)Marshal.ReadInt64(frame, 0x50));
                IncrementMaskCount(afterMasks, afterMask);
            }
        }

        Console.WriteLine(
            $"derive_final.attempted={attempted} skipped={skipped} failed={failed} " +
            $"before={FormatMaskCounts(beforeMasks)} after={FormatMaskCounts(afterMasks)}");
    }

    private static void BuildMainVoicebankFields(nint module, nint analysis, int sampleRate)
    {
        nint track = Marshal.ReadIntPtr(analysis, 0xcd0);
        if (track == nint.Zero)
        {
            Console.WriteLine("build_main_fields.skipped=no_track");
            return;
        }

        AllocateFrameFields allocateFields = GetFunction<AllocateFrameFields>(
            module,
            AllocateFrameFieldsRva);
        ConfigureEnvelope configureEnvelope = GetFunction<ConfigureEnvelope>(
            module,
            ConfigureEnvelopeRva);
        EvaluateEnvelope evaluateEnvelope = GetFunction<EvaluateEnvelope>(
            module,
            EvaluateEnvelopeRva);
        EnvelopeSetValue setEnvelopeValue = GetFunction<EnvelopeSetValue>(
            module,
            EnvelopeSetValueRva);
        ReconstructFrameFields reconstruct = GetFunction<ReconstructFrameFields>(
            module,
            ReconstructFrameFieldsRva);
        nint envelopeDescriptor = AllocateZeroed(0x48);
        nint harmonicDescriptor = AllocateZeroed(0x48);
        Marshal.WriteInt64(envelopeDescriptor, 0x00, unchecked((long)(1UL << 37)));
        Marshal.WriteInt64(harmonicDescriptor, 0x00, 0x7);
        Marshal.WriteInt32(harmonicDescriptor, 0x08, 350);

        var beforeMasks = new Dictionary<ulong, int>();
        var afterMasks = new Dictionary<ulong, int>();
        int attempted = 0;
        int skipped = 0;
        int failed = 0;
        try
        {
            int regionCount = Marshal.ReadInt32(track, 0x140);
            nint regionArray = Marshal.ReadIntPtr(track, 0x148);
            for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
            {
                nint region = Marshal.ReadIntPtr(regionArray, regionIndex * nint.Size);
                int frameCount = Marshal.ReadInt32(region, 0x18);
                nint frameArray = Marshal.ReadIntPtr(region, 0x20);
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    nint frame = Marshal.ReadIntPtr(frameArray, frameIndex * nint.Size);
                    ulong beforeMask = unchecked((ulong)Marshal.ReadInt64(frame, 0x50));
                    IncrementMaskCount(beforeMasks, beforeMask);
                    if ((beforeMask & (1UL << 7)) == 0 ||
                        (beforeMask & ((1UL << 21) | (1UL << 22))) == 0)
                    {
                        skipped++;
                        IncrementMaskCount(afterMasks, NormalizePitchEncoding(frame, beforeMask));
                        continue;
                    }

                    float fundamentalFrequency = ReadPitchHertz(frame, beforeMask);
                    if (!float.IsFinite(fundamentalFrequency) ||
                        fundamentalFrequency <= 0.0f ||
                        fundamentalFrequency > sampleRate / 2.0f)
                    {
                        skipped++;
                        IncrementMaskCount(afterMasks, NormalizePitchEncoding(frame, beforeMask));
                        continue;
                    }

                    long allocateResult = allocateFields(frame, envelopeDescriptor, 0);
                    nint sourceEnvelope = Marshal.ReadIntPtr(frame, 0x108);
                    nint destinationEnvelope = Marshal.ReadIntPtr(frame, 0x138);
                    long envelopeResult = sourceEnvelope != nint.Zero && destinationEnvelope != nint.Zero
                        ? BuildHarmonicEnvelope(
                            sourceEnvelope,
                            destinationEnvelope,
                            fundamentalFrequency,
                            sampleRate / 2.0f,
                            configureEnvelope,
                            evaluateEnvelope,
                            setEnvelopeValue)
                        : -1;
                    long normalizeFrequencyResult = reconstruct(
                        frame,
                        4,
                        0,
                        1.0f,
                        nint.Zero,
                        sampleRate / 2.0f,
                        sampleRate / 2.0f + fundamentalFrequency,
                        0.0f,
                        nint.Zero,
                        0.0f,
                        0,
                        float.Epsilon,
                        float.Epsilon);
                    NormalizeHarmonicGrid(
                        frame,
                        fundamentalFrequency,
                        sampleRate / 2.0f);
                    long reconstructAmplitudeResult = reconstruct(
                        frame,
                        1,
                        0,
                        1.0f,
                        nint.Zero,
                        sampleRate / 2.0f,
                        sampleRate / 2.0f + fundamentalFrequency,
                        0.0f,
                        nint.Zero,
                        0.0f,
                        0,
                        float.Epsilon,
                        float.Epsilon);
                    ulong reconstructedMask = unchecked((ulong)Marshal.ReadInt64(frame, 0x50));
                    reconstructedMask |= 1UL << 9;
                    Marshal.WriteInt64(frame, 0x50, unchecked((long)reconstructedMask));
                    long harmonicAllocateResult = allocateFields(frame, harmonicDescriptor, 1);
                    NormalizeHarmonicGrid(
                        frame,
                        fundamentalFrequency,
                        sampleRate / 2.0f);
                    reconstructedMask = NormalizePitchEncoding(frame, reconstructedMask);
                    attempted++;
                    if (allocateResult != 0 || envelopeResult != 0 ||
                        normalizeFrequencyResult != 0 || reconstructAmplitudeResult != 0 ||
                        harmonicAllocateResult != 0)
                    {
                        failed++;
                    }

                    IncrementMaskCount(afterMasks, reconstructedMask);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(harmonicDescriptor);
            Marshal.FreeHGlobal(envelopeDescriptor);
        }

        Console.WriteLine(
            $"build_main_fields.attempted={attempted} skipped={skipped} failed={failed} " +
            $"before={FormatMaskCounts(beforeMasks)} after={FormatMaskCounts(afterMasks)} " +
            $"serialization_mask=0x{MainVoicebankMask:x16}");
    }

    private static long BuildHarmonicEnvelope(
        nint source,
        nint destination,
        float fundamentalFrequency,
        float maximumFrequency,
        ConfigureEnvelope configure,
        EvaluateEnvelope evaluate,
        EnvelopeSetValue setValue)
    {
        int harmonicCount = (int)Math.Floor(maximumFrequency / fundamentalFrequency);
        double firstPosition = fundamentalFrequency / maximumFrequency;
        double lastPosition = harmonicCount * (double)fundamentalFrequency / maximumFrequency;
        float firstValue = evaluate(source, firstPosition);
        float lastValue = evaluate(source, lastPosition);
        long result = configure(
            destination,
            3,
            1,
            3,
            -20000.0f,
            20000.0f,
            0.0f,
            2,
            0,
            0);

        result |= setValue(destination, 0.0, firstValue);
        for (int harmonic = 1; harmonic <= harmonicCount; harmonic++)
        {
            double position = harmonic * (double)fundamentalFrequency / maximumFrequency;
            if (position >= 1.0)
            {
                break;
            }

            float value = harmonic switch
            {
                1 => firstValue,
                _ when harmonic == harmonicCount => lastValue,
                _ => evaluate(source, position),
            };
            result |= setValue(destination, position, value);
        }
        result |= setValue(destination, 1.0, lastValue);
        return result;
    }

    private static void NormalizeHarmonicGrid(
        nint frame,
        float fundamentalFrequency,
        float maximumFrequency)
    {
        int count = Marshal.ReadInt32(frame, 0x58);
        nint frequencies = Marshal.ReadIntPtr(frame, 0x60);
        nint amplitudes = Marshal.ReadIntPtr(frame, 0x68);
        nint phases = Marshal.ReadIntPtr(frame, 0x70);
        if (count <= 0 || frequencies == nint.Zero ||
            amplitudes == nint.Zero || phases == nint.Zero)
        {
            return;
        }

        int activeCount = Math.Min(
            count,
            (int)Math.Ceiling(maximumFrequency / fundamentalFrequency));
        for (int index = 0; index < count; index++)
        {
            int offset = checked(index * sizeof(float));
            if (index < activeCount)
            {
                WriteSingle(
                    frequencies,
                    offset,
                    (index + 1) * fundamentalFrequency);
                float phase = ReadSingle(phases, offset);
                if (float.IsFinite(phase))
                {
                    WriteSingle(
                        phases,
                        offset,
                        MathF.IEEERemainder(phase, 2.0f * MathF.PI));
                }
            }
            else
            {
                WriteSingle(frequencies, offset, 0.0f);
                WriteSingle(amplitudes, offset, 10000.0f);
                WriteSingle(phases, offset, 0.0f);
            }
        }
    }

    private static float ReadPitchHertz(nint frame, ulong mask)
    {
        float storedPitch = ReadSingle(frame, 0xf0);
        if ((mask & (1UL << 31)) == 0)
        {
            return storedPitch;
        }

        return checked((float)(440.0 * Math.Pow(2.0, storedPitch / 1200.0)));
    }

    private static ulong NormalizePitchEncoding(nint frame, ulong mask)
    {
        if ((mask & (1UL << 31)) == 0)
        {
            return mask;
        }

        float cents = ReadSingle(frame, 0xf0);
        double hertz = 440.0 * Math.Pow(2.0, cents / 1200.0);
        if (double.IsFinite(hertz) && hertz > 0.0)
        {
            WriteSingle(frame, 0xf0, checked((float)hertz));
        }
        mask &= ~(1UL << 31);
        Marshal.WriteInt64(frame, 0x50, unchecked((long)mask));
        return mask;
    }

    private static void IncrementMaskCount(Dictionary<ulong, int> counts, ulong mask)
    {
        counts.TryGetValue(mask, out int count);
        counts[mask] = count + 1;
    }

    private static string FormatMaskCounts(Dictionary<ulong, int> counts)
    {
        return string.Join(
            ',',
            counts.OrderBy(pair => pair.Key)
                .Select(pair => $"0x{pair.Key:x16}:{pair.Value}"));
    }

    private static bool ReadBackCollection(nint module, string path)
    {
        nint file = _wfopen(path, "rb");
        if (file == nint.Zero)
        {
            Console.Error.WriteLine($"Unable to reopen output: {path}");
            return false;
        }

        nint collection = AllocateZeroed(CollectionSize);
        nint stream = AllocateZeroed(StreamSize);
        try
        {
            GetFunction<UnaryConstructor>(module, CollectionConstructorRva)(collection);
            Marshal.WriteIntPtr(stream, 0x08, file);
            long readResult = GetFunction<ChunkRead>(module, ChunkReadRva)(collection, stream);

            int genericCount = Marshal.ReadInt32(collection, 0x44);
            long totalFrames = 0;
            if (genericCount > 0)
            {
                nint genericArray = Marshal.ReadIntPtr(collection, 0x48);
                for (int genericIndex = 0; genericIndex < genericCount; genericIndex++)
                {
                    nint generic = Marshal.ReadIntPtr(genericArray, genericIndex * nint.Size);
                    int trackCount = Marshal.ReadInt32(generic, 0x48);
                    nint trackArray = Marshal.ReadIntPtr(generic, 0x50);
                    for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
                    {
                        nint track = Marshal.ReadIntPtr(trackArray, trackIndex * nint.Size);
                        int regionCount = Marshal.ReadInt32(track, 0x140);
                        nint regionArray = Marshal.ReadIntPtr(track, 0x148);
                        for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
                        {
                            nint region = Marshal.ReadIntPtr(regionArray, regionIndex * nint.Size);
                            totalFrames += Marshal.ReadInt32(region, 0x18);
                        }
                    }
                }
            }

            long bytesRead = Marshal.ReadInt64(stream, 0x00);
            Console.WriteLine(
                $"readback.result={readResult} stream_bytes={bytesRead} " +
                $"generics={genericCount} total_frames={totalFrames}");
            return readResult == 0 && genericCount > 0 && totalFrames > 0;
        }
        finally
        {
            fclose(file);
            Marshal.FreeHGlobal(stream);
            Marshal.FreeHGlobal(collection);
        }
    }

    private static bool RoundTripFirstFrameThroughDse5(nint module, string sourcePath)
    {
        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        int frameOffset = FindMagic(sourceBytes, "FRM2"u8);
        if (frameOffset < 0 || frameOffset + 28 > sourceBytes.Length)
        {
            Console.Error.WriteLine("dse5.frame_interop=no_embedded_frm2");
            return false;
        }

        int sourceFrameSize = BitConverter.ToInt32(sourceBytes, frameOffset + 4);
        if (sourceFrameSize < 28 || frameOffset + sourceFrameSize > sourceBytes.Length)
        {
            Console.Error.WriteLine($"dse5.frame_interop=invalid_size offset={frameOffset} size={sourceFrameSize}");
            return false;
        }

        ulong sourceMask = BitConverter.ToUInt64(sourceBytes, frameOffset + 20);
        string roundTripPath = sourcePath + ".dse5.frm2";
        nint frame = AllocateZeroed(Dse5FrameSize);
        nint readStream = AllocateZeroed(StreamSize);
        nint inputFile = nint.Zero;
        nint writeStream = AllocateZeroed(StreamSize);
        nint outputFile = nint.Zero;

        try
        {
            GetFunction<UnaryConstructor>(module, Dse5FrameConstructorRva)(frame);

            inputFile = _wfopen(sourcePath, "rb");
            if (inputFile == nint.Zero || fseek(inputFile, frameOffset, 0) != 0)
            {
                Console.Error.WriteLine("dse5.frame_interop=input_seek_failed");
                return false;
            }

            Marshal.WriteInt64(readStream, 0x00, frameOffset);
            Marshal.WriteIntPtr(readStream, 0x08, inputFile);
            long readResult = GetFunction<ChunkRead>(module, Dse5ChunkReadRva)(frame, readStream);
            ulong memoryMask = unchecked((ulong)Marshal.ReadInt64(frame, 0x150));
            int kind = Marshal.ReadInt32(frame, 0x140);
            double timeSeconds = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(frame, 0x148));
            long bytesAfterRead = Marshal.ReadInt64(readStream, 0x00);
            Console.WriteLine(
                $"dse5.read.result={readResult} offset={frameOffset} source_size={sourceFrameSize} " +
                $"source_mask=0x{sourceMask:x16} memory_mask=0x{memoryMask:x16} " +
                $"kind={kind} time={timeSeconds:R} stream_position={bytesAfterRead}");

            fclose(inputFile);
            inputFile = nint.Zero;

            outputFile = _wfopen(roundTripPath, "wb");
            if (outputFile == nint.Zero)
            {
                Console.Error.WriteLine($"dse5.frame_interop=output_open_failed path={roundTripPath}");
                return false;
            }

            Marshal.WriteIntPtr(writeStream, 0x08, outputFile);
            Marshal.WriteInt64(writeStream, 0x20, -1);
            long writeResult = GetFunction<ChunkWrite>(module, Dse5ChunkWriteRva)(frame, writeStream);
            long bytesWritten = Marshal.ReadInt64(writeStream, 0x00);
            fclose(outputFile);
            outputFile = nint.Zero;

            byte[] roundTripBytes = File.ReadAllBytes(roundTripPath);
            ulong roundTripMask = roundTripBytes.Length >= 28
                ? BitConverter.ToUInt64(roundTripBytes, 20)
                : 0;
            bool byteExact = roundTripBytes.AsSpan().SequenceEqual(
                sourceBytes.AsSpan(frameOffset, sourceFrameSize));
            Console.WriteLine(
                $"dse5.write.result={writeResult} output={roundTripPath} bytes={bytesWritten} " +
                $"mask=0x{roundTripMask:x16} byte_exact={byteExact}");

            return readResult == 0 && writeResult == 0 &&
                   bytesAfterRead == frameOffset + sourceFrameSize &&
                   bytesWritten == roundTripBytes.Length && roundTripBytes.Length >= 28;
        }
        finally
        {
            if (inputFile != nint.Zero)
            {
                fclose(inputFile);
            }
            if (outputFile != nint.Zero)
            {
                fclose(outputFile);
            }
            Marshal.FreeHGlobal(writeStream);
            Marshal.FreeHGlobal(readStream);
            Marshal.FreeHGlobal(frame);
        }
    }

    private static int FindMagic(byte[] data, ReadOnlySpan<byte> magic)
    {
        for (int offset = 0; offset <= data.Length - magic.Length; offset++)
        {
            if (data.AsSpan(offset, magic.Length).SequenceEqual(magic))
            {
                return offset;
            }
        }

        return -1;
    }

    private static T GetFunction<T>(nint module, long rva) where T : Delegate
    {
        nint address = module + checked((int)rva);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void SetDynamicParameterConstant(
        nint config,
        EnvelopeSetValue setValue,
        int parameterId,
        float value)
    {
        if ((uint)parameterId > 0x4c)
        {
            throw new ArgumentOutOfRangeException(nameof(parameterId));
        }

        nint envelope = config + DynamicParameterBase + parameterId * DynamicParameterStride;
        setValue(envelope, 0.0, value);
        setValue(envelope, 1.0, value);
    }

    private static void SetDynamicParameterStep(
        nint config,
        EnvelopeSetValue setValue,
        int parameterId,
        double boundarySeconds,
        double durationSeconds,
        float voicedF0,
        bool silenceFirst)
    {
        if ((uint)parameterId > 0x4c)
        {
            throw new ArgumentOutOfRangeException(nameof(parameterId));
        }

        const double epsilonPosition = 0.000001;
        double boundaryPosition = boundarySeconds / durationSeconds;
        float before = silenceFirst ? 0.0f : voicedF0;
        float after = silenceFirst ? voicedF0 : 0.0f;
        nint envelope = config + DynamicParameterBase + parameterId * DynamicParameterStride;
        setValue(envelope, 0.0, before);
        setValue(envelope, boundaryPosition - epsilonPosition, before);
        setValue(envelope, boundaryPosition, after);
        setValue(envelope, 1.0, after);
    }

    private static nint AllocateZeroed(int bytes)
    {
        nint result = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(new byte[bytes], 0, result, bytes);
        return result;
    }

    private static float ReadSingle(nint pointer, int offset)
    {
        return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer, offset));
    }

    private static void WriteSingle(nint pointer, int offset, float value)
    {
        Marshal.WriteInt32(pointer, offset, BitConverter.SingleToInt32Bits(value));
    }
}
