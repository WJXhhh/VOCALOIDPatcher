using VOCALOIDPatcher.ParameterEnergy;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public class ParameterEnergyTests
{
    [Theory]
    [InlineData(-30, -30, 0, 200)]
    [InlineData(-18, -30, 0, 200)]
    [InlineData(-12.3, -60, 6, 85)]
    [InlineData(0, -30, 0, 400)]
    [InlineData(12, -40, 24, 200)]
    public void TargetGuideUsesTheSameDbScaleAsEnergyAndRoundTripsToTenths(double db, double minimum, double maximum, double height)
    {
        double y = EnergyLevelScale.ToY(db, height, minimum, maximum);
        Assert.Equal(db, EnergyLevelScale.FromY(y, height, minimum, maximum), 6);
    }

    [Fact]
    public void TargetGuideDragClampsAtVisibleRangeAndRejectsInvalidCoordinates()
    {
        Assert.Equal(0, EnergyLevelScale.FromY(-100, 200, -30, 0));
        Assert.Equal(-30, EnergyLevelScale.FromY(500, 200, -30, 0));
        Assert.Equal(-30, EnergyLevelScale.FromY(double.NaN, 200, -30, 0));
        Assert.Equal(-30, EnergyLevelScale.FromY(4, 8, -30, 0));
        Assert.Equal(4, EnergyLevelScale.ToY(20, 200, -30, 0));
        Assert.Equal(196, EnergyLevelScale.ToY(-100, 200, -30, 0));
    }

    private sealed class ShortReadWave(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(7, buffer.Length)]);
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(7, count));
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(24, 1)]
    [InlineData(32, 1)]
    [InlineData(32, 3)]
    public void BlockDecodePreservesStereoAndPartialBucketsAcrossShortReads(int bits, int tag)
    {
        using var input = Wave(2, bits, tag, 32_773, (i, c) => (c == 0 ? .4 : -.3) * Math.Sin(i * .13));
        var expected = EnergyEnvelope.Read(input);
        using var shortReads = new ShortReadWave(input.ToArray());
        var actual = EnergyEnvelope.Read(shortReads);
        Assert.Equal(expected.StepSeconds, actual.StepSeconds);
        Assert.Equal(expected.DurationSeconds, actual.DurationSeconds);
        Assert.Equal(expected.Decibels, actual.Decibels);
        Assert.Equal(expected.PeakDecibels, actual.PeakDecibels);
        Assert.True(shortReads.CanRead); // The caller still owns the stream.
    }

    private static MemoryStream Wave(int channels, int bits, int tag, int frames, Func<int, int, double> sample, int rate = 8000)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int size = frames * channels * (bits / 8);
        writer.Write(0x46464952); writer.Write(36 + size); writer.Write(0x45564157);
        writer.Write(0x20746d66); writer.Write(16); writer.Write((short)tag); writer.Write((short)channels);
        writer.Write(rate); writer.Write(rate * channels * (bits / 8));
        writer.Write((short)(channels * (bits / 8))); writer.Write((short)bits);
        writer.Write(0x61746164); writer.Write(size);
        for (int i = 0; i < frames; i++)
        for (int c = 0; c < channels; c++)
        {
            double value = sample(i, c);
            if (tag == 3) writer.Write((float)value);
            else if (bits == 16) writer.Write((short)(value * 32768));
            else if (bits == 24)
            {
                int pcm = (int)(value * 8388608);
                writer.Write((byte)pcm); writer.Write((byte)(pcm >> 8)); writer.Write((byte)(pcm >> 16));
            }
            else writer.Write((int)(value * 2147483648));
        }
        stream.Position = 0;
        return stream;
    }

    private static EnergyEnvelope Tone(int rate, double frequency, int weighting, int channels = 1)
    {
        using var wave = Wave(channels, 32, 3, rate,
            (i, c) => (c == 0 ? .25 : -.25) * Math.Sin(2 * Math.PI * frequency * i / rate), rate);
        return EnergyEnvelope.Read(wave, weightingPercent: weighting);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(384000)]
    public void WeightingKeepsOneKhzReferenceAndGentlyTiltsFrequencyBalance(int rate)
    {
        foreach (int strength in new[] { 50, 100 })
        {
            var reference = Tone(rate, 1000, strength);
            Assert.InRange(reference.GetDecibels(.5, .501), -15.10, -15.00);
            Assert.Equal(strength, reference.WeightingPercent);
        }
        double rawLow = Tone(rate, 50, 0).GetDecibels(.5, .52);
        double halfLow = Tone(rate, 50, 50).GetDecibels(.5, .52);
        double fullLow = Tone(rate, 50, 100).GetDecibels(.5, .52);
        double frequency = Math.Min(6000, rate * .4);
        double rawHigh = Tone(rate, frequency, 0).GetDecibels(.5, .52);
        double halfHigh = Tone(rate, frequency, 50).GetDecibels(.5, .52);
        double fullHigh = Tone(rate, frequency, 100).GetDecibels(.5, .52);
        Assert.InRange(halfLow - rawLow, -3.5, -2.5);
        Assert.InRange(fullLow - rawLow, -6.5, -5.5);
        Assert.InRange(halfHigh - rawHigh, 1.5, 2.1);
        Assert.InRange(fullHigh - rawHigh, 3.0, 4.1);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(48000)]
    [InlineData(384000)]
    public void WeightingAboveOneHundredContinuesChangingResponseWithoutMovingOneKhz(int rate)
    {
        double frequency = Math.Min(6000, rate * .4);
        double previousHigh = Tone(rate, frequency, 100).GetDecibels(.5, .52);
        double previousLow = Tone(rate, 50, 100).GetDecibels(.5, .52);
        double previousPeak = Tone(rate, frequency, 100).GetPeakDecibels(.5, .52);
        foreach (int strength in new[] { 200, 300 })
        {
            var reference = Tone(rate, 1000, strength);
            Assert.Equal(strength, reference.WeightingPercent);
            Assert.InRange(reference.GetDecibels(.5, .501), -15.10, -15.00);
            var highTone = Tone(rate, frequency, strength);
            double high = highTone.GetDecibels(.5, .52);
            double peak = highTone.GetPeakDecibels(.5, .52);
            double low = Tone(rate, 50, strength).GetDecibels(.5, .52);
            Assert.InRange(high - previousHigh, 2.5, 4.1);
            Assert.InRange(low - previousLow, -6.5, -5.0);
            Assert.InRange(peak - previousPeak, 2.0, 4.5);
            previousHigh = high;
            previousLow = low;
            previousPeak = peak;
        }
    }

    [Fact]
    public void ZeroWeightingExactlyPreservesOriginalAnalysisAndInputBytes()
    {
        using var wave = Wave(2, 32, 3, 24_001, (i, c) => .2 * Math.Sin(i * .37 + c));
        byte[] original = wave.ToArray();
        var raw = EnergyEnvelope.Read(wave);
        wave.Position = 0;
        var disabled = EnergyEnvelope.Read(wave, weightingPercent: 0);
        Assert.Equal(raw.Decibels, disabled.Decibels);
        Assert.Equal(raw.PeakDecibels, disabled.PeakDecibels);
        Assert.Equal(0, disabled.WeightingPercent);
        wave.Position = 0;
        EnergyEnvelope.Read(wave, weightingPercent: 100);
        Assert.Equal(original, wave.ToArray());
    }

    [Fact]
    public void StronglyWeightedPeakCanExceedZeroDbWithoutBeingClippedDuringAnalysis()
    {
        using var wave = Wave(1, 32, 3, 48000,
            (i, _) => .75 * Math.Sin(2 * Math.PI * 6000 * i / 48000), rate: 48000);
        var envelope = EnergyEnvelope.Read(wave, weightingPercent: 300);
        Assert.InRange(envelope.GetPeakDecibels(.5, .52), 6, 10);
        Assert.All(envelope.PeakDecibels, db => Assert.True(float.IsFinite(db)));
    }

    [Fact]
    public void WeightedAnalysisHasIndependentChannelStateAndPreservesGainInDb()
    {
        var mono = Tone(48000, 3500, 100);
        var stereo = Tone(48000, 3500, 100, channels: 2);
        Assert.Equal(mono.Decibels, stereo.Decibels);
        Assert.Equal(mono.PeakDecibels, stereo.PeakDecibels);
        using var leftOnly = Wave(2, 32, 3, 48000,
            (i, c) => c == 0 ? .25 * Math.Sin(2 * Math.PI * 3500 * i / 48000) : 0, rate: 48000);
        var left = EnergyEnvelope.Read(leftOnly, weightingPercent: 100);
        Assert.Equal(mono.GetDecibels(.5, .501) - 10 * Math.Log10(2), left.GetDecibels(.5, .501), 4);
        Assert.Equal(mono.PeakDecibels, left.PeakDecibels);
        using var louder = Wave(1, 32, 3, 48000,
            (i, _) => .5 * Math.Sin(2 * Math.PI * 3500 * i / 48000), rate: 48000);
        var doubled = EnergyEnvelope.Read(louder, weightingPercent: 100);
        Assert.Equal(20 * Math.Log10(2), doubled.GetDecibels(.5, .501) - mono.GetDecibels(.5, .501), 4);
    }

    [Theory]
    [InlineData(8000, 100)]
    [InlineData(8000, 300)]
    [InlineData(384000, 100)]
    [InlineData(384000, 300)]
    public void WeightingStaysFiniteThroughImpulseNonfiniteInputAndSilence(int rate, int weighting)
    {
        using var silence = Wave(1, 32, 3, rate, (_, _) => 0, rate);
        var empty = EnergyEnvelope.Read(silence, weightingPercent: weighting);
        Assert.All(empty.Decibels, db => Assert.Equal(-120, db));
        Assert.All(empty.PeakDecibels, db => Assert.Equal(-120, db));
        using var impulse = Wave(2, 32, 3, rate,
            (i, c) => i == 0 ? .9 : i == 1 ? double.NaN : i == 2 ? double.PositiveInfinity : 0, rate);
        var decay = EnergyEnvelope.Read(impulse, weightingPercent: weighting);
        Assert.All(decay.Decibels, db => Assert.True(float.IsFinite(db)));
        Assert.All(decay.PeakDecibels, db => Assert.True(float.IsFinite(db)));
        Assert.Equal(-120, decay.Decibels[^1]);
        Assert.Equal(-120, decay.PeakDecibels[^1]);
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(24, 1)]
    [InlineData(32, 1)]
    [InlineData(32, 3)]
    public void HalfScaleIsMinusSixDbAndPartialWindowUsesItsActualLength(int bits, int tag)
    {
        using var wave = Wave(1, bits, tag, 170, (_, _) => -.5);
        var envelope = EnergyEnvelope.Read(wave);
        Assert.Equal(22, envelope.Decibels.Length);
        Assert.Equal(.001, envelope.StepSeconds, 8);
        Assert.Equal(170d / 8000, envelope.DurationSeconds, 8);
        Assert.All(envelope.Decibels, db => Assert.InRange(db, -6.021f, -6.020f));
        Assert.All(envelope.PeakDecibels, db => Assert.InRange(db, -6.021f, -6.020f));
    }

    [Fact]
    public void AntiphaseStereoKeepsItsEnergy()
    {
        using var wave = Wave(2, 16, 1, 160, (_, channel) => channel == 0 ? .5 : -.5);
        Assert.InRange(EnergyEnvelope.Read(wave).Decibels[0], -6.021f, -6.020f);
    }

    [Fact]
    public void QuietVoiceBelowRmsFloorStillHasVisiblePeakEnvelopeAtDefaultRange()
    {
        using var wave = Wave(1, 32, 3, 800, (i, _) => .04 * Math.Sin(2 * Math.PI * i / 8));
        var envelope = EnergyEnvelope.Read(wave);
        Assert.All(envelope.Decibels, db => Assert.True(db < -30));
        Assert.All(envelope.PeakDecibels, db => Assert.InRange(db, -27.960f, -27.958f));
        Assert.True(envelope.GetPeakDecibels(0, .1) > -30);
        Assert.True(envelope.GetDecibels(0, .1) < -30);
        Assert.Equal(-120, envelope.GetPeakDecibels(-1, -.1));
        Assert.Equal(-120, envelope.GetPeakDecibels(.1, 1));
    }

    [Fact]
    public void SineUsesRmsRatherThanPeakAndDoublingAmplitudeAddsSixDb()
    {
        using var quiet = Wave(1, 32, 3, 800, (i, _) => .25 * Math.Sin(2 * Math.PI * i / 8));
        using var loud = Wave(1, 32, 3, 800, (i, _) => .5 * Math.Sin(2 * Math.PI * i / 8));
        var a = EnergyEnvelope.Read(quiet);
        var b = EnergyEnvelope.Read(loud);
        Assert.InRange(a.Decibels[0], -15.052f, -15.051f);
        Assert.InRange(b.Decibels[0] - a.Decibels[0], 6.020f, 6.021f);
    }

    [Fact]
    public void SilenceAndOutsideAudioStayAtFloorWhileZoomedOutQueriesKeepTransients()
    {
        using var wave = Wave(1, 16, 1, 480, (i, _) => i is >= 160 and < 320 ? .5 : 0);
        var envelope = EnergyEnvelope.Read(wave);
        Assert.Equal(-120, envelope.GetDecibels(-1, -.1));
        Assert.Equal(-120, envelope.GetDecibels(.06, 1));
        Assert.Equal(-120, envelope.Decibels[0]);
        Assert.Equal(-120, envelope.Decibels[^1]);
        Assert.InRange(envelope.GetDecibels(0, .06), -6.021, -6.020);
    }

    [Fact]
    public void NonFiniteFloatSamplesAreIgnored()
    {
        using var wave = Wave(1, 32, 3, 160, (i, _) => i % 2 == 0 ? double.NaN : double.PositiveInfinity);
        Assert.Equal(-120, EnergyEnvelope.Read(wave).Decibels[0]);
    }

    [Fact]
    public void HighZoomInterpolatesAmplitudeAndPowerRatherThanHoldingAnalysisBuckets()
    {
        var envelope = new EnergyEnvelope(.001, .002, new float[] { -40, -20 }, new float[] { -40, -20 });
        Assert.Equal(-40, envelope.GetPeakDecibels(.0005, .0005), 6);
        Assert.Equal(-20, envelope.GetPeakDecibels(.0015, .0015), 6);
        Assert.Equal(20 * Math.Log10(.055), envelope.GetPeakDecibels(.001, .001), 6);
        Assert.Equal(10 * Math.Log10(.00505), envelope.GetDecibels(.001, .001), 6);
    }

    [Fact]
    public void ZoomedOutQueriesDoNotBorrowPeakFromTheNextPixelInterval()
    {
        var envelope = new EnergyEnvelope(.001, .004, new float[] { -40, -40, -6, -40 },
            new float[] { -40, -40, -6, -40 });
        Assert.Equal(-40, envelope.GetPeakDecibels(0, .002));
        Assert.Equal(-6, envelope.GetPeakDecibels(.001, .003));
        Assert.Equal(-6, envelope.GetPeakDecibels(0, .0021));
        Assert.Equal(-40, envelope.GetDecibels(0, .002));
    }

    [Fact]
    public void MillisecondHopsKeepShortTransientsAndProduceOverlappingRmsWindows()
    {
        using var wave = Wave(1, 32, 3, 800, (i, _) => i == 83 ? .8 : 0);
        var envelope = EnergyEnvelope.Read(wave);
        Assert.Equal(.001, envelope.StepSeconds, 8);
        Assert.Equal(100, envelope.PeakDecibels.Length);
        Assert.InRange(envelope.GetPeakDecibels(.010, .011), -1.939, -1.938);
        Assert.Equal(-120, envelope.GetPeakDecibels(.030, .031));
        Assert.InRange(envelope.Decibels.Count(db => db > -100), 19, 21);
        Assert.InRange(envelope.PeakDecibels.Count(db => db > -100), 9, 11);
    }

    [Fact]
    public void AnalysisOptionsClampAtExtendedWeightingLimits()
    {
        using var wave = Wave(1, 32, 3, 160, (_, _) => 0);
        var high = EnergyEnvelope.Read(wave, weightingPercent: 1000);
        Assert.Equal(300, high.WeightingPercent);
        wave.Position = 0;
        var low = EnergyEnvelope.Read(wave, weightingPercent: -1);
        Assert.Equal(0, low.WeightingPercent);
    }

    [Fact]
    public void TruncatedRiffIsRejectedAndCancellationIsObserved()
    {
        using var wave = Wave(1, 16, 1, 160, (_, _) => 0);
        var bytes = wave.ToArray();
        using var truncated = new MemoryStream(bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => EnergyEnvelope.Read(truncated));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EnergyEnvelope.Read(wave, cancellation.Token));
    }
}
