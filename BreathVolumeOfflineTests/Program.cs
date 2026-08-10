using System.IO.Compression;
using System.Text;
using VOCALOIDPatcher.BreathVolume;

var testDirectory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher-BreathVolumeTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);

try
{
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
    Assert(loaded.Version == 1 && loaded.Entries.Count == 1 && loaded.Entries[0].Value == 64,
        "version 1 project data must round-trip");

    loaded.Version = 99;
    BreathProjectArchive.Write(project, loaded);
    Assert(BreathProjectArchive.Read(project).Version == 99, "unknown versions must remain detectable by the loader");

    ReplaceBreathEntry(project, "{\"version\":1,\"entries\":null}");
    AssertInvalidProjectData(project, "a null entry list must be rejected");

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
