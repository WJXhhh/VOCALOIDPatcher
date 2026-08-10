using System;
using System.Threading;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID.VAE;

namespace VOCALOIDPatcher.Utils.Audio;

internal enum PlaybackLatencySource
{
    None,
    BufferEstimate,
    DirectSoundCursor,
    DirectSoundSignalValidated,
    AsioBufferEstimate,
    AsioDriverReported
}

internal enum PlaybackLatencyConfidence
{
    None,
    Low,
    Medium,
    High
}

internal readonly record struct PlaybackLatencyStatus(
    double LatencySeconds,
    PlaybackLatencySource Source,
    PlaybackLatencyConfidence Confidence,
    int ObservationCount,
    double JitterSeconds,
    double ValidationCorrelation,
    int BufferFrames,
    int SampleRate,
    bool IsActive);

/// <summary>
/// Estimates how far the audible master output trails VAE's engine position.
/// ASIO uses the driver's reported output latency and validates its callback
/// cadence. DirectSound uses the actual play-cursor-to-lock distance and only
/// promotes signal correlation when it agrees with that independent estimate.
/// </summary>
internal static class PlaybackLatencyCalibrator
{
    private const int AnalysisSamples = 16384;
    private const int ReductionFactor = 4;
    private const double MaximumLatencySeconds = 0.25;
    private const double MinimumCorrelation = 0.55;
    private const double MinimumPeakProminence = 0.035;
    private const double CandidateToleranceSeconds = 0.008;
    private const double ValidationAgreementSeconds = 0.020;

    private static readonly object StatusLock = new();
    private static readonly ManualResetEventSlim StopSignal = new(false);
    private static readonly float[] Source = new float[AnalysisSamples];
    private static readonly float[] Output = new float[AnalysisSamples];
    private static readonly float[] ResampledSource = new float[AnalysisSamples];
    private static readonly float[] ReducedSource = new float[AnalysisSamples / ReductionFactor];
    private static readonly float[] ReducedOutput = new float[AnalysisSamples / ReductionFactor];

    private static WasapiLoopbackCapture? _loopback;
    private static Thread? _worker;
    private static volatile bool _running;
    private static bool _directSoundTapStarted;
    private static VEDeviceType _deviceType;
    private static double _latencySeconds;
    private static double _candidateLatency = double.NaN;
    private static double _candidateJitter;
    private static double _candidateCorrelation;
    private static double _candidateProminence;
    private static int _candidateHits;
    private static long _lastValidationTick;
    private static long _nextSignalValidationTick;
    private static PlaybackLatencyStatus _status;

    public static double LatencySeconds => Volatile.Read(ref _latencySeconds);

    public static PlaybackLatencyStatus GetStatus()
    {
        lock (StatusLock)
            return _status;
    }

    public static void Start(VEDeviceConfig config)
    {
        Stop();

        if (!Settings.AutoCalibratePlayheadLatency)
        {
            Publish(0.0, PlaybackLatencySource.None, PlaybackLatencyConfidence.None,
                0, 0.0, double.NaN, 0, 0, false);
            return;
        }

        _deviceType = config.DeviceType;
        _candidateLatency = double.NaN;
        _candidateJitter = 0.0;
        _candidateCorrelation = 0.0;
        _candidateProminence = 0.0;
        _candidateHits = 0;
        _lastValidationTick = 0;
        _nextSignalValidationTick = 0;

        var fallback = EstimateBufferLatency(config);
        Publish(fallback, PlaybackLatencySource.BufferEstimate, PlaybackLatencyConfidence.Low,
            0, 0.0, double.NaN, (int)config.BufferSample, (int)config.SampleRate, true);

        try
        {
            if (config.DeviceType == VEDeviceType.DS && DirectSoundPcmTap.Installed)
            {
                DirectSoundPcmTap.Start();
                _directSoundTapStarted = true;

                _loopback = new WasapiLoopbackCapture(AnalysisSamples * 2);
                _loopback.Start();
            }

            StopSignal.Reset();
            _running = true;
            _worker = new Thread(CalibrationLoop)
            {
                IsBackground = true,
                Name = "VOCALOIDPatcher.PlaybackLatencyCalibration",
                Priority = ThreadPriority.Lowest
            };
            _worker.Start();
        }
        catch
        {
            StopResources();
            Publish(fallback, PlaybackLatencySource.BufferEstimate, PlaybackLatencyConfidence.Low,
                0, 0.0, double.NaN, (int)config.BufferSample, (int)config.SampleRate, false);
        }
    }

