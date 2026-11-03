using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class NativeInputInvalidationTests
{
    private static IEnumerable<nint> NoGlobalScan() => throw new InvalidOperationException("Unexpected whole-project scan.");

    [Fact]
    public void DrawingAControllerOnlyInvalidatesItsRecordedPartWithoutScanningOtherParts()
    {
        var input = new NativeInputInvalidation();
        input.BeforeControllerRemoval(100, 1);
        input.BeforeControllerRemoval(101, 1);
        input.ControllerRemoved(100, NoGlobalScanLazy());
        input.ControllerRemoved(101, NoGlobalScanLazy());
        Assert.Equal(new nint[] { 1 }, input.DirtyParts);
    }

    private static IEnumerable<nint> NoGlobalScanLazy()
    {
        foreach (var value in NoGlobalScan()) yield return value;
    }

    [Fact]
    public void NativeUndoAndUnobservedRemovalsKeepTheConservativeFallback()
    {
        var input = new NativeInputInvalidation();
        input.ControllerRemoved(999, new nint[] { 1, 2, 3 });
        Assert.True(input.DirtyParts.SetEquals(new nint[] { 1, 2, 3 }));
    }

    [Fact]
    public void FailedRemovalRollbackAndReusedHandlesCannotCarryOwnershipIntoAnotherMutation()
    {
        var input = new NativeInputInvalidation();
        input.BeforeControllerRemoval(100, 1);
        input.FailedControllerRemoval(100);
        input.ControllerRemoved(100, new nint[] { 2 });
        Assert.True(input.DirtyParts.SetEquals(new nint[] { 2 }));
        input.DirtyParts.Clear();
        input.BeforeControllerRemoval(100, 1);
        input.EndMutation();
        input.ControllerRemoved(100, new nint[] { 3 });
        Assert.True(input.DirtyParts.SetEquals(new nint[] { 3 }));
        input.DirtyParts.Clear();
        input.BeforeControllerRemoval(100, 2);
        input.ControllerRemoved(100, NoGlobalScanLazy());
        Assert.True(input.DirtyParts.SetEquals(new nint[] { 2 }));
    }
}
