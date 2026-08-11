using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using VOCALOIDPatcher.BreathVolume;

var testDirectory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher-BreathVolumeTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);

try
{
    TestNativeBreathPhonemes();
    TestNativePhonemeInspector();
    TestNativeBreathRange();
    TestTraditionalBreathDetection();
    TestWave(testDirectory, 44100);
    TestWave(testDirectory, 48000);
    TestProjectArchive(testDirectory);
    Console.WriteLine("Breath-volume offline verification passed.");
}
finally
{
    if (Directory.Exists(testDirectory))
        Directory.Delete(testDirectory, true);
}

static void TestNativeBreathPhonemes()
{
    foreach (var phoneme in new[] { "br", "SilBreath", "silbreath", "SilBreath+" })
        Assert(BreathPhonemeClassifier.IsNativeBreathPhoneme(phoneme),
            $"native breath phoneme {phoneme} must be recognized");

    foreach (var phoneme in new string?[] { null, "", "Sil", "a", "e", "i", "o", "u", "v" })
        Assert(!BreathPhonemeClassifier.IsNativeBreathPhoneme(phoneme),
            $"ordinary phoneme {phoneme ?? "<null>"} must not be treated as a breath");
}

static void TestNativePhonemeInspector()
{
    var direct = Marshal.StringToHGlobalAnsi("SilBreath");
    var inlineObject = Marshal.AllocHGlobal(96);
    var heapObject = Marshal.AllocHGlobal(96);
    var heapText = Marshal.StringToHGlobalAnsi("SilBreath+");
    try
    {
        Assert(NativePhonemeInspector.ReadName(direct) == "SilBreath",
            "a renderer phoneme exposed as a direct char pointer must be readable");

        ZeroMemory(inlineObject, 96);
        Marshal.WriteByte(inlineObject, 0, 1);
        var inline = Encoding.ASCII.GetBytes("SilBreath\0");
        Marshal.Copy(inline, 0, inlineObject + 8, inline.Length);
        Marshal.WriteInt64(inlineObject, 24, 9);
        Marshal.WriteInt64(inlineObject, 32, 15);
        Assert(NativePhonemeInspector.ReadName(inlineObject) == "SilBreath",
            "an inline MSVC string inside a renderer phoneme object must be readable");

        ZeroMemory(heapObject, 96);
        Marshal.WriteByte(heapObject, 0, 1);
        Marshal.WriteIntPtr(heapObject, 8, heapText);
        Marshal.WriteInt64(heapObject, 24, 10);
        Marshal.WriteInt64(heapObject, 32, 31);
        Assert(NativePhonemeInspector.ReadName(heapObject) == "SilBreath+",
            "a heap-backed MSVC string inside a renderer phoneme object must be readable");
    }
    finally
    {
        Marshal.FreeHGlobal(heapText);
        Marshal.FreeHGlobal(heapObject);
        Marshal.FreeHGlobal(inlineObject);
        Marshal.FreeHGlobal(direct);
    }
}

static void ZeroMemory(IntPtr pointer, int length)
{
    for (var index = 0; index < length; index++)
        Marshal.WriteByte(pointer, index, 0);
}

static void TestNativeBreathRange()
{
    const long samplesPerFrame = 256;
    Assert(NativeBreathRangeResolver.TryResolve(
            184, 281, 1465, samplesPerFrame,
            out var beginSample, out var endSample),
        "the native hu breath marker must resolve from the renderer's exact frame bounds");
    Assert(beginSample == 184 * samplesPerFrame && endSample == 281 * samplesPerFrame,
        "the native hu breath range must preserve the renderer's frame boundaries");
    Assert(!NativeBreathRangeResolver.TryResolve(
            184, 1466, 1465, samplesPerFrame, out _, out _),
        "a native range outside the score must be rejected");
}

