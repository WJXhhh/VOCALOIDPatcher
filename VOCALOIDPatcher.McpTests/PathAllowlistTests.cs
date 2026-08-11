using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class PathAllowlistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "v6patch-mcp-test-" + Guid.NewGuid().ToString("N"));

    public PathAllowlistTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void AllowsExistingAndNewFilesUnderRoot()
    {
        var allowlist = new PathAllowlist(new[] { _root });

        Assert.True(allowlist.TryResolve(Path.Combine(_root, "existing.vpr"), out string resolved, out _));
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "existing.vpr")), resolved);
    }

    [Fact]
    public void RejectsTraversalOutsideRoot()
    {
        var allowlist = new PathAllowlist(new[] { _root });
        string outside = Path.Combine(_root, "..", "outside.vpr");

        Assert.False(allowlist.TryResolve(outside, out _, out string? reason));
        Assert.Contains("outside", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\server\share\project.vpr")]
    [InlineData(@"\\?\C:\project.vpr")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void RejectsUncAndDevicePaths(string path)
    {
        var allowlist = new PathAllowlist(new[] { _root });
        Assert.False(allowlist.TryResolve(path, out _, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
