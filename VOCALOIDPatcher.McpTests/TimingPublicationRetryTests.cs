using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TimingPublicationRetryTests
{
    [Fact]
    public void ContentionBacksOffAndStopsAfterFiveSecondsForTheSameRevision()
    {
        var budget = new TimingPublicationRetryBudget();
        int[] expected = [25, 50, 100, 200, 250, 250];
        long now = 100;
        foreach (int delay in expected)
        {
            Assert.True(budget.TrySchedule(7, now, out int actual));
            Assert.Equal(delay, actual);
            now += delay;
        }
        Assert.False(budget.TrySchedule(7, 5100, out _));
        Assert.False(budget.TrySchedule(7, 6000, out _));
    }

    [Fact]
    public void NewEditsAndSuccessfulPublicationReceiveAFreshBudget()
    {
        var budget = new TimingPublicationRetryBudget();
        Assert.True(budget.TrySchedule(7, 0, out _));
        Assert.False(budget.TrySchedule(7, 5000, out _));
        Assert.True(budget.TrySchedule(8, 5001, out int delay));
        Assert.Equal(25, delay);
        budget.Reset();
        Assert.True(budget.TrySchedule(8, 10000, out delay));
        Assert.Equal(25, delay);
        Assert.IsAssignableFrom<TimeoutException>(new TimingPlannerBusyException("busy"));
        Assert.IsNotType<TimingPlannerBusyException>(new TimeoutException("unrelated timeout"));
    }
}
