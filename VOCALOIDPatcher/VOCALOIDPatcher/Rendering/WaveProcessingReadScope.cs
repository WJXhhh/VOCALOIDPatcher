using System;

namespace VOCALOIDPatcher.Rendering;

internal enum WaveProcessingStage
{
    PhonemeVolume,
    TransitionSmoothing,
    AceAcoustic
}

/// <summary>
/// Reading a processor's input must stop before that processor and every
/// downstream stage. Bypassing only the reader feeds later output back upstream.
/// The scope covers a synchronous WaveFilePath getter, never background work.
/// </summary>
internal static class WaveProcessingReadScope
{
    [ThreadStatic] private static WaveProcessingStage? _stopBefore;

    internal static bool ShouldApply(WaveProcessingStage stage)
        => _stopBefore == null || stage < _stopBefore.Value;

    internal static string ReadBefore(WaveProcessingStage stage, Func<string> read)
    {
        var previous = _stopBefore;
        _stopBefore = previous is { } limit && limit < stage ? limit : stage;
        try { return read(); }
        finally { _stopBefore = previous; }
    }
}
