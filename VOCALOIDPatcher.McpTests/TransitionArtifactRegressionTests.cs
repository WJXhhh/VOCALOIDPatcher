using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionArtifactRegressionTests
{
    [Theory]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void AutomaticModeRepairsEqualLoudnessPhaseDiscontinuity(int rate)
    {
        int junction = rate / 2;
        var audio = Enumerable.Range(0, rate).Select(i => (float)(.4 * Math.Sin(
            2 * Math.PI * 220 * i / rate + (i >= junction ? Math.PI / 2 : 0)))).ToArray();
        var original = (float[])audio.Clone();
        var result = TransitionSmoother.SmoothJunction(audio, junction, rate, null, 0, 0);
        Assert.True(result.Intervened);
        Assert.True(MaxJump(audio) < MaxJump(original) * .3);
    }

    [Theory]
    [InlineData(-128, 1)]
    [InlineData(0, 1)]
    [InlineData(128, 1)]
    [InlineData(128, 3)]
    public void ShortSpikeNearJunctionIsNotMaskedByItsOwnReturnEdge(int offset, int width)
    {
        const int rate = 44100, junction = rate / 2;
        var audio = Enumerable.Range(0, rate).Select(i => (float)(.1 * Math.Sin(2 * Math.PI * 220 * i / rate))).ToArray();
        for (int i = 0; i < width; i++) audio[junction + offset + i] += .7f;
        var original = (float[])audio.Clone();
        var result = TransitionSmoother.SmoothJunction(audio, junction, rate, null, 0, 0);
        Assert.True(result.Intervened);
        Assert.True(MaxJump(audio) < .04, $"Remaining jump {MaxJump(audio)}");
        Assert.Equal(original[..(junction - 1500)], audio[..(junction - 1500)]);
        Assert.Equal(original[(junction + 1500)..], audio[(junction + 1500)..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoostHeadroomDoesNotDisableAttenuationOfLouderSide(bool falling)
    {
        const int rate = 44100, junction = rate / 2;
        var audio = Enumerable.Range(0, rate).Select(i => (float)(
            (i < junction ? .35 : .7) * Math.Cos(2 * Math.PI * 220 * (i - junction) / rate))).ToArray();
        // One unrelated peak on the quiet side used to zero the entire bridge.
        audio[junction - 1000] = 1;
        if (falling) Array.Reverse(audio);
        var result = TransitionSmoother.SmoothJunction(audio, junction, rate, null, 0, falling ? -6.0206 : 6.0206);
        Assert.True(result.Intervened);
        Assert.True(Math.Abs(audio[junction] - audio[junction - 1]) < .02);
        Assert.InRange(Math.Abs(audio[junction]), .30, .38);
        Assert.All(audio, s => Assert.InRange(s, -1, 1));
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(-12)]
    [InlineData(12)]
    public void NominalGainMustNotInventAStepInAnAlreadyContinuousWave(double nominal)
    {
        const int rate = 44100, junction = rate / 2;
        var audio = Enumerable.Range(0, rate).Select(i => (float)(.4 * Math.Cos(2 * Math.PI * 220 * i / rate))).ToArray();
        var original = (float[])audio.Clone();
        TransitionSmoother.SmoothJunction(audio, junction, rate, null, 0, nominal);
        Assert.Equal(original, audio);
    }

    private static double MaxJump(float[] audio) => Enumerable.Range(1, audio.Length - 1)
        .Max(i => Math.Abs((double)audio[i] - audio[i - 1]));

    [Fact]
    public void SmoothExistingAttackDoesNotBecomeAnArtificialGainSeam()
    {
        const int rate = 44100, junction = rate / 2;
        var audio = Enumerable.Range(0, rate).Select(i =>
        {
            double t = Math.Clamp((i - junction + rate * .075) / (rate * .15), 0, 1);
            return (float)((.2 + .6 * t * t * (3 - 2 * t)) * Math.Cos(2 * Math.PI * 220 * i / rate));
        }).ToArray();
        var original = (float[])audio.Clone();
        TransitionSmoother.SmoothJunction(audio, junction, rate, null, 0, 12);
        Assert.Equal(original, audio);
    }

    [Fact]
    public void FileResultIncludesARepairOnlyPresentInTheSecondChannelAndDeduplicatesSeams()
    {
        string directory = Path.Combine(Path.GetTempPath(), "v6-stereo-click-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const int rate = 44100, frames = 10000, junction = 5000;
            string source = Path.Combine(directory, "source.wav"), single = Path.Combine(directory, "single.wav"),
                duplicate = Path.Combine(directory, "duplicate.wav");
            using (var writer = new BinaryWriter(File.Create(source)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + frames * 8); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)3); writer.Write((short)2); writer.Write(rate);
                writer.Write(rate * 8); writer.Write((short)8); writer.Write((short)32);
                writer.Write("data"u8); writer.Write(frames * 8);
                for (int i = 0; i < frames; i++)
                {
                    writer.Write(0f);
                    writer.Write((float)(.1 * Math.Sin(i * .03) + (i == junction + 128 ? .7 : 0)));
                }
            }
            var seam = new WaveJunctionProcessor.JunctionInfo(junction, 0, 0);
            var result = WaveJunctionProcessor.ProcessWaveFile(source, single, [seam]);
            Assert.True(Assert.Single(result).Intervened);
            Assert.Single(WaveJunctionProcessor.ProcessWaveFile(source, duplicate, [seam, seam]));
            Assert.Equal(File.ReadAllBytes(single), File.ReadAllBytes(duplicate));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(220)]
    [InlineData(440)]
    [InlineData(1000)]
    public void ContinuousHarmonicAttacksRemainExactAcrossCarrierPhases(int frequency)
    {
        const int rate = 44100, junction = rate / 2;
        for (int phase = 0; phase < 16; phase++)
        {
            var audio = Enumerable.Range(0, rate).Select(i =>
            {
                double t = Math.Clamp((i - junction + 256d) / 512, 0, 1);
                double angle = 2 * Math.PI * frequency * i / rate + phase * Math.PI / 8;
                return (float)((.15 + .45 * t * t * (3 - 2 * t)) *
                    (.6 * Math.Sin(angle) + .25 * Math.Sin(2 * angle) + .15 * Math.Sin(4 * angle)));
            }).ToArray();
            var original = (float[])audio.Clone();
            TransitionSmoother.SmoothJunction(audio, junction, rate, null, 0, 12);
            Assert.Equal(original, audio);
        }
    }
}
