using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class ConsonantTimingDragSearchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void IncrementalDragFindsNearestActualFrameWithoutSweepingVelocities(int index)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "consonant-timing-response.json")));
        var starts = fixture.RootElement.GetProperty("cases")[index].GetProperty("starts")
            .EnumerateArray().Select(x => x.GetInt64()).ToArray();
        for (long goal = starts[0] - 5; goal <= starts[^1] + 5; goal++)
        {
            var search = new ConsonantTimingDragSearch(64, [starts[64], -2, 477]);
            search.SetGoal(goal);
            int calls = 0;
            for (int step = 0; step < 10 && !search.Complete; step++)
                search.Step(v => { calls++; return [starts[v], -2, 477]; });
            Assert.True(search.Complete);
            Assert.InRange(calls, 0, 8);
            Assert.Equal(starts.Min(start => Math.Abs(start - goal)), Math.Abs(search.Best.Boundaries[0] - goal));
        }
    }

    [Fact]
    public void BusyAndLatestPointerUpdatesNeverPublishAnUnmeasuredCandidate()
    {
        var search = new ConsonantTimingDragSearch(64, [64, 200]);
        search.SetGoal(20);
        search.Step(_ => null);
        Assert.Equal(64, search.Best.Velocity);
        search.SetGoal(100);
        for (int i = 0; i < 10 && !search.Complete; i++) search.Step(v => [v, 200]);
        Assert.True(search.Complete);
        Assert.Equal(100, search.Best.Velocity);
        Assert.Equal(100, search.Best.Boundaries[0]);
    }

    [Fact]
    public void StationaryPointerAndPlateauPreserveTheOriginalVelocity()
    {
        var search = new ConsonantTimingDragSearch(70, [-30, -2, 477]);
        search.SetGoal(-30);
        Assert.True(search.Complete);
        Assert.Equal(70, search.Best.Velocity);
    }
}
