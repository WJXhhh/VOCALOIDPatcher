using VOCALOIDPatcher.Rendering;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class PostProcessingQuietPeriodTests
{
    [Fact]
    public void ReadyAudioDoesNotInventAQuietPeriod()
    {
        var quiet = new PostProcessingQuietPeriod(100);
        Assert.Equal(0, quiet.UpstreamReady(500));
        Assert.Equal(100, quiet.Edited(500));
        Assert.Equal(0, quiet.UpstreamReady(600));
        Assert.Equal(0, quiet.UpstreamReady(601));
    }

    [Fact]
    public void RepeatedReadinessKeepsTheOriginalEditDeadline()
    {
        var quiet = new PostProcessingQuietPeriod(100); quiet.Edited(1000);
        Assert.Equal(75, quiet.UpstreamReady(1025));
        Assert.Equal(30, quiet.UpstreamReady(1070));
        Assert.Equal(1, quiet.UpstreamReady(1099));
        Assert.Equal(0, quiet.UpstreamReady(1100));
    }

    [Fact]
    public void LatestEditRestartsItsOwnStabilityWindow()
    {
        var quiet = new PostProcessingQuietPeriod(120); quiet.Edited(1000);
        Assert.Equal(20, quiet.UpstreamReady(1100));
        Assert.Equal(120, quiet.Edited(1105));
        Assert.Equal(115, quiet.UpstreamReady(1110));
        Assert.Equal(0, quiet.UpstreamReady(1225));
    }

    [Fact]
    public void PartsHaveIndependentDeadlinesAndUseLongTicks()
    {
        var first = new PostProcessingQuietPeriod(100); var second = new PostProcessingQuietPeriod(100);
        long start = (long)int.MaxValue + 1000;
        first.Edited(start);
        Assert.Equal(50, first.UpstreamReady(start + 50));
        Assert.Equal(0, second.UpstreamReady(start + 50));
        second.Edited(start + 50);
        Assert.Equal(0, first.UpstreamReady(start + 100));
        Assert.Equal(50, second.UpstreamReady(start + 100));
    }

    [Fact]
    public void InvalidWindowIsRejectedAndEarlierClockDoesNotBypassEdits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostProcessingQuietPeriod(0));
        var quiet = new PostProcessingQuietPeriod(100); quiet.Edited(1000);
        Assert.Equal(100, quiet.UpstreamReady(999));
    }
}
