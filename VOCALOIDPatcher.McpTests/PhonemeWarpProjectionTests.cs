using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class PhonemeWarpProjectionTests
{
    [Fact]
    public void ValidWarpIsExactAndRequestedValuesStayUnchanged()
    {
        PhonemeWarpRequest[] input = [new(0, 0, false), new(10, 17, true), new(20, 29, true), new(40, 40, false)];
        var copy = input.ToArray();
        Assert.Equal(input.Select(p => new TransitionAnchor(p.From, p.To)), PhonemeWarpProjection.Resolve(input));
        Assert.Equal(copy, input);
    }

    [Theory]
    [InlineData(-100, 1)]
    [InlineData(5, 5)]
    [InlineData(100, 9)]
    public void OneEditedBoundaryStopsInsideItsFixedNeighbors(int requested, int expected)
    {
        var result = PhonemeWarpProjection.Resolve([new(0, 0, false), new(5, requested, true), new(10, 10, false)]);
        Assert.Equal(expected, result[1].To);
        Assert.Equal(new TransitionAnchor(0, 0), result[0]);
        Assert.Equal(new TransitionAnchor(10, 10), result[2]);
    }

    [Fact]
    public void ProjectionMatchesExhaustiveNearestLegalIntegerWarp()
    {
        var random = new Random(2471);
        for (int sample = 0; sample < 100; sample++)
        {
            int[] wanted = Enumerable.Range(0, 3).Select(_ => random.Next(-5, 14)).ToArray();
            var result = PhonemeWarpProjection.Resolve([new(0, 0, false), new(1, wanted[0], true),
                new(2, wanted[1], true), new(3, wanted[2], true), new(8, 8, false)]);
            int cost = Enumerable.Range(0, 3).Sum(i => (result[i + 1].To - wanted[i]) * (result[i + 1].To - wanted[i]));
            int best = int.MaxValue;
            for (int a = 1; a < 6; a++)
            for (int b = a + 1; b < 7; b++)
            for (int c = b + 1; c < 8; c++)
                best = Math.Min(best, (a - wanted[0]) * (a - wanted[0]) + (b - wanted[1]) * (b - wanted[1]) + (c - wanted[2]) * (c - wanted[2]));
            Assert.Equal(best, cost);
            Assert.True(result.Zip(result.Skip(1)).All(p => p.First.To < p.Second.To));
        }
    }

    [Fact]
    public void FixedBoundarySeparatesIndependentEditedRuns()
    {
        var result = PhonemeWarpProjection.Resolve([new(0, 0, false), new(5, 100, true), new(10, 10, false),
            new(15, -100, true), new(20, 20, false)]);
        Assert.Equal(new[] { 0, 9, 10, 11, 20 }, result.Select(p => p.To));
    }

    [Fact]
    public void ImpossibleFixedGeometryAndUnboundedInputsStillFail()
    {
        Assert.Throws<NotSupportedException>(() => PhonemeWarpProjection.Resolve([new(0, 0, true), new(10, 10, false)]));
        Assert.Throws<NotSupportedException>(() => PhonemeWarpProjection.Resolve([new(0, 0, false), new(10, 10, true)]));
        Assert.Throws<NotSupportedException>(() => PhonemeWarpProjection.Resolve([new(0, 0, false), new(5, 0, true), new(10, 1, false)]));
        Assert.Throws<NotSupportedException>(() => PhonemeWarpProjection.Resolve([new(0, 0, false), new(0, 10, false)]));
    }

    [Fact]
    public void FirstControlRespectsTheNativeOutputDomain()
    {
        var result = PhonemeWarpProjection.Resolve([new(0, 0, false), new(3200, -100, true), new(3300, 3300, false)], 3087);
        Assert.Equal(3087, result[1].To);
    }

    [Fact]
    public void ExtremeRequestedFramesCannotOverflowTheProjection()
    {
        var result = PhonemeWarpProjection.Resolve([new(0, 0, false), new(1, int.MaxValue, true),
            new(2, int.MinValue, true), new(3, 3, false)]);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Select(p => p.To));
    }
}
