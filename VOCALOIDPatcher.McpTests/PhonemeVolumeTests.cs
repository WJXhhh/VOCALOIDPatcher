using System.IO.Compression;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.PhonemeVolume;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class PhonemeVolumeTests
{
    [Fact]
    public void ValuesAreImmutableFiniteAndBoundToTheExactPhonemeTopology()
    {
        var plan = new PhonemeVolumePlan("ts\\_h i iE_r @N");
        var edited = plan.With(3, -6.04);
        Assert.Equal(new float[] { 0, 0, 0, -6 }, edited.Values);
        Assert.True(plan.IsDefault);
        Assert.Equal(-60, edited.With(3, -999).Values[3]);
        Assert.Equal(12, edited.With(3, 999).Values[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => edited.With(3, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => edited.With(4, 0));
        Assert.Throws<InvalidDataException>(() => new PhonemeVolumePlan("a i", [0]));
        Assert.Throws<InvalidDataException>(() => new PhonemeVolumePlan("a", [float.PositiveInfinity]));
    }

    [Fact]
    public void ArchiveRoundTripPreservesTimingAndOtherEntriesAndRemovesResetValues()
    {
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "test.vpr");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("Project/keep.txt").Open()); writer.Write("keep");
            }
            var address = new TimingNoteAddress(0, 1, 2, 960, 960, 60, "test-bank", "l a i");
            var timing = new SampledTimingPlan(address.Topology, [new(TimingAdjustment.PhonemeBoundary, 2, .02f)]);
            SampledTimingProjectArchive.Write(path, new Dictionary<TimingNoteAddress, SampledTimingPlan> { [address] = timing });
            PhonemeVolumeArchive.Write(path, [new(address, [0, -6, 12])]);
            Assert.Equal(new float[] { 0, -6, 12 }, Assert.Single(PhonemeVolumeArchive.Read(path)).Decibels);
            Assert.Equal(timing.Fingerprint, SampledTimingProjectArchive.Read(path)[address].Fingerprint);
            Assert.Throws<InvalidDataException>(() => PhonemeVolumeArchive.Write(path, [new(address, [0, 0, 0]), new(address, [0, 0, 0])]));
            Assert.Throws<InvalidDataException>(() => PhonemeVolumeArchive.Write(path, [new(address, [0, -61, 0])]));
            PhonemeVolumeArchive.Write(path, []);
            Assert.Empty(PhonemeVolumeArchive.Read(path));
            using var archive = ZipFile.OpenRead(path);
            using var reader = new StreamReader(archive.GetEntry("Project/keep.txt")!.Open());
            Assert.Equal("keep", reader.ReadToEnd());
            Assert.NotNull(archive.GetEntry(SampledTimingProjectArchive.EntryPath));
        });
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(1, 16)]
    [InlineData(1, 24)]
    [InlineData(1, 32)]
    [InlineData(3, 32)]
    public void GainAndMuteAffectOnlyTheTargetWindowAcrossStereoPcmFormats(int tag, int bits)
    {
        WithDirectory(directory =>
        {
            string input = Path.Combine(directory, "input.wav"), output = Path.Combine(directory, "output.wav");
            WriteWave(input, tag, bits, 2);
            byte[] original = File.ReadAllBytes(input);
            foreach (float db in new float[] { -6, 6, -60 })
            {
                PhonemeGainWaveProcessor.Create(input, output, [new(.025, .075, db)]);
                byte[] changed = File.ReadAllBytes(output);
                int align = 2 * bits / 8;
                Assert.Equal(original.AsSpan(0, 44 + 1200 * align).ToArray(), changed.AsSpan(0, 44 + 1200 * align).ToArray());
                Assert.Equal(original.AsSpan(44 + 3600 * align).ToArray(), changed.AsSpan(44 + 3600 * align).ToArray());
                double expected = PhonemeVolumePlan.LinearGain(db);
                for (int channel = 0; channel < 2; channel++)
                {
                    int offset = 44 + 2400 * align + channel * bits / 8;
                    double ratio = ReadSample(changed, offset, tag, bits) / ReadSample(original, offset, tag, bits);
                    Assert.InRange(Math.Abs(ratio - expected), 0, bits == 8 ? .025 : .0003);
                }
            }
            Assert.Equal(original, File.ReadAllBytes(input));
        });
    }

    [Fact]
    public void ShortPhonemeReachesMuteAndOverlappingGainsComposeBeforeIntegerClipping()
    {
        WithDirectory(directory =>
        {
            string input = Path.Combine(directory, "input.wav"), a = Path.Combine(directory, "a.wav"), b = Path.Combine(directory, "b.wav");
            WriteWave(input, 1, 16, 1);
            PhonemeGainWaveProcessor.Create(input, a, [new(2400d / 48000, 2402d / 48000, -60)]);
            Assert.Equal(0, ReadSample(File.ReadAllBytes(a), 44 + 2400 * 2, 1, 16));
            var ranges = new PhonemeGainRegion[] { new(.025, .075, 12), new(.025, .075, -12) };
            PhonemeGainWaveProcessor.Create(input, a, ranges);
            PhonemeGainWaveProcessor.Create(input, b, ranges.Reverse().ToArray());
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
            Assert.Equal(ReadSample(File.ReadAllBytes(input), 44 + 2400 * 2, 1, 16), ReadSample(File.ReadAllBytes(a), 44 + 2400 * 2, 1, 16));
        });
    }

    [Fact]
    public void BvlAndPhonemeGainComposeOnceAndResetRestoresTheBvlInputExactly()
    {
        WithDirectory(directory =>
        {
            string input = Path.Combine(directory, "input.wav"), bvl = Path.Combine(directory, "bvl.wav"),
                output = Path.Combine(directory, "output.wav"), repeated = Path.Combine(directory, "repeated.wav");
            WriteWave(input, 3, 32, 1);
            BreathWaveProcessor.CreateAdjustedWave(input, bvl, [new(1200, 3600, 64)]);
            PhonemeGainWaveProcessor.Create(bvl, output, [new(.025, .075, -6)]);
            double original = ReadSample(File.ReadAllBytes(input), 44 + 2400 * 4, 3, 32);
            double adjusted = ReadSample(File.ReadAllBytes(output), 44 + 2400 * 4, 3, 32);
            Assert.InRange(Math.Abs(adjusted / original - 64d / 127 * PhonemeVolumePlan.LinearGain(-6)), 0, .00001);
            PhonemeGainWaveProcessor.Create(bvl, repeated, [new(.025, .075, -6)]);
            Assert.Equal(File.ReadAllBytes(output), File.ReadAllBytes(repeated));
            PhonemeGainWaveProcessor.Create(bvl, output, [new(.025, .075, 0)]);
            Assert.Equal(File.ReadAllBytes(bvl), File.ReadAllBytes(output));
        });
    }

    [Fact]
    public void MalformedInputAndInvalidRegionsCannotReplaceAnExistingDestination()
    {
        WithDirectory(directory =>
        {
            string input = Path.Combine(directory, "input.wav"), output = Path.Combine(directory, "output.wav");
            File.WriteAllText(input, "truncated"); File.WriteAllText(output, "keep");
            Assert.Throws<InvalidDataException>(() => PhonemeGainWaveProcessor.Create(input, output, [new(0, 1, -6)]));
            Assert.Equal("keep", File.ReadAllText(output));
            Assert.Throws<InvalidDataException>(() => PhonemeGainWaveProcessor.Create(input, output, [new(double.NaN, 1, -6)]));
            Assert.Throws<ArgumentException>(() => PhonemeGainWaveProcessor.Create(input, input, []));
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "phoneme-gain-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, true); }
    }

    private static void WriteWave(string path, int tag, int bits, int channels)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int align = channels * bits / 8, length = 4800 * align;
        writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)tag); writer.Write((ushort)channels); writer.Write(48000); writer.Write(48000 * align);
        writer.Write((ushort)align); writer.Write((ushort)bits); writer.Write("data"u8); writer.Write(length);
        for (int i = 0; i < 4800; i++)
            for (int channel = 0; channel < channels; channel++)
            {
                double sample = channel == 0 ? .25 : -.25;
                if (tag == 3) writer.Write((float)sample);
                else if (bits == 8) writer.Write((byte)(128 + sample * 120));
                else if (bits == 16) writer.Write((short)(sample * 32000));
                else if (bits == 24) { int value = (int)(sample * 8000000); writer.Write((byte)value); writer.Write((byte)(value >> 8)); writer.Write((byte)(value >> 16)); }
                else writer.Write((int)(sample * 2000000000));
            }
    }

    private static double ReadSample(byte[] bytes, int offset, int tag, int bits) => tag == 3 ? BitConverter.ToSingle(bytes, offset) : bits switch
    {
        8 => bytes[offset] - 128,
        16 => BitConverter.ToInt16(bytes, offset),
        24 => (bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16) << 8 >> 8,
        _ => BitConverter.ToInt32(bytes, offset)
    };
}
