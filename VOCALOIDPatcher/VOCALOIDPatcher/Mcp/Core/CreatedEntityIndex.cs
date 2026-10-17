using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.Mcp.Core;

internal static class CreatedEntityIndex
{
    // VSM recreates managed wrappers on enumeration; only native handles are stable.
    public static int Find(IEnumerable<IntPtr> current, HashSet<IntPtr> before)
    {
        int found = -1;
        int index = 0;
        foreach (IntPtr handle in current)
        {
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("An entity has no native identity.");
            if (!before.Contains(handle))
            {
                if (found >= 0)
                    throw new InvalidOperationException("The created entity is ambiguous.");
                found = index;
            }
            index++;
        }
        if (found < 0)
            throw new InvalidOperationException("The created entity could not be identified.");
        return found;
    }
}
