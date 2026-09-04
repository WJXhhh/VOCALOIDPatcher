using System;
using System.IO;

namespace VOCALOIDPatcher.Evec;

/// <summary>
/// Small bounded mutation log for host-only EVEC failures. It deliberately
/// records no lyrics, phoneme text, project paths, or voicebank paths.
/// </summary>
internal static class EvecDiagnosticLog
{
    private static readonly object Sync = new();
    private const long MaximumBytes = 512 * 1024;

    internal static string LogPath { get; } = Path.Combine(
        Patcher.ConfigDir,
        "evec-diagnostic.log");

    internal static void Record(
        EvecNoteState before,
        EvecNoteState requested,
        EvecNoteState applied,
        bool success,
        string beforePhonemes,
        string afterPhonemes)
    {
        try
        {
            string line =
                $"{DateTimeOffset.Now:O} success={success} " +
                $"before={Format(before)} requested={Format(requested)} applied={Format(applied)} " +
                $"tokens={CountTokens(beforePhonemes)}->{CountTokens(afterPhonemes)}" +
                Environment.NewLine;

            lock (Sync)
            {
                Directory.CreateDirectory(Patcher.ConfigDir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumBytes)
                    File.Move(LogPath, LogPath + ".previous", true);
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Diagnostics must never affect the editor mutation path.
        }
    }

    private static string Format(EvecNoteState state) =>
        $"{state.VoiceColorId}/{state.AttackId}/{state.ReleaseId}/{state.ConsonantExtension}";

    private static int CountTokens(string phonemes) =>
        string.IsNullOrWhiteSpace(phonemes)
            ? 0
            : phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
