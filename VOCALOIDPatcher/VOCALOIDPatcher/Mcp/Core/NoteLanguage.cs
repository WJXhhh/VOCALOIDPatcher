using System;

namespace VOCALOIDPatcher.Mcp.Core;

internal static class NoteLanguage
{
    public static int Resolve(int? requested, int partLanguage, Func<int> nativeLanguage, int fallback)
    {
        // Explicit values are preserved so the caller can reject invalid requests.
        if (requested.HasValue) return requested.Value;
        if (partLanguage >= 0) return partLanguage;
        int native = nativeLanguage();
        return native >= 0 ? native : fallback;
    }
}