    public static void Stop()
    {
        _running = false;
        StopSignal.Set();

        var worker = _worker;
        _worker = null;
        if (worker != null && worker != Thread.CurrentThread && worker.IsAlive)
            worker.Join(500);

        StopResources();

        lock (StatusLock)
            _status = _status with { IsActive = false };
    }

    private static void StopResources()
    {
        var loopback = _loopback;
        _loopback = null;
        loopback?.Stop();

        if (_directSoundTapStarted)
        {
            _directSoundTapStarted = false;
            DirectSoundPcmTap.Stop();
        }
    }

    private static double EstimateBufferLatency(VEDeviceConfig config)
    {
        if (config.SampleRate == 0 || config.BufferSample == 0)
            return 0.0;

        return Math.Clamp((double)config.BufferSample / config.SampleRate,
            0.0, MaximumLatencySeconds);
    }

    private static void CalibrationLoop()
    {
        if (StopSignal.Wait(250)) return;

        while (_running)
        {
            try
            {
                UpdatePrimaryEstimate();
                var now = Environment.TickCount64;
                if (_deviceType == VEDeviceType.DS &&
                    now >= Volatile.Read(ref _nextSignalValidationTick))
                {
                    TryUpdateSignalValidation();

                    var status = GetStatus();
                    var interval = status.Source == PlaybackLatencySource.DirectSoundSignalValidated &&
                        status.Confidence == PlaybackLatencyConfidence.High
                        ? 10_000
                        : 2_000;
                    Volatile.Write(ref _nextSignalValidationTick, now + interval);
                }
            }
            catch
            {
                // Calibration is optional. Retain the last credible estimate.
            }

            if (StopSignal.Wait(800)) return;
        }
    }

    private static void UpdatePrimaryEstimate()
    {
        if (_deviceType == VEDeviceType.ASIO)
        {
            UpdateAsioEstimate();
            return;
        }

        if (_deviceType != VEDeviceType.DS ||
            !DirectSoundPcmTap.TryGetLatencyInfo(out var info))
            return;

        if (Volatile.Read(ref _lastValidationTick) != 0 &&
            Environment.TickCount64 - Volatile.Read(ref _lastValidationTick) <= 2500)
            return;

        var stable = info.MeasurementCount >= 20 &&
            info.JitterSeconds <= Math.Max(0.0015, info.LatencySeconds * 0.12);
        Publish(info.LatencySeconds, PlaybackLatencySource.DirectSoundCursor,
            stable ? PlaybackLatencyConfidence.Medium : PlaybackLatencyConfidence.Low,
            info.MeasurementCount, info.JitterSeconds, double.NaN,
            info.BufferFrames, info.SampleRate, true);
    }

    private static void UpdateAsioEstimate()
    {
        if (!AsioPcmTap.TryGetLatencyInfo(out var info) || info.SampleRate <= 0)
            return;

        var expectedPeriod = (double)info.BufferFrames / info.SampleRate;
        var periodError = expectedPeriod > 0.0
            ? Math.Abs(info.CallbackPeriodSeconds - expectedPeriod) / expectedPeriod
            : double.PositiveInfinity;
        var cadenceStable = info.CallbackActive && info.CallbackCount >= 16 &&
            periodError <= 0.08 &&
            info.CallbackJitterSeconds <= Math.Max(0.00075, expectedPeriod * 0.08);

        var source = info.DriverReported
            ? PlaybackLatencySource.AsioDriverReported
            : PlaybackLatencySource.AsioBufferEstimate;
        var confidence = info.DriverReported
            ? cadenceStable ? PlaybackLatencyConfidence.High : PlaybackLatencyConfidence.Medium
            : cadenceStable ? PlaybackLatencyConfidence.Medium : PlaybackLatencyConfidence.Low;

        Publish(info.LatencySeconds, source, confidence,
            info.CallbackCount, info.CallbackJitterSeconds, double.NaN,
            info.BufferFrames, info.SampleRate, true);
    }

