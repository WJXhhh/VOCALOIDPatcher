using VOCALOIDPatcher.Mcp.Core;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public class CreatedEntityIndexTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InsertionAtAnyPositionFindsOnlyNewHandle(int index)
    {
        var before = new HashSet<IntPtr> { new(10), new(20) };
        var current = new List<IntPtr> { new(20), new(10) };
        current.Insert(index, new IntPtr(30));
        Assert.Equal(index, CreatedEntityIndex.Find(current, before));
    }

    [Fact]
    public void MissingAmbiguousOrNullIdentityFailsInsteadOfBindingExistingEntity()
    {
        var before = new HashSet<IntPtr> { new(10) };
        Assert.Throws<InvalidOperationException>(() => CreatedEntityIndex.Find([new(10)], before));
        Assert.Throws<InvalidOperationException>(() => CreatedEntityIndex.Find([new(10), new(20), new(30)], before));
        Assert.Throws<InvalidOperationException>(() => CreatedEntityIndex.Find([IntPtr.Zero], before));
    }
}