static void TestTraditionalBreathDetection()
{
    const int frameCount = 500;
    const int onset = 300;
    const long samplesPerFrame = 256;
    const int sampleRate = 44100;
    Assert(TraditionalBreathDetector.NormalizeThumbnailPeak(short.MinValue, short.MaxValue) == 1f,
        "native thumbnail extrema must normalize without overflowing short.MinValue");
    var noteFrames = TraditionalBreathDetector.BuildPitchedFrames(
        frameCount,
        samplesPerFrame,
        new[]
        {
            new TraditionalBreathRange(50 * samplesPerFrame, 100 * samplesPerFrame),
            new TraditionalBreathRange(onset * samplesPerFrame, 400 * samplesPerFrame),
        });
    Assert(noteFrames.Count(frame => frame) == 150 && !noteFrames[200] && noteFrames[onset],
        "pitched frames must follow real note ranges and preserve rests between equal-pitch notes");
    var rms = new float[frameCount];
    var peaks = new float[frameCount];
    var pitched = new bool[frameCount];
    Array.Fill(pitched, true, onset, 100);
    for (var frame = 120; frame < 200; frame++)
    {
        rms[frame] = 0.001f;
        peaks[frame] = 0.01f;
    }

    var detected = TraditionalBreathDetector.Detect(
        rms, peaks, pitched, samplesPerFrame, sampleRate);
    Assert(detected.Ranges.Count == 1,
        "a sustained unpitched PCM region immediately before a pitched onset must be detected");
    Assert(detected.Ranges[0] == new TraditionalBreathRange(
            120 * samplesPerFrame, 200 * samplesPerFrame),
        "traditional breath PCM boundaries must preserve the renderer frame boundaries");

    for (var frame = 280; frame < onset; frame++)
    {
        rms[frame] = 0.001f;
        peaks[frame] = 0.01f;
    }
    detected = TraditionalBreathDetector.Detect(
        rms, peaks, pitched, samplesPerFrame, sampleRate);
    Assert(detected.Ranges.Count == 1 && detected.Ranges[0] == new TraditionalBreathRange(
            120 * samplesPerFrame, 200 * samplesPerFrame),
        "a short onset consonant must not hide an earlier automatic breath cluster");

    Array.Clear(rms);
    Array.Clear(peaks);
    for (var frame = 280; frame < onset; frame++)
    {
        rms[frame] = 0.001f;
        peaks[frame] = 0.01f;
    }
    detected = TraditionalBreathDetector.Detect(
        rms, peaks, pitched, samplesPerFrame, sampleRate);
    Assert(detected.Ranges.Count == 0,
        "a short unpitched consonant at note onset must not be classified as an automatic breath");

    Array.Clear(rms);
    Array.Clear(peaks);
    Array.Clear(pitched);
    Array.Fill(pitched, true, 50, 50);
    Array.Fill(pitched, true, onset, 100);
    for (var frame = 100; frame < 130; frame++)
    {
        rms[frame] = 0.001f;
        peaks[frame] = 0.01f;
    }
    detected = TraditionalBreathDetector.Detect(
        rms, peaks, pitched, samplesPerFrame, sampleRate);
    Assert(detected.Ranges.Count == 0 && detected.RejectedPreviousTail == 1,
        "an unpitched release immediately following the previous note must not become a breath");
}

