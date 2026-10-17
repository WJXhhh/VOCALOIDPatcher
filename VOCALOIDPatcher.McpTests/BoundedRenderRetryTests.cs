using VOCALOIDPatcher.Evec;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public class BoundedRenderRetryTests
{
    [Fact]
    public void SecondAttemptUsesFreshResourceUnderSameRetryBudget()
    {
        object copy = new();
        var rendered = new HashSet<object>();
        int prepared = 0;
        Assert.True(BoundedRenderRetry.Run(() => false, index =>
        {
            Assert.True(rendered.Add(copy));
            return index == 1;
        }, () => true, () => { copy = new(); prepared++; return true; }));
        Assert.Equal(1, prepared);
        Assert.Equal(2, rendered.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrCanceledPreparationDoesNotStartSecondRender(bool cancel)
    {
        bool canceled = false;
        int calls = 0;
        Assert.False(BoundedRenderRetry.Run(() => canceled,
            _ => { calls++; return false; }, () => true,
            () => { canceled = cancel; return cancel; }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0, true, 1, 0)]
    [InlineData(1, true, 2, 1)]
    [InlineData(2, false, 2, 1)]
    public void StopsAfterSuccessOrOneRetry(int failures, bool expected, int calls, int waits)
    {
        int actualCalls = 0, actualWaits = 0;
        bool result = BoundedRenderRetry.Run(() => false,
            _ => actualCalls++ >= failures, () => { actualWaits++; return true; });
        Assert.Equal(expected, result);
        Assert.Equal(calls, actualCalls);
        Assert.Equal(waits, actualWaits);
    }

    [Fact]
    public void RemovalDuringDelayPreventsSecondAttempt()
    {
        bool canceled = false;
        int calls = 0;
        Assert.False(BoundedRenderRetry.Run(() => canceled,
            _ => { calls++; return false; }, () => { canceled = true; return true; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CancellationDuringSuccessfulAttemptPreventsPublication()
    {
        bool canceled = false;
        Assert.False(BoundedRenderRetry.Run(() => canceled,
            _ => { canceled = true; return true; }, () => throw new Exception()));
    }
}
