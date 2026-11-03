using System.Buffers.Binary;
using VOCALOIDPatcher.VoiceComp;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class CompWaveStreamingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "v6-comp-stream-tests", Guid.NewGuid().ToString("N"));
    public CompWaveStreamingTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(1, 16, 1)]
    [InlineData(1, 16, 2)]
    [InlineData(1, 24, 1)]
    [InlineData(1, 24, 2)]
    [InlineData(1, 32, 1)]
    [InlineData(1, 32, 2)]
    [InlineData(3, 32, 1)]
    [InlineData(3, 32, 2)]
    public void CombinedPeaksMeasureQuantizedOutputAcrossBlocks(int tag, int bits, int channels)
    {
        string input = Fixture(tag, bits, channels), output = Path.Combine(_directory, "out.wav");
        // Cross a block boundary, mix clipped overlaps, retain empty head/tail
        // bins and finish in a partial block. Stereo channels differ in sign.
        var plan = new CompRenderPlan(44100, 9007,
            [new(input, 0, 3, 8999, 31, 33), new(input, 17, 4091, 3001, 43, 47)]);
        foreach (int bins in new[] { 1, 7, 1024, 4096 })
        {
            var result = CompWave.MixWithPeaks(output, plan, bins: bins);
            Assert.Equal(CompWave.Peaks(output, bins), result.Peaks);
            Assert.True(result.Clipped > 0);
            Assert.Equal(9007, CompWave.Inspect(output).Frames);
            var pcm = File.ReadAllBytes(output).AsSpan(44);
            Assert.Equal(new byte[3 * channels * 2], pcm[..(3 * channels * 2)].ToArray());
            Assert.Equal(new byte[5 * channels * 2], pcm[^(5 * channels * 2)..].ToArray());
        }
    }

    [Fact]
    public void SingleFrameClipsAndPeaksAreBasedOnWrittenPcm()
    {
        string input = Fixture(3, 32, 1), output = Path.Combine(_directory, "single.wav");
        var result = CompWave.MixWithPeaks(output, new(44100, 1, [new(input, 1, 0, 1), new(input, 1, 0, 1)]));
        Assert.Equal(1, result.Clipped);
        Assert.Equal(1f, result.Peaks[0]);
        Assert.All(result.Peaks.Skip(1), p => Assert.Equal(0f, p));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(File.ReadAllBytes(output).AsSpan(44)));
    }

    [Fact]
    public void CancellationAndInvalidBinsLeaveDestinationIntact()
    {
        string input = Fixture(1, 16, 2), output = Path.Combine(_directory, "retained.wav");
        byte[] retained = [1, 2, 3, 4]; File.WriteAllBytes(output, retained);
        var plan = new CompRenderPlan(44100, 9000, [new(input, 0, 0, 9000)]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompWave.MixWithPeaks(output, plan, cancellation.Token));
        foreach (int bins in new[] { 0, 4097 })
            Assert.Throws<ArgumentOutOfRangeException>(() => CompWave.MixWithPeaks(output, plan, bins: bins));
        Assert.Equal(retained, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    private string Fixture(int tag, int bits, int channels)
    {
        string path = Path.Combine(_directory, "source.wav"); const int frames = 9000;
        using var writer = new BinaryWriter(File.Create(path)); int align = channels * bits / 8;
        writer.Write(0x46464952u); writer.Write(36 + frames * align); writer.Write(0x45564157u);
        writer.Write(0x20746d66u); writer.Write(16); writer.Write((short)tag); writer.Write((short)channels);
        writer.Write(44100); writer.Write(44100 * align); writer.Write((short)align); writer.Write((short)bits);
        writer.Write(0x61746164u); writer.Write(frames * align);
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
            {
                double value = (i % 5) switch { 0 => .99999, 1 => -1, 2 => .0000152587890625, 3 => -.4, _ => .6 };
                if (c == 1) value = -value * .75;
                if (tag == 3) writer.Write((float)value);
                else if (bits == 16) writer.Write((short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue));
                else if (bits == 32) writer.Write((int)Math.Clamp(Math.Round(value * 2147483648), int.MinValue, int.MaxValue));
                else
                {
                    int sample = (int)Math.Clamp(Math.Round(value * 8388608), -8388608, 8388607);
                    writer.Write((byte)sample); writer.Write((byte)(sample >> 8)); writer.Write((byte)(sample >> 16));
                }
            }
        return path;
    }
}
