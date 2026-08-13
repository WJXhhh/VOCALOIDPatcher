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
    [InlineData(@"C:\project.wav:payload")]
    public void RejectsUncAndDevicePaths(string path)
    {
        var allowlist = new PathAllowlist(new[] { _root });
        Assert.False(allowlist.TryResolve(path, out _, out _));
    }

    [Fact]
    public void RejectsEscapeThroughIntermediateSymbolicLink()
    {
        string outside = Path.Combine(Path.GetTempPath(), "v6patch-mcp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string link = Path.Combine(_root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Directory.Delete(outside, true);
            return;
        }

        try
        {
            var allowlist = new PathAllowlist(new[] { _root });
            Assert.False(allowlist.TryResolve(Path.Combine(link, "audio.wav"), out _, out string? reason));
            Assert.Contains("escapes", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
