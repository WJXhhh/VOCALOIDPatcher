using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TimingCacheInvalidationScopeTests
{
    [Fact]
    public void NestedInvalidationsOnlySuppressTheirOwnSourcesAndRestoreAfterFailure()
    {
        Assert.Throws<IOException>((Action)(() =>
        {
            using var outer = TimingCacheInvalidationScope.Enter(1);
            using (TimingCacheInvalidationScope.Enter(1))
            using (TimingCacheInvalidationScope.Enter(2))
            {
                Assert.True(TimingCacheInvalidationScope.IsActive(1));
                Assert.True(TimingCacheInvalidationScope.IsActive(2));
                Assert.False(TimingCacheInvalidationScope.IsActive(3));
            }
            Assert.True(TimingCacheInvalidationScope.IsActive(1));
            Assert.False(TimingCacheInvalidationScope.IsActive(2));
            throw new IOException();
        }));
        Assert.False(TimingCacheInvalidationScope.IsActive(1));
        Assert.False(TimingCacheInvalidationScope.IsActive(2));
    }

    [Fact]
    public void AnotherThreadStillProcessesRealEditsAndDisposalIsIdempotent()
    {
        var scope = TimingCacheInvalidationScope.Enter(1);
        try
        {
            bool suppressed = true;
            var thread = new Thread(() => suppressed = TimingCacheInvalidationScope.IsActive(1));
            thread.Start();
            thread.Join();
            Assert.False(suppressed);
        }
        finally { scope.Dispose(); }
        scope.Dispose();
        Assert.False(TimingCacheInvalidationScope.IsActive(1));
        Assert.Throws<ArgumentException>(() => TimingCacheInvalidationScope.Enter(0));
    }
}
