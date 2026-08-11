using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.Utils;

/// <summary>
/// Compatibility facade for existing segmented-pinyin diagnostics. File ownership
/// belongs exclusively to RuntimeObservationLog.
/// </summary>
internal static class ExtendedPinyinDiagnosticLog
{
    public static string LogPath => RuntimeObservationLog.LogPath;

    public static void Write(string stage, string message)
    {
        try
        {
            string safeStage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage;
            string value = message ?? string.Empty;
            RuntimeObservationLog.Write("pinyin.diagnostic", "point", new Dictionary<string, object?>
            {
                ["phase"] = safeStage,
                ["messageId"] = RuntimeObservationLog.HashText(value),
                ["messageLength"] = value.Length,
            });
        }
        catch
        {
            // Diagnostics must never interfere with synthesis fallback.
        }
    }
}
