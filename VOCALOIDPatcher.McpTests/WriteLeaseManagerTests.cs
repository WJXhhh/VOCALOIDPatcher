using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class WriteLeaseManagerTests
{
    [Fact]
    public void OnlyOneClientCanHoldLease()
    {
        var lease = new WriteLeaseManager(TimeSpan.FromMinutes(5));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(lease.TryAcquire("one", "Client One", now, out _));
        Assert.False(lease.TryAcquire("two", "Client Two", now, out string? heldBy));
        Assert.Equal("Client One", heldBy);
        Assert.True(lease.Release("one"));
        Assert.True(lease.TryAcquire("two", "Client Two", now, out _));
    }

    [Fact]
    public void IdleLeaseExpires()
    {
        var lease = new WriteLeaseManager(TimeSpan.FromSeconds(10));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(lease.TryAcquire("one", "Client One", now, out _));

        Assert.True(lease.TryAcquire("two", "Client Two", now.AddSeconds(11), out _));
    }

    [Fact]
    public void ActiveJobPreventsIdleExpiry()
    {
        var lease = new WriteLeaseManager(TimeSpan.FromSeconds(10));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(lease.TryAcquire("one", "Client One", now, out _));
        Assert.True(lease.BeginJob("one", now));

        Assert.False(lease.TryAcquire("two", "Client Two", now.AddMinutes(1), out _));
        lease.EndJob("one", now.AddMinutes(1));
        Assert.True(lease.TryAcquire("two", "Client Two", now.AddMinutes(1).AddSeconds(11), out _));
    }
}