    private static void TryUpdateSignalValidation()
    {
        var loopback = _loopback;
        if (loopback == null || !loopback.IsRunning || !loopback.ProcessIsolated ||
            Environment.TickCount64 - loopback.LastBufferTick > 250)
            return;

        if (!DirectSoundPcmTap.TryReadLatest(Source, out var sourceRate) || sourceRate <= 0)
            return;

        var outputRate = loopback.SampleRate;
        if (outputRate <= 0) return;

        loopback.ReadLatest(Output);
        var validSamples = ResampleLatest(Source, sourceRate, ResampledSource, outputRate);
        if (validSamples < 2048) return;

        Array.Copy(Output, Output.Length - validSamples, Output, 0, validSamples);
        var reducedCount = ReduceAndDifferentiate(ResampledSource, ReducedSource, validSamples);
        ReduceAndDifferentiate(Output, ReducedOutput, validSamples);

        if (CalculateRms(ReducedSource, reducedCount) < 0.001 ||
            CalculateRms(ReducedOutput, reducedCount) < 0.001)
            return;

        if (!DirectSoundPcmTap.TryGetLatencyInfo(out var cursorInfo) ||
            cursorInfo.MeasurementCount < 8)
            return;

        var reducedRate = (double)outputRate / ReductionFactor;
        var maximumLag = Math.Min((int)(MaximumLatencySeconds * reducedRate),
            reducedCount - 512);
        if (maximumLag <= 0) return;

        var centerLag = (int)Math.Round(cursorInfo.LatencySeconds * reducedRate);
        var searchRadius = Math.Max(4, (int)Math.Round(0.035 * reducedRate));
        var minLag = Math.Max(0, centerLag - searchRadius);
        var maxLag = Math.Min(maximumLag, centerLag + searchRadius);
        if (minLag > maxLag) return;

        var result = FindBestLag(ReducedSource, ReducedOutput, reducedCount,
            minLag, maxLag, reducedRate);
        if (result.Lag < 0 || result.Correlation < MinimumCorrelation ||
            result.Prominence < MinimumPeakProminence)
            return;

        AcceptSignalCandidate(result.Lag / reducedRate, result.Correlation, result.Prominence);
    }

    private static int ResampleLatest(float[] source, int sourceRate,
        float[] destination, int destinationRate)
    {
        var ratio = (double)sourceRate / destinationRate;
        var validSamples = Math.Min(destination.Length,
            (int)((source.Length - 2) / ratio) + 1);
        var start = source.Length - (validSamples - 1) * ratio - 1.0;

        for (var i = 0; i < validSamples; i++)
        {
            var position = start + i * ratio;
            if (position <= 0)
            {
                destination[i] = source[0];
                continue;
            }

            if (position >= source.Length - 1)
            {
                destination[i] = source[^1];
                continue;
            }

            var index = (int)position;
            var fraction = (float)(position - index);
            destination[i] = source[index] + (source[index + 1] - source[index]) * fraction;
        }

        return validSamples;
    }

    private static int ReduceAndDifferentiate(float[] source, float[] destination, int sourceCount)
    {
        var destinationCount = sourceCount / ReductionFactor;
        float previous = 0;
        for (var i = 0; i < destinationCount; i++)
        {
            float average = 0;
            var start = i * ReductionFactor;
            for (var j = 0; j < ReductionFactor; j++)
                average += source[start + j];
            average /= ReductionFactor;

            destination[i] = i == 0 ? 0 : average - previous;
            previous = average;
        }

        return destinationCount;
    }

    private static double CalculateRms(float[] values, int count)
    {
        double sum = 0;
        for (var i = 0; i < count; i++)
            sum += values[i] * values[i];
        return Math.Sqrt(sum / count);
    }

