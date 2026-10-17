using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class ConsonantTimingMathTests
{
    [Theory]
    [InlineData(64, .32)]
    [InlineData(127, .32)]
    [InlineData(32, .16)]
    [InlineData(0, .08)]
    public void ValueOnlyPreviewPreservesNativeEmotionSensitivity(int velocity, double expected)
    {
        Assert.Equal(expected, ConsonantTimingMath.DragWindowSeconds(velocity, 1, .92), 10);
    }

    [Fact]
    public void ValueOnlyPreviewRetainsSignedTruncationAndSaturation()
    {
        Assert.Equal(64, ConsonantTimingMath.VelocityFromDrag(64, 9, 1270));
        Assert.Equal(64, ConsonantTimingMath.VelocityFromDrag(64, -9, 1270));
        Assert.Equal(65, ConsonantTimingMath.VelocityFromDrag(64, 10, 1270));
        Assert.Equal(63, ConsonantTimingMath.VelocityFromDrag(64, -10, 1270));
        Assert.Equal(0, ConsonantTimingMath.VelocityFromDrag(64, long.MinValue, 1));
        Assert.Equal(127, ConsonantTimingMath.VelocityFromDrag(64, long.MaxValue, 1));
        Assert.Equal(64, ConsonantTimingMath.VelocityFromDrag(0, 100, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeResponseSearchMatchesExhaustiveNearestPosition(int index)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "consonant-timing-response.json")));
        long[] starts = fixture.RootElement.GetProperty("cases")[index].GetProperty("starts")
            .EnumerateArray().Select(x => x.GetInt64()).ToArray();
        Assert.Equal(128, starts.Length);
        Assert.True(starts.SequenceEqual(starts.OrderBy(x => x)));
        for (long target = starts[0] - 5; target <= starts[^1] + 5; target++)
        {
            int calls = 0;
            var sample = await ConsonantTimingMath.FindNearestAsync(target, 64, velocity =>
            {
                calls++;
                return Task.FromResult(new ConsonantTimingSample(velocity, [starts[velocity], -2, 477]));
            }, () => true);
            Assert.Equal(starts.Min(start => Math.Abs(start - target)), Math.Abs(sample.Boundaries[0] - target));
            Assert.InRange(calls, 1, 10);
        }
    }

    [Fact]
    public async Task UnmovedOrPlateauPositionPreservesTheCurrentVelocity()
    {
        int calls = 0;
        var sample = await ConsonantTimingMath.FindNearestAsync(-30, 65, velocity =>
        {
            calls++;
            return Task.FromResult(new ConsonantTimingSample(velocity, [-30, -2, 477]));
        }, () => true);
        Assert.Equal(65, sample.Velocity);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(long.MinValue, 0)]
    [InlineData(long.MaxValue, 127)]
    public async Task UnreachableAndExtremeTargetsSnapToTheEngineLimit(long target, int expected)
    {
        var sample = await ConsonantTimingMath.FindNearestAsync(target, 64,
            velocity => Task.FromResult(new ConsonantTimingSample(velocity, [velocity, 200])), () => true);
        Assert.Equal(expected, sample.Velocity);
    }

    [Fact]
    public async Task ANewRequestCancelsTheOldResultAfterAnEngineAwait()
    {
        bool current = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ConsonantTimingMath.FindNearestAsync(30, 64,
            velocity =>
            {
                current = false;
                return Task.FromResult(new ConsonantTimingSample(velocity, [velocity, 200]));
            }, () => current));
    }

    [Fact]
    public async Task NonMonotonicResponsesAreRejectedInsteadOfInventingATiming()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsonantTimingMath.FindNearestAsync(30, 64,
            velocity => Task.FromResult(new ConsonantTimingSample(velocity, [127 - velocity, 200])), () => true));
    }
}
