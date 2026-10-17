using System;

namespace VOCALOIDPatcher.Evec;

internal static class BoundedRenderRetry
{
    public static bool Run(Func<bool> canceled, Func<int, bool> attempt, Func<bool> wait,
        Func<bool>? prepareRetry = null)
    {
        for (int index = 0; index < 2; index++)
        {
            if (canceled()) return false;
            if (index > 0 && prepareRetry != null && (!prepareRetry() || canceled())) return false;
            if (attempt(index)) return !canceled();
            if (index == 1 || canceled() || !wait()) return false;
        }
        return false;
    }
}
