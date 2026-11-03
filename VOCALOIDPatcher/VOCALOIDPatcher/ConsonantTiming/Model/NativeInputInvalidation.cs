using System.Collections.Generic;

namespace VOCALOIDPatcher.ConsonantTiming;

// The removed native wrapper must never be dereferenced to recover its owner.
// Capture that identity at the managed removal boundary; retain a conservative
// whole-sequence fallback for native Undo or other unobserved removal routes.
internal sealed class NativeInputInvalidation
{
    private readonly Dictionary<nint, nint> _removedControllerOwners = new();
    internal HashSet<nint> DirtyParts { get; } = new();

    internal void BeforeControllerRemoval(nint controller, nint part)
    {
        if (controller != 0 && part != 0) _removedControllerOwners[controller] = part;
    }

    internal void FailedControllerRemoval(nint controller) => _removedControllerOwners.Remove(controller);

    internal void ControllerRemoved(nint controller, IEnumerable<nint> fallbackParts)
    {
        if (_removedControllerOwners.TryGetValue(controller, out nint part)) DirtyParts.Add(part);
        else DirtyParts.UnionWith(fallbackParts);
    }

    internal void EndMutation() => _removedControllerOwners.Clear();
}
