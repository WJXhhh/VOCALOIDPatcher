using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.VoiceComp;

if (args.Length != 1) throw new ArgumentException("Usage: CompWaveBenchmark <output-directory>");
string directory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(directory);
var combined = typeof(CompWave).GetMethod("MixWithPeaks", BindingFlags.Static | BindingFlags.NonPublic)?
    .CreateDelegate<Func<string, CompRenderPlan, CancellationToken, int, (long Clipped, float[] Peaks)>>();
var records = new List<object>();
foreach (var fixture in new[] { (Name: "short-mono", Seconds: 8, Channels: 1, Slices: 6),
    (Name: "long-stereo", Seconds: 60, Channels: 2, Slices: 24) })
{
    int frames = fixture.Seconds * 44100 + 137;
    string source = Path.Combine(directory, fixture.Name + "-source.wav");
    WriteFixture(source, frames, fixture.Channels);
    var slices = new List<SlicePlacement>();
    for (int i = 0; i < fixture.Slices; i++)
    {
        long begin = i * (frames / fixture.Slices);
        long length = Math.Min(frames - begin, frames / fixture.Slices + 1234);
        slices.Add(new(source, begin, begin + 197, length, 220, 221));
    }
    // Deliberate overlapping sources exercise clipping and mixing order.
    slices.Add(new(source, 81, 4189, 9000, 311, 313));
    var plan = new CompRenderPlan(44100, frames + 1021, slices.ToImmutableArray());
    string output = Path.Combine(directory, fixture.Name + "-out.wav");
    (long Clipped, float[] Peaks) Compose() => combined != null ? combined(output, plan, default, 1024) :
        (CompWave.Mix(output, plan), CompWave.Peaks(output));
    var warmup = Stopwatch.StartNew(); int warmupIterations = 0;
    while (warmup.ElapsedMilliseconds < 1000 || warmupIterations < 3) { Compose(); warmupIterations++; }
    warmup.Stop();
    var milliseconds = new List<double>(); var allocations = new List<long>();
    (long Clipped, float[] Peaks) result = (0, []);
    for (int i = 0; i < 30; i++)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew(); result = Compose(); timer.Stop();
        allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated); milliseconds.Add(timer.Elapsed.TotalMilliseconds);
    }
    if (!result.Peaks.SequenceEqual(CompWave.Peaks(output))) throw new InvalidDataException("Streaming peaks differ from file peaks.");
    byte[] peakBytes = new byte[result.Peaks.Length * sizeof(float)]; Buffer.BlockCopy(result.Peaks, 0, peakBytes, 0, peakBytes.Length);
    var ordered = milliseconds.Order().ToArray();
    records.Add(new { fixture.Name, fixture.Seconds, fixture.Channels, slices = slices.Count, samples = plan.Samples,
        combined = combined != null, warmupIterations, warmupMs = warmup.Elapsed.TotalMilliseconds,
        iterations = milliseconds.Count, medianMs = (ordered[14] + ordered[15]) / 2,
        p95Ms = ordered[28], maxMs = ordered[^1], medianAllocatedBytes = allocations.Order().ElementAt(15),
        result.Clipped, wavSha256 = Hash(File.ReadAllBytes(output)), peaksSha256 = Hash(peakBytes),
        milliseconds, allocatedBytes = allocations });
}
File.WriteAllText(Path.Combine(directory, "comp-wave-benchmark.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(records.Select(r => JsonSerializer.SerializeToElement(r)).Select(r => new
{ name = r.GetProperty("Name").GetString(), medianMs = r.GetProperty("medianMs").GetDouble(), p95Ms = r.GetProperty("p95Ms").GetDouble() })));
var matrix = new List<object>();
foreach (var format in new[] { (Tag: 1, Bits: 16), (Tag: 1, Bits: 24), (Tag: 1, Bits: 32), (Tag: 3, Bits: 32) })
    foreach (int channels in new[] { 1, 2 })
    {
        string name = $"format-{format.Tag}-{format.Bits}-{channels}";
        string input = Path.Combine(directory, name + "-source.wav"), output = Path.Combine(directory, name + "-out.wav");
        WriteFixture(input, 9000, channels, format.Tag, format.Bits);
        var plan = new CompRenderPlan(44100, 9007,
            [new(input, 0, 3, 8999, 31, 33), new(input, 17, 4091, 3001, 43, 47)]);
        var result = combined != null ? combined(output, plan, default, 1024) : (CompWave.Mix(output, plan), CompWave.Peaks(output));
        if (!result.Item2.SequenceEqual(CompWave.Peaks(output))) throw new InvalidDataException("Format matrix peaks differ.");
        byte[] peaks = new byte[result.Item2.Length * sizeof(float)]; Buffer.BlockCopy(result.Item2, 0, peaks, 0, peaks.Length);
        matrix.Add(new { name, clipped = result.Item1, wavSha256 = Hash(File.ReadAllBytes(output)), peaksSha256 = Hash(peaks) });
    }
File.WriteAllText(Path.Combine(directory, "comp-wave-formats.json"), JsonSerializer.Serialize(matrix, new JsonSerializerOptions { WriteIndented = true }));

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
static void WriteFixture(string path, int frames, int channels, int tag = 1, int bits = 16)
{
    using var writer = new BinaryWriter(File.Create(path)); int align = channels * bits / 8;
    writer.Write(0x46464952u); writer.Write(36 + frames * align); writer.Write(0x45564157u);
    writer.Write(0x20746d66u); writer.Write(16); writer.Write((short)tag); writer.Write((short)channels);
    writer.Write(44100); writer.Write(44100 * align); writer.Write((short)align); writer.Write((short)bits);
    writer.Write(0x61746164u); writer.Write(frames * align);
    for (int i = 0; i < frames; i++)
        for (int c = 0; c < channels; c++)
        {
            double value = 30000 / 32768d * Math.Sin((i + c * 127) * .03125);
            if (tag == 3) writer.Write((float)value);
            else if (bits == 16) writer.Write((short)Math.Round(value * 32768));
            else if (bits == 32) writer.Write((int)Math.Round(value * 2147483648));
            else
            {
                int sample = (int)Math.Round(value * 8388608);
                writer.Write((byte)sample); writer.Write((byte)(sample >> 8)); writer.Write((byte)(sample >> 16));
            }
        }
}
