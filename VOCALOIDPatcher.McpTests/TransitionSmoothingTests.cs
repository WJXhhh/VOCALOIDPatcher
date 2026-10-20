using VOCALOIDPatcher.ConsonantTiming.Smoothing;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TransitionSmoothingTests
{
    private const int Rate = 44100;
    private const int Junction = Rate / 2;
    private static readonly TransitionSmootherConfig Always = new() { Mode = TransitionSmoothingMode.Always };

    private static float[] Sine(int frequency, double phase = 0, bool gainStep = false, bool phaseStep = false)
        => Enumerable.Range(0, Rate).Select(i => (float)((gainStep && i < Junction ? .25 : .5) *
            Math.Sin(2 * Math.PI * frequency * i / Rate + phase + (phaseStep && i >= Junction ? Math.PI / 2 : 0)))).ToArray();

    private static double MaxJump(float[] audio)
        => Enumerable.Range(1, audio.Length - 1).Max(i => Math.Abs((double)audio[i] - audio[i - 1]));

    [Fact]
    public void NonFiniteCrossfadeDefaultsStillRespectTheMinimum()
    {
        var config = new TransitionSmootherConfig { MinCrossfadeMs = 50, MaxCrossfadeMs = double.NaN, DefaultCrossfadeMs = double.NaN }.Validate();
        Assert.Equal(50, config.MaxCrossfadeMs);
        Assert.Equal(50, config.DefaultCrossfadeMs);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(220)]
    [InlineData(440)]
    [InlineData(1000)]
    public void ContinuousWaveRemainsExactEvenInAlwaysMode(int frequency)
    {
        var original = Sine(frequency);
        var audio = (float[])original.Clone();
        var result = TransitionSmoother.SmoothJunction(audio, Junction, Rate, Always, 0, 0);
        Assert.Equal(original, audio);
        Assert.False(result.Intervened);
        Assert.Equal(0, result.CrossfadeDurationMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(2.49)]
    public void BelowThresholdBypassesBothBufferAndSplice(double nominalDifference)
    {
        var original = Sine(220, phaseStep: true);
        var audio = (float[])original.Clone();
        Assert.False(TransitionSmoother.SmoothJunction(audio, Junction, Rate, null, 0, nominalDifference).Intervened);
        Assert.Equal(original, audio);
        var result = TransitionSmoother.Splice(original.AsSpan(0, Junction), original.AsSpan(Junction), Rate, null, 0, nominalDifference);
        Assert.False(result.Intervened);
        Assert.Equal(original, result.Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.7)]
    [InlineData(1.5707963267948966)]
    public void GainBridgeDoesNotCreateWindowEdgeClicks(double phase)
    {
        var original = Sine(220, phase, gainStep: true);
        var audio = (float[])original.Clone();
        var result = TransitionSmoother.SmoothJunction(audio, Junction, Rate, null, 0, 20 * Math.Log10(2));
        Assert.True(result.Intervened);
        // Test the whole changed neighbourhood, including both outer edges.
        double localJump = Enumerable.Range(Junction - 3000, 6000)
            .Max(i => Math.Abs((double)audio[i] - audio[i - 1]));
        Assert.True(localJump < .02, $"Local jump {localJump}");
        Assert.Equal(original[..(Junction - 3000)], audio[..(Junction - 3000)]);
        Assert.Equal(original[(Junction + 3000)..], audio[(Junction + 3000)..]);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(220)]
    [InlineData(440)]
    [InlineData(1000)]
    public void RepairsPhaseStepWithoutNewEdgesOrPeakOvershoot(int frequency)
    {
        var original = Sine(frequency, phaseStep: true);
        var audio = (float[])original.Clone();
        var result = TransitionSmoother.SmoothJunction(audio, Junction, Rate, Always, 0, 0);
        Assert.True(result.Intervened);
        Assert.True(result.CrossfadeDurationMs > 0);
        Assert.True(MaxJump(audio) < MaxJump(original) * .3);
        Assert.True(audio.Max(Math.Abs) <= original.Max(Math.Abs));
        Assert.Equal(original[..(Junction - 1200)], audio[..(Junction - 1200)]);
        Assert.Equal(original[(Junction + 1200)..], audio[(Junction + 1200)..]);
    }

    [Fact]
    public void LocalGainBridgeRespectsHeadroomWithoutChangingBodyGain()
    {
        var audio = Sine(220, .7).Select(v => v * 1.9f).ToArray();
        var original = (float[])audio.Clone();
        TransitionSmoother.SmoothJunction(audio, Junction, Rate, null, 0, 24);
        Assert.All(audio, v => Assert.InRange(v, -1, 1));
        Assert.Equal(original[..1000], audio[..1000]);
        Assert.Equal(original[^1000..], audio[^1000..]);
    }

    [Theory]
    [InlineData(-200)]
    [InlineData(200)]
    public void ShortBuffersAndExtremeOffsetsStayBounded(int offset)
    {
        for (int length = 0; length <= 16; length++)
        for (int junction = 0; junction <= length; junction++)
        {
            var audio = Enumerable.Range(0, length).Select(i => i % 2 == 0 ? .5f : -.5f).ToArray();
            TransitionSmoother.SmoothJunction(audio, junction, Rate, Always with { TimingOffsetMs = offset }, 0, 6);
            Assert.All(audio, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1));
        }
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void WaveBypassIsByteExactAndProcessingPreservesOutsideSamples(int bits, bool ieee)
    {
        string directory = Path.Combine(Path.GetTempPath(), "v6-smoothing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.wav"), bypass = Path.Combine(directory, "bypass.wav"),
                processed = Path.Combine(directory, "processed.wav");
            const int frames = 10000, channels = 2, junction = 5000;
            int size = bits / 8;
            using (var writer = new BinaryWriter(File.Create(source)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + frames * channels * size); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)(ieee ? 3 : 1)); writer.Write((short)channels);
                writer.Write(Rate); writer.Write(Rate * channels * size); writer.Write((short)(channels * size)); writer.Write((short)bits);
                writer.Write("data"u8); writer.Write(frames * channels * size);
                for (int i = 0; i < frames; i++)
                for (int ch = 0; ch < channels; ch++)
                {
                    // Include unrepresentable low bits in PCM32 outside the window.
                    int value = (int)((i < junction ? .1 : .4) * Math.Sin(i * .04 + ch) * int.MaxValue) | 7;
                    if (ieee) writer.Write(value / 2147483648f);
                    else if (bits == 32) writer.Write(value);
                    else if (bits == 16) writer.Write((short)(value >> 16));
                    else { writer.Write((byte)(value >> 8)); writer.Write((byte)(value >> 16)); writer.Write((byte)(value >> 24)); }
                }
            }
            WaveJunctionProcessor.ProcessWaveFile(source, bypass, junction, null, 0, 0);
            var original = File.ReadAllBytes(source);
            Assert.Equal(original, File.ReadAllBytes(bypass));
            var result = WaveJunctionProcessor.ProcessWaveFile(source, processed, junction, null, 0, 12);
            Assert.True(result.Intervened);
            var output = File.ReadAllBytes(processed);
            Assert.Equal(original.Length, output.Length);
            Assert.Equal(original[..(44 + 2000 * channels * size)], output[..(44 + 2000 * channels * size)]);
            Assert.Equal(original[(44 + 8000 * channels * size)..], output[(44 + 8000 * channels * size)..]);
        }
        finally { Directory.Delete(directory, true); }
    }
}