static void TestWave(string directory, int sampleRate)
{
    const int sampleCount = 2400;
    const short amplitude = 12000;
    var source = Path.Combine(directory, $"source-{sampleRate}.wav");
    var unchanged = Path.Combine(directory, $"unchanged-{sampleRate}.wav");
    var adjusted = Path.Combine(directory, $"adjusted-{sampleRate}.wav");
    WritePcm16Wave(source, sampleRate, Enumerable.Repeat(amplitude, sampleCount).ToArray());

    BreathWaveProcessor.CreateAdjustedWave(source, unchanged,
        new[] { new BreathGainRegion(100, 900, 127) });
    Assert(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(unchanged)),
        $"127 must preserve every RIFF byte at {sampleRate} Hz");

    BreathWaveProcessor.CreateAdjustedWave(source, adjusted, new[]
    {
        new BreathGainRegion(100, 900, 64),
        new BreathGainRegion(1200, 2100, 0)
    });
    var bytes = File.ReadAllBytes(adjusted);
    var dataOffset = FindChunkData(bytes, "data");
    var junkOffset = FindChunkData(bytes, "JUNK");
    Assert(Encoding.ASCII.GetString(bytes, junkOffset, 5) == "BVL!!", "non-audio RIFF chunks must be preserved");
    Assert(ReadSample(bytes, dataOffset, 50) == amplitude, "samples outside breath regions must be unchanged");

    var expected64 = (short)Math.Round(amplitude * 64.0 / 127.0);
    Assert(Math.Abs(ReadSample(bytes, dataOffset, 500) - expected64) <= 1, "64 must apply value / 127 gain");
    Assert(ReadSample(bytes, dataOffset, 1650) == 0, "0 must silence the middle of a breath");
    Assert(ReadSample(bytes, dataOffset, 100) is > 0 and < amplitude, "breath start must use a smooth fade");
    Assert(ReadSample(bytes, dataOffset, 899) is > 0 and < amplitude, "breath end must use a smooth fade");
    Assert(ReadSample(bytes, dataOffset, 1000) == amplitude, "separate breath regions must not affect the gap");
}

