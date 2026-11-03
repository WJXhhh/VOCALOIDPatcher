using VOCALOIDPatcher.Rendering;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public class PartLoadTicketsTests
{
    [Fact]
    public void RenderingOnePartDoesNotDiscardAnotherPartsPendingPcm()
    {
        var loads = new PartLoadTickets();
        Assert.True(loads.TryBegin(1, out long first));
        Assert.True(loads.TryBegin(2, out long second));
        loads.Invalidate(1);
        Assert.False(loads.IsCurrent(1, first));
        Assert.True(loads.IsCurrent(2, second));
        Assert.True(loads.Complete(2, second));
    }

    [Fact]
    public void StaleCompletionCannotReplaceOrRemoveANewerLoadAtTheSamePath()
    {
        var loads = new PartLoadTickets();
        Assert.True(loads.TryBegin(1, out long old));
        Assert.False(loads.TryBegin(1, out _));
        loads.Invalidate(1);
        Assert.True(loads.TryBegin(1, out long current));
        Assert.False(loads.Complete(1, old));
        Assert.True(loads.IsCurrent(1, current));
        Assert.True(loads.Complete(1, current));
    }

    [Fact]
    public void ClearDiscardsEveryPendingLoadWithoutReusingTickets()
    {
        var loads = new PartLoadTickets();
        Assert.True(loads.TryBegin(1, out long old));
        loads.Clear();
        Assert.True(loads.TryBegin(1, out long current));
        Assert.NotEqual(old, current);
        Assert.False(loads.Complete(1, old));
    }
}
