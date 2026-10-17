using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.PhonemeVolume;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbePhonemeVolume(WIVSMSequence source, WIVSMMidiPart part, WIVSMNote note, string directory)
    {
        string input = Path.Combine(directory, "baseline.wav");
        var metrics = ReadWaveMetrics(input);
        byte[] original = File.ReadAllBytes(input);
        int dataOffset = 12;
        while (dataOffset + 8 <= original.Length && System.Text.Encoding.ASCII.GetString(original, dataOffset, 4) != "data")
        { int size = checked((int)BitConverter.ToUInt32(original, dataOffset + 4)); dataOffset += 8 + size + (size & 1); }
        dataOffset += 8;
        int align = metrics.Channels * metrics.BitsPerSample / 8;
        var positions = note.GetPhonemePositions();
        string[] phones = note.Phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Require(positions.Count == phones.Length + 1, "Missing native volume boundaries.");
        using var counters = new TimingDseCounters();
        var before = counters.Read();
        var results = new List<object>();
        for (int i = 0; i < phones.Length; i++)
        {
            double Time(int p) => source.PresendTimeSec + source.GetTimeFromTick(part.AbsBeginTick, note.GetAbsPositionFromNoteBaseTick(p));
            double begin = Time(positions[i]), end = Time(positions[i + 1]);
            int first = (int)Math.Round(begin * metrics.SampleRate), last = (int)Math.Round(end * metrics.SampleRate);
            foreach (float db in new float[] { -12, 6, -60 })
            {
                string output = Path.Combine(directory, $"gain-{i}-{db}.wav");
                var timer = Stopwatch.StartNew();
                PhonemeGainWaveProcessor.Create(input, output, [new(begin, end, db)]);
                double milliseconds = timer.Elapsed.TotalMilliseconds;
                byte[] adjusted = File.ReadAllBytes(output);
                Require(original.AsSpan(0, dataOffset + first * align).SequenceEqual(adjusted.AsSpan(0, dataOffset + first * align)), "Gain changed samples before target.");
                Require(original.AsSpan(dataOffset + last * align).SequenceEqual(adjusted.AsSpan(dataOffset + last * align)), "Gain changed samples after target.");
                double sumBefore = 0, sumAfter = 0;
                int fade = (int)Math.Ceiling(metrics.SampleRate * .006);
                for (int frame = first + fade; frame < last - fade; frame++)
                    for (int channel = 0; channel < metrics.Channels; channel++)
                    {
                        int at = dataOffset + frame * align + channel * metrics.BitsPerSample / 8;
                        double a = ReadSample(original, at, metrics.FormatTag, metrics.BitsPerSample);
                        double b = ReadSample(adjusted, at, metrics.FormatTag, metrics.BitsPerSample);
                        sumBefore += a * a; sumAfter += b * b;
                    }
                Require(sumBefore > 1e-12, "Target phoneme is silent or coordinates are wrong.");
                double ratio = Math.Sqrt(sumAfter / sumBefore), expected = PhonemeVolumePlan.LinearGain(db);
                Require(Math.Abs(ratio - expected) < .002, "Real phoneme gain differs from requested level.");
                var result = new { index = i, phoneme = phones[i], db, begin, end, ratio, expected, milliseconds, outsideUnchanged = true };
                results.Add(result); Console.WriteLine("volume.case=" + JsonSerializer.Serialize(result));
            }
        }
        var dse = counters.Read().Since(before);
        Require(dse.Starts == 0 && dse.Steps == 0 && dse.Samples == 0, "Volume editing synthesized audio.");
        Require(original.AsSpan().SequenceEqual(File.ReadAllBytes(input)), "Volume overwrote source audio.");
        File.WriteAllText(Path.Combine(directory, "volume.json"), JsonSerializer.Serialize(new { cases = results, dse, sourceUnchanged = true }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