static void TestProjectArchive(string directory)
{
    var project = Path.Combine(directory, "test.vpr");
    using (var archive = ZipFile.Open(project, ZipArchiveMode.Create))
    {
        WriteEntry(archive, "Project/sequence.json", "{\"sequence\":true}");
        WriteEntry(archive, "Audio/original.bin", "keep-me");
    }

    var data = new BreathProjectData
    {
        Entries =
        {
            new BreathProjectEntry
            {
                Track = 1,
                Part = 2,
                Note = 3,
                RelPosTick = 480,
                NoteNumber = 64,
                Occurrence = 0,
                Value = 64
            }
        },
        NativeMarkers =
        {
            new BreathProjectNativeMarker
            {
                Track = 1,
                Part = 2,
                BeginFrame = 184,
                EndFrame = 281
            }
        }
    };
    BreathProjectArchive.Write(project, data);

    using (var archive = ZipFile.OpenRead(project))
    {
        Assert(ReadEntry(archive, "Project/sequence.json") == "{\"sequence\":true}", "sequence.json must be preserved");
        Assert(ReadEntry(archive, "Audio/original.bin") == "keep-me", "unrelated ZIP entries must be preserved");
        var json = ReadEntry(archive, BreathProjectArchive.EntryPath);
        Assert(json.Contains("\"value\": 64", StringComparison.Ordinal), "non-default BVL must be stored");
        Assert(!json.Contains("\"value\": 127", StringComparison.Ordinal), "default BVL must be omitted by the caller");
    }

    var loaded = BreathProjectArchive.Read(project);
    Assert(loaded.Version == 1 && loaded.Entries.Count == 1 && loaded.Entries[0].Value == 64 &&
           loaded.NativeMarkers.Count == 1 && loaded.NativeMarkers[0].BeginFrame == 184 &&
           loaded.NativeMarkers[0].EndFrame == 281,
        "version 1 project data must round-trip");

    loaded.Version = 99;
    BreathProjectArchive.Write(project, loaded);
    Assert(BreathProjectArchive.Read(project).Version == 99, "unknown versions must remain detectable by the loader");

    ReplaceBreathEntry(project, "{\"version\":1,\"entries\":null}");
    AssertInvalidProjectData(project, "a null entry list must be rejected");

    ReplaceBreathEntry(project, "{\"version\":1,\"entries\":[],\"nativeMarkers\":null}");
    AssertInvalidProjectData(project, "a null native marker list must be rejected");

    ReplaceBreathEntry(project,
        "{\"version\":1,\"entries\":[],\"nativeMarkers\":[{\"beginSeconds\":1.25}]}");
    Assert(BreathProjectArchive.Read(project).NativeMarkers[0].LegacyBeginSeconds == 1.25,
        "ABI 2 marker data must remain readable without inventing an end boundary");

    ReplaceBreathEntry(project,
        "{\"version\":1,\"entries\":[],\"nativeMarkers\":[{\"beginSeconds\":-1}]}");
    AssertInvalidProjectData(project, "an invalid native marker position must be rejected");

    ReplaceBreathEntry(project, "{\"version\":1,\"entries\":[null]}");
    AssertInvalidProjectData(project, "null list items must be rejected");

    ReplaceBreathEntry(project,
        "{\"version\":1,\"entries\":[" + string.Join(',', Enumerable.Repeat("{}", BreathProjectArchive.MaxEntries + 1)) + "]}");
    AssertInvalidProjectData(project, "oversized entry lists must be rejected");

    ReplaceBreathEntry(project, new string(' ', BreathProjectArchive.MaxEntryBytes + 1));
    AssertInvalidProjectData(project, "oversized JSON entries must be rejected before deserialization");

    var beforeFailedWrite = File.ReadAllBytes(project);
    var writeFailed = false;
    using (var locked = new FileStream(project, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try
        {
            BreathProjectArchive.Write(project, new BreathProjectData());
        }
        catch (IOException)
        {
            writeFailed = true;
        }
    }
    Assert(writeFailed, "a locked project must report an extension write failure");
    Assert(beforeFailedWrite.SequenceEqual(File.ReadAllBytes(project)),
        "a failed extension write must preserve the original project byte-for-byte");
}

static void ReplaceBreathEntry(string project, string json)
{
    using var archive = ZipFile.Open(project, ZipArchiveMode.Update);
    archive.GetEntry(BreathProjectArchive.EntryPath)?.Delete();
    WriteEntry(archive, BreathProjectArchive.EntryPath, json);
}

static void AssertInvalidProjectData(string project, string message)
{
    try
    {
        BreathProjectArchive.Read(project);
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void WritePcm16Wave(string path, int sampleRate, IReadOnlyList<short> samples)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
    var dataSize = samples.Count * sizeof(short);
    var junkSize = 5;
    var riffSize = 4 + (8 + 16) + (8 + junkSize + 1) + (8 + dataSize);
    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(riffSize);
    writer.Write(Encoding.ASCII.GetBytes("WAVE"));
    writer.Write(Encoding.ASCII.GetBytes("fmt "));
    writer.Write(16);
    writer.Write((ushort)1);
    writer.Write((ushort)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((ushort)2);
    writer.Write((ushort)16);
    writer.Write(Encoding.ASCII.GetBytes("JUNK"));
    writer.Write(junkSize);
    writer.Write(Encoding.ASCII.GetBytes("BVL!!"));
    writer.Write((byte)0);
    writer.Write(Encoding.ASCII.GetBytes("data"));
    writer.Write(dataSize);
    foreach (var sample in samples)
        writer.Write(sample);
}

static int FindChunkData(byte[] bytes, string id)
{
    for (var offset = 12; offset + 8 <= bytes.Length;)
    {
        var size = BitConverter.ToInt32(bytes, offset + 4);
        if (Encoding.ASCII.GetString(bytes, offset, 4) == id)
            return offset + 8;
        offset += 8 + size + (size & 1);
    }
    throw new InvalidDataException($"Missing {id} chunk");
}

static short ReadSample(byte[] bytes, int dataOffset, int index)
    => BitConverter.ToInt16(bytes, dataOffset + index * 2);

static void WriteEntry(ZipArchive archive, string path, string value)
{
    var entry = archive.CreateEntry(path);
    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
    writer.Write(value);
}

static string ReadEntry(ZipArchive archive, string path)
{
    using var reader = new StreamReader(archive.GetEntry(path)!.Open(), Encoding.UTF8);
    return reader.ReadToEnd();
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
