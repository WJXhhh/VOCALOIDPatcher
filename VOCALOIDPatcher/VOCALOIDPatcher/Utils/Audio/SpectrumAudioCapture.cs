using System;

namespace VOCALOIDPatcher.Utils.Audio;

/// <summary>
/// Prefers VAE's pre-Windows DirectSound or ASIO PCM and retains WASAPI
/// loopback as a compatibility fallback for unsupported output paths.
/// </summary>
public sealed class SpectrumAudioCapture : IDisposable
{
    private readonly WasapiLoopbackCapture _fallback = new();
    private volatile bool _running;
    private int _sampleRate = 48000;

    public int SampleRate => _sampleRate;

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;

        _running = true;
        DirectSoundPcmTap.Start();
        AsioPcmTap.Start();
        if (!DirectSoundPcmTap.HasOutputBuffer && !AsioPcmTap.HasOutputBuffer)
            _fallback.Start();
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;
        DirectSoundPcmTap.Stop();
        AsioPcmTap.Stop();
        if (_fallback.IsRunning)
            _fallback.Stop();
    }

    public void ReadLatest(float[] destination)
    {
        if (!_running)
        {
            Array.Clear(destination, 0, destination.Length);
            return;
        }

        if (DirectSoundPcmTap.TryReadLatest(destination, out var sampleRate))
        {
            _sampleRate = sampleRate;
            return;
        }

        if (AsioPcmTap.TryReadLatest(destination, out sampleRate))
        {
            _sampleRate = sampleRate;
            return;
        }

        if (!_fallback.IsRunning)
            _fallback.Start();

        _fallback.ReadLatest(destination);
        _sampleRate = _fallback.SampleRate;
    }

    public void Dispose() => Stop();
}
