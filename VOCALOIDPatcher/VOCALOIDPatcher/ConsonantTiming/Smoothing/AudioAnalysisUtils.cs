using System;

namespace VOCALOIDPatcher.ConsonantTiming.Smoothing;

/// <summary>
/// Digital Signal Processing utilities for acoustic analysis:
/// local loudness/RMS, voicedness and pitch period estimation,
/// transient detection, and zero-crossing phase alignment.
/// </summary>
internal static class AudioAnalysisUtils
{
    private const float MinRmsFloor = 1e-6f; // -120 dBFS floor

    /// <summary>
    /// Computes the Root Mean Square (RMS) of a span of audio samples.
    /// </summary>
    internal static float ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0f;
        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            sum += s * s;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// Converts a linear RMS amplitude into decibels (dBFS).
    /// </summary>
    internal static float ToDecibels(float rms, float floorDb = -120f)
    {
        if (rms <= MinRmsFloor) return floorDb;
        float db = 20f * (float)Math.Log10(rms);
        return Math.Max(db, floorDb);
    }

    /// <summary>
    /// Estimates local loudness in dB over a specific window of samples.
    /// </summary>
    internal static float EstimateLocalLoudnessDb(ReadOnlySpan<float> samples)
    {
        float rms = ComputeRms(samples);
        return ToDecibels(rms);
    }

    /// <summary>
    /// Detects if an audio segment is voiced (harmonic) and estimates its fundamental pitch period in samples
    /// using normalized autocorrelation over the human vocal frequency range (70 Hz to 600 Hz).
    /// </summary>
    /// <param name="samples">Audio samples to analyze.</param>
    /// <param name="sampleRate">Sample rate in Hz (e.g. 44100).</param>
    /// <param name="pitchPeriodSamples">Estimated fundamental pitch period in samples.</param>
    /// <param name="voicedConfidence">Confidence value between 0.0 (unvoiced/noise) and 1.0 (strongly periodic vowel).</param>
    /// <returns>True if the signal is reliably voiced.</returns>
    internal static bool DetectVoicedAndPitchPeriod(
        ReadOnlySpan<float> samples,
        int sampleRate,
        out int pitchPeriodSamples,
        out float voicedConfidence)
    {
        pitchPeriodSamples = 0;
        voicedConfidence = 0f;

        if (samples.Length < 128 || sampleRate <= 0) return false;

        // Human vocal pitch range: 70 Hz to 600 Hz
        int minLag = Math.Max(2, (int)Math.Floor(sampleRate / 600.0));
        int maxLag = Math.Min(samples.Length / 2, (int)Math.Ceiling(sampleRate / 70.0));

        if (maxLag <= minLag || samples.Length <= maxLag) return false;

        int correlationLength = Math.Min(samples.Length - maxLag, 2048);
        if (correlationLength < minLag) return false;

        // Zero-lag energy
        double energy0 = 0.0;
        for (int i = 0; i < correlationLength; i++)
        {
            float s = samples[i];
            energy0 += s * s;
        }

        if (energy0 < 1e-8) return false; // Silence

        double bestCorrelation = 0.0;
        int bestLag = 0;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double dotProduct = 0.0;
            double energyLag = 0.0;

            for (int i = 0; i < correlationLength; i++)
            {
                float s0 = samples[i];
                float s1 = samples[i + lag];
                dotProduct += s0 * s1;
                energyLag += s1 * s1;
            }

            double denom = Math.Sqrt(energy0 * energyLag);
            if (denom > 1e-12)
            {
                double normalized = dotProduct / denom;
                if (normalized > bestCorrelation)
                {
                    bestCorrelation = normalized;
                    bestLag = lag;
                }
            }
        }

        pitchPeriodSamples = bestLag;
        voicedConfidence = (float)Math.Clamp((bestCorrelation - 0.3) / 0.5, 0.0, 1.0);

        // Reliable periodicity threshold for vocal segments
        return bestCorrelation >= 0.48 && bestLag > 0;
    }

    /// <summary>
    /// Measures transient intensity around a target position using short-term energy derivatives.
    /// Plosives (/p/, /t/, /k/, /b/, /d/, /g/) and sharp attack bursts yield high values (> 0.5),
    /// while sustained vowels or steady unvoiced fricatives yield low values.
    /// </summary>
    internal static float MeasureTransientIntensity(
        ReadOnlySpan<float> samples,
        int targetSample,
        int sampleRate,
        int windowMs = 25)
    {
        if (samples.Length < 32 || sampleRate <= 0) return 0f;

        int windowSamples = Math.Max(16, (int)(sampleRate * windowMs / 1000.0));
        int start = Math.Clamp(targetSample - windowSamples / 2, 0, samples.Length - 1);
        int end = Math.Clamp(targetSample + windowSamples / 2, start + 1, samples.Length);
        int len = end - start;

        if (len < 16) return 0f;

        // Divide into small sub-frames (approx 2 ms each)
        int subFrameSize = Math.Max(4, sampleRate * 2 / 1000);
        int numSubFrames = len / subFrameSize;
        if (numSubFrames < 2) return 0f;

        double maxRise = 0.0;
        double prevEnergy = 0.0;
        double maxEnergy = 0.0;

        for (int f = 0; f < numSubFrames; f++)
        {
            double subEnergy = 0.0;
            int offset = start + f * subFrameSize;
            for (int i = 0; i < subFrameSize; i++)
            {
                float s = samples[offset + i];
                subEnergy += s * s;
            }
            subEnergy /= subFrameSize;

            if (subEnergy > maxEnergy) maxEnergy = subEnergy;

            if (f > 0 && subEnergy > prevEnergy)
            {
                double rise = subEnergy - prevEnergy;
                if (rise > maxRise) maxRise = rise;
            }
            prevEnergy = subEnergy;
        }

        if (maxEnergy < 1e-8) return 0f; // Silence

        // Relative transient ratio: sharp energy rise relative to local peak energy
        float relativeRise = (float)Math.Clamp(maxRise / maxEnergy, 0.0, 1.0);
        return relativeRise;
    }

    /// <summary>
    /// Finds the nearest zero-crossing point around a given target sample index.
    /// </summary>
    internal static int FindZeroCrossing(
        ReadOnlySpan<float> samples,
        int targetIndex,
        int searchRadius,
        bool preferPositiveSlope = true)
    {
        if (samples.Length < 2) return targetIndex;

        int bestIndex = targetIndex;
        int minDistance = int.MaxValue;

        int start = Math.Max(0, targetIndex - searchRadius);
        int end = Math.Min(samples.Length - 2, targetIndex + searchRadius);

        for (int i = start; i <= end; i++)
        {
            float current = samples[i];
            float next = samples[i + 1];

            // Zero crossing detection
            bool isZeroCrossing = (current <= 0f && next > 0f) || (current >= 0f && next < 0f);
            if (isZeroCrossing)
            {
                bool slopeMatch = preferPositiveSlope ? (current <= 0f && next > 0f) : (current >= 0f && next < 0f);
                int dist = Math.Abs(i - targetIndex);

                // Prefer slope-matching zero crossing
                int effectiveDist = slopeMatch ? dist : dist + searchRadius / 2;
                if (effectiveDist < minDistance)
                {
                    minDistance = effectiveDist;
                    bestIndex = Math.Abs(current) < Math.Abs(next) ? i : i + 1;
                }
            }
        }

        return bestIndex;
    }
}
