namespace VOCALOIDPatcher.Mcp.Core;

internal static class SelectionActivation
{
    internal static bool ShouldActivate(bool alreadyActive) => !alreadyActive;

    internal static bool Succeeded(bool requestedPartIsActive) => requestedPartIsActive;
}