    private static CorrelationResult FindBestLag(float[] source, float[] output,
        int count, int minLag, int maxLag, double reducedRate)
    {
        var bestLag = -1;
        var bestCorrelation = double.NegativeInfinity;

        for (var lag = minLag; lag <= maxLag; lag += 2)
        {
            var correlation = CalculateCorrelation(source, output, count, lag);
            if (correlation <= bestCorrelation) continue;
            bestCorrelation = correlation;
            bestLag = lag;
        }

        var begin = Math.Max(minLag, bestLag - 2);
        var end = Math.Min(maxLag, bestLag + 2);
        for (var lag = begin; lag <= end; lag++)
        {
            var correlation = CalculateCorrelation(source, output, count, lag);
            if (correlation <= bestCorrelation) continue;
            bestCorrelation = correlation;
            bestLag = lag;
        }

        var exclusion = Math.Max(3, (int)(0.004 * reducedRate));
        var secondCorrelation = double.NegativeInfinity;
        for (var lag = minLag; lag <= maxLag; lag += 2)
        {
            if (Math.Abs(lag - bestLag) <= exclusion) continue;
            var correlation = CalculateCorrelation(source, output, count, lag);
            if (correlation > secondCorrelation)
                secondCorrelation = correlation;
        }

        var prominence = double.IsFinite(secondCorrelation)
            ? bestCorrelation - secondCorrelation
            : bestCorrelation;
        return new CorrelationResult(bestLag, bestCorrelation, prominence);
    }

    private static double CalculateCorrelation(float[] source, float[] output, int length, int lag)
    {
        var count = length - lag;
        double product = 0;
        double sourcePower = 0;
        double outputPower = 0;

        for (var i = 0; i < count; i++)
        {
            var sourceValue = source[i];
            var outputValue = output[i + lag];
            product += sourceValue * outputValue;
            sourcePower += sourceValue * sourceValue;
            outputPower += outputValue * outputValue;
        }

        var denominator = Math.Sqrt(sourcePower * outputPower);
        return denominator <= double.Epsilon ? 0.0 : product / denominator;
    }

    private static void AcceptSignalCandidate(double latency, double correlation, double prominence)
    {
        latency = Math.Clamp(latency, 0.0, MaximumLatencySeconds);

        if (double.IsFinite(_candidateLatency) &&
            Math.Abs(latency - _candidateLatency) <= CandidateToleranceSeconds)
        {
            var updated = _candidateLatency * 0.75 + latency * 0.25;
            _candidateJitter = _candidateJitter * 0.75 + Math.Abs(latency - updated) * 0.25;
            _candidateLatency = updated;
            _candidateCorrelation = _candidateCorrelation * 0.75 + correlation * 0.25;
            _candidateProminence = _candidateProminence * 0.75 + prominence * 0.25;
            _candidateHits++;
        }
        else
        {
            _candidateLatency = latency;
            _candidateJitter = 0.0;
            _candidateCorrelation = correlation;
            _candidateProminence = prominence;
            _candidateHits = 1;
        }

        if (_candidateHits < 5 ||
            !DirectSoundPcmTap.TryGetLatencyInfo(out var cursorInfo) ||
            Math.Abs(_candidateLatency - cursorInfo.LatencySeconds) > ValidationAgreementSeconds)
            return;

        var highConfidence = _candidateHits >= 8 && _candidateCorrelation >= 0.70 &&
            _candidateProminence >= 0.06 && _candidateJitter <= 0.004;
        var combinedLatency = _candidateLatency * 0.75 + cursorInfo.LatencySeconds * 0.25;
        Volatile.Write(ref _lastValidationTick, Environment.TickCount64);
        Publish(combinedLatency, PlaybackLatencySource.DirectSoundSignalValidated,
            highConfidence ? PlaybackLatencyConfidence.High : PlaybackLatencyConfidence.Medium,
            _candidateHits, Math.Max(_candidateJitter, cursorInfo.JitterSeconds),
            _candidateCorrelation, cursorInfo.BufferFrames, cursorInfo.SampleRate, true);
    }

    private static void Publish(double latency, PlaybackLatencySource source,
        PlaybackLatencyConfidence confidence, int observationCount, double jitterSeconds,
        double validationCorrelation, int bufferFrames, int sampleRate, bool active)
    {
        latency = double.IsFinite(latency)
            ? Math.Clamp(latency, 0.0, MaximumLatencySeconds)
            : 0.0;
        Volatile.Write(ref _latencySeconds, latency);

        lock (StatusLock)
        {
            _status = new PlaybackLatencyStatus(latency, source, confidence,
                observationCount, jitterSeconds, validationCorrelation,
                bufferFrames, sampleRate, active);
        }
    }

    private readonly record struct CorrelationResult(
        int Lag,
        double Correlation,
        double Prominence);
}
