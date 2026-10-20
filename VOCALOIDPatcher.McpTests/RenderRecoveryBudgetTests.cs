using VOCALOIDPatcher.Rendering;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

public sealed class RenderRecoveryBudgetTests
{
    [Fact]
    public void FailedRetryDoesNotStartAnInfiniteRenderLoop()
    {
        var budget = new RenderRecoveryBudget();
        Assert.True(budget.TryAcquire(1, 0));
        Assert.False(budget.TryAcquire(1, 100));
        Assert.False(budget.TryAcquire(1, 60_001));
        Assert.True(budget.TryAcquire(2, 60_001));
    }

    [Fact]
    public void RapidEditsCannotResetTheCircuitBreaker()
    {
        var budget = new RenderRecoveryBudget();
        Assert.True(budget.TryAcquire(1, 0));
        Assert.True(budget.TryAcquire(2, 10));
        Assert.True(budget.TryAcquire(3, 20));
        Assert.False(budget.TryAcquire(4, 30));
        Assert.True(budget.TryAcquire(4, 60_000));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(10)] [InlineData(20)] [InlineData(999)]
    public void CancellationMemoryPressureAndInvalidBanksAreNotBlindlyRetried(int reason)
        => Assert.False(RenderRecoveryBudget.CanRetry(reason));

    [Theory]
    [InlineData(8)] [InlineData(14)] [InlineData(17)] [InlineData(18)]
    public void StoppedEngineAndTemporaryIoFailuresAllowOneAttempt(int reason)
        => Assert.True(RenderRecoveryBudget.CanRetry(reason));

    private sealed class Resource : IDisposable { internal bool Disposed; public void Dispose() => Disposed = true; }
    [Fact]
    public void RebuildingTheBackendReleasesItWithoutDiscardingItsDocumentOwner()
    {
        int created = 0;
        using var lease = new TimingBackendLease<Resource>(() => { created++; return new(); });
        var original = lease.Require(); lease.Reset();
        Assert.True(original.Disposed);
        Assert.False(lease.IsCreated);
        Assert.NotSame(original, lease.Require());
        Assert.Equal(2, created);
    }
}
