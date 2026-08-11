using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class RequestGuardTests
{
    [Fact]
    public void RevisionGuardRejectsProjectAndRevisionChanges()
    {
        Assert.Null(ProjectRevisionGuard.Validate("p", 4, "p", 4));

        BridgeError project = ProjectRevisionGuard.Validate("new", 1, "old", 1)!;
        Assert.Equal("stale_project", project.Code);
        Assert.Equal("new", project.Details!.Value.GetProperty("project_id").GetString());

        BridgeError revision = ProjectRevisionGuard.Validate("p", 5, "p", 4)!;
        Assert.Equal(5, revision.Details!.Value.GetProperty("revision").GetInt64());
    }

    [Fact]
    public void IdempotencyCacheReplaysAndBoundsEntries()
    {
        var cache = new BoundedIdempotencyCache<int>(2);
        cache.Store("client:request-1", 10);
        cache.Store("client:request-1", 11);
        Assert.True(cache.TryGet("client:request-1", out int replay));
        Assert.Equal(11, replay);

        cache.Store("client:request-2", 20);
        cache.Store("client:request-3", 30);
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("client:request-1", out _));
    }
}
