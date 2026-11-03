using VOCALOIDPatcher.Utils;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class BoundedPreviewPoolTests
{
    [Fact]
    public void PreviewUpdatesReuseObjectsWithoutAliasingSimultaneousPoints()
    {
        int created = 0;
        var pool = new BoundedPreviewPool<object>(() => { created++; return new(); }, 3);
        pool.BeginUpdate();
        object first = pool.Rent(), second = pool.Rent();
        Assert.NotSame(first, second);
        pool.BeginUpdate();
        Assert.Same(first, pool.Rent());
        Assert.Same(second, pool.Rent());
        Assert.Equal(2, created);
    }

    [Fact]
    public void LongGesturesDoNotGrowRetainedObjectsWithoutBound()
    {
        var pool = new BoundedPreviewPool<object>(() => new(), 2);
        pool.BeginUpdate();
        object a = pool.Rent(), b = pool.Rent(), overflow = pool.Rent();
        pool.BeginUpdate();
        Assert.Same(a, pool.Rent());
        Assert.Same(b, pool.Rent());
        Assert.NotSame(overflow, pool.Rent());
    }
}
