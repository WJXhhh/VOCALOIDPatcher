using VOCALOIDPatcher.Utils;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class CachedDiagnosticSwitchTests
{
    [Fact]
    public void MarkerChangesAreObservedAtTheNextPollWithoutPerCallbackFileReads()
    {
        long now = 0;
        bool marker = false;
        int reads = 0;
        var flag = new CachedDiagnosticSwitch(() => { reads++; return marker; }, () => now, 250);
        Assert.False(flag.IsEnabled);
        marker = true;
        now = 249;
        for (int i = 0; i < 10_000; i++) Assert.False(flag.IsEnabled);
        Assert.Equal(1, reads);
        now = 250;
        Assert.True(flag.IsEnabled);
        marker = false;
        now = 500;
        Assert.False(flag.IsEnabled);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void ConcurrentCallbacksShareOnePollAndReadFailuresDisableObservation()
    {
        int reads = 0;
        var flag = new CachedDiagnosticSwitch(() =>
        {
            Interlocked.Increment(ref reads);
            throw new IOException("Unavailable marker");
        }, () => 100, 250);
        Parallel.For(0, 1000, _ => Assert.False(flag.IsEnabled));
        Assert.Equal(1, reads);
    }
}
