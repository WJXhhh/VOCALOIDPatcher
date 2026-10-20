using System;

namespace VOCALOIDPatcher.ConsonantTiming.Smoothing;

/// <summary>
/// Detailed diagnostic and outcome result for an audio transition splicing operation.
/// </summary>
public sealed record SpliceResult
{
    /// <summary>
    /// Spliced continuous audio waveform.
    /// </summary>
    public required float[] Output { get; init; }

    /// <summary>
    /// Whether any audio samples were changed.
    /// </summary>
    public required bool Intervened { get; init; }

    /// <summary>
    /// Offset in samples from the nominal junction to the local processing window.
    /// </summary>
    public required int SpliceOffsetSamples { get; init; }

    /// <summary>
    /// Offset in milliseconds from the nominal junction to the selected splice point.
    /// </summary>
    public required double SpliceOffsetMs { get; init; }

    /// <summary>
    /// Duration of the local waveform repair in milliseconds; zero for gain-only processing.
    /// </summary>
    public required double CrossfadeDurationMs { get; init; }

    /// <summary>
    /// Measured local loudness of the left side before smoothing, in dBFS.
    /// </summary>
    public required float LeftMeasuredDb { get; init; }

    /// <summary>
    /// Measured local loudness of the right side before smoothing, in dBFS.
    /// </summary>
    public required float RightMeasuredDb { get; init; }

    /// <summary>
    /// Absolute volume difference across the junction before smoothing, in dB.
    /// </summary>
    public required float VolumeDifferenceDb { get; init; }

    /// <summary>
    /// Whether the repair window was centred on a nearby zero crossing. Samples are never moved.
    /// </summary>
    public required bool PhaseAligned { get; init; }
}

/// <summary>
/// Detailed diagnostic result for in-place junction smoothing on a continuous waveform.
/// </summary>
public sealed record SmoothingResult
{
    /// <summary>
    /// Whether any audio samples were changed.
    /// </summary>
    public required bool Intervened { get; init; }

    /// <summary>
    /// Offset in samples from the nominal junction to the local processing window.
    /// </summary>
    public required int SpliceOffsetSamples { get; init; }

    /// <summary>
    /// Offset in milliseconds from the nominal junction to the selected splice point.
    /// </summary>
    public required double SpliceOffsetMs { get; init; }

    /// <summary>
    /// Duration of the local waveform repair in milliseconds; zero for gain-only processing.
    /// </summary>
    public required double CrossfadeDurationMs { get; init; }

    /// <summary>
    /// Measured local loudness of the left side before smoothing, in dBFS.
    /// </summary>
    public required float LeftMeasuredDb { get; init; }

    /// <summary>
    /// Measured local loudness of the right side before smoothing, in dBFS.
    /// </summary>
    public required float RightMeasuredDb { get; init; }

    /// <summary>
    /// Absolute volume difference across the junction before smoothing, in dB.
    /// </summary>
    public required float VolumeDifferenceDb { get; init; }

    /// <summary>
    /// Whether the repair window was centred on a nearby zero crossing. Samples are never moved.
    /// </summary>
    public required bool PhaseAligned { get; init; }
}

/// <summary>
/// Preserves the sample timeline while bridging local gain changes and repairing
/// isolated junction discontinuities. Audio outside the local windows is unchanged.
/// </summary>
public static class TransitionSmoother
{
    /// <summary>Concatenates segments without trimming or shifting their samples.</summary>
    public static SpliceResult Splice(
        ReadOnlySpan<float> leftSegment,
        ReadOnlySpan<float> rightSegment,
        int sampleRate,
        TransitionSmootherConfig? config = null,
        double? leftNominalGainDb = null,
        double? rightNominalGainDb = null)
    {
        var output = new float[checked(leftSegment.Length + rightSegment.Length)];
        leftSegment.CopyTo(output);
        rightSegment.CopyTo(output.AsSpan(leftSegment.Length));
        var result = SmoothJunction(output, leftSegment.Length, sampleRate, config, leftNominalGainDb, rightNominalGainDb);
        return new SpliceResult
        {
            Output = output, Intervened = result.Intervened,
            SpliceOffsetSamples = result.SpliceOffsetSamples, SpliceOffsetMs = result.SpliceOffsetMs,
            CrossfadeDurationMs = result.CrossfadeDurationMs,
            LeftMeasuredDb = result.LeftMeasuredDb, RightMeasuredDb = result.RightMeasuredDb,
            VolumeDifferenceDb = result.VolumeDifferenceDb, PhaseAligned = result.PhaseAligned
        };
    }

    /// <summary>
    /// Applies a local gain bridge and, only for an outlying sample jump, a
    /// bounded repair whose weight and slope vanish at the window boundaries.
    /// </summary>
    public static SmoothingResult SmoothJunction(
        Span<float> audio,
        int junctionSample,
        int sampleRate,
        TransitionSmootherConfig? config = null,
        double? leftNominalGainDb = null,
        double? rightNominalGainDb = null)
    {
        var cfg = (config ?? TransitionSmootherConfig.Default).Validate();
        SmoothingResult Result(bool changed = false, int offset = 0, double fadeMs = 0,
            float leftDb = -120f, float rightDb = -120f, float difference = 0f, bool aligned = false) => new()
        {
            Intervened = changed, SpliceOffsetSamples = offset,
            SpliceOffsetMs = sampleRate > 0 ? offset * 1000.0 / sampleRate : 0,
            CrossfadeDurationMs = fadeMs, LeftMeasuredDb = leftDb, RightMeasuredDb = rightDb,
            VolumeDifferenceDb = difference, PhaseAligned = aligned
        };
        if (audio.Length < 4 || sampleRate <= 0 || junctionSample <= 0 || junctionSample >= audio.Length ||
            cfg.Mode == TransitionSmoothingMode.Disabled) return Result();
        if ((leftNominalGainDb.HasValue && !double.IsFinite(leftNominalGainDb.Value)) ||
            (rightNominalGainDb.HasValue && !double.IsFinite(rightNominalGainDb.Value))) return Result();

        int length = audio.Length;
        int Samples(double ms) => (int)Math.Min(length, Math.Max(4, Math.Round(sampleRate * ms / 1000.0)));
        int target = (int)Math.Clamp(junctionSample + Math.Round(sampleRate * cfg.TimingOffsetMs / 1000.0), 1, audio.Length - 1);
        int analysis = Samples(cfg.AnalysisRangeMs);
        var left = audio.Slice(Math.Max(0, target - analysis), Math.Min(target, analysis));
        var right = audio.Slice(target, Math.Min(audio.Length - target, analysis));
        float leftDb = AudioAnalysisUtils.EstimateLocalLoudnessDb(left);
        float rightDb = AudioAnalysisUtils.EstimateLocalLoudnessDb(right);
        bool nominal = leftNominalGainDb.HasValue || rightNominalGainDb.HasValue;
        double step = nominal ? (rightNominalGainDb ?? 0) - (leftNominalGainDb ?? 0) : rightDb - leftDb;
        float difference = (float)Math.Abs(step);
        if (!double.IsFinite(step) || (cfg.Mode != TransitionSmoothingMode.Always && difference < cfg.VolumeThresholdDb))
            return Result(leftDb: leftDb, rightDb: rightDb, difference: difference);

        bool leftVoiced = AudioAnalysisUtils.DetectVoicedAndPitchPeriod(left, sampleRate, out int leftPitch, out float leftConfidence);
        bool rightVoiced = AudioAnalysisUtils.DetectVoicedAndPitchPeriod(right, sampleRate, out int rightPitch, out float rightConfidence);
        float confidence = (leftConfidence + rightConfidence) * .5f;
        float transient = Math.Max(AudioAnalysisUtils.MeasureTransientIntensity(audio, target - 16, sampleRate),
            AudioAnalysisUtils.MeasureTransientIntensity(audio, target + 16, sampleRate));
        double fadeMs = cfg.DefaultCrossfadeMs;
        if (transient > .4f) fadeMs *= 1 - transient * .6;
        else if (confidence > .6f) fadeMs *= 1 + confidence * .5;
        int fadeSamples = Samples(Math.Clamp(fadeMs, cfg.MinCrossfadeMs, cfg.MaxCrossfadeMs));

        // Phase selection may centre the repair window near a zero crossing;
        // the gain boundary and discontinuity remain at the requested sample.
        int centre = target;
        bool aligned = false;
        if (cfg.EnablePhaseAlignment && leftVoiced && rightVoiced && confidence >= .45f)
        {
            int radius = Math.Min(Math.Max(leftPitch, rightPitch) / 2, fadeSamples / 4);
            centre = AudioAnalysisUtils.FindZeroCrossing(audio, target, radius);
            aligned = centre != target;
        }
        int start = Math.Max(0, centre - fadeSamples / 2);
        int end = Math.Min(audio.Length, centre + (fadeSamples + 1) / 2);
        bool changed = ApplyLoudnessEnvelope(audio, target, Samples(cfg.LoudnessWindowMs),
            Math.Clamp(step / 2, -cfg.MaxLoudnessAdjustmentDb, cfg.MaxLoudnessAdjustmentDb));
        bool repaired = RepairDiscontinuity(audio, target, start, end);
        changed |= repaired;
        return Result(changed, changed ? (repaired ? centre : target) - junctionSample : 0,
            repaired ? (end - start) * 1000.0 / sampleRate : 0, leftDb, rightDb, difference,
            repaired && aligned);
    }

    private static double SmoothStep(double t) => t * t * (3 - 2 * t);

    private static bool RepairDiscontinuity(Span<float> audio, int junction, int start, int end)
    {
        if (junction - start < 2 || end - junction < 2) return false;
        // Compare with nearby within-side derivatives, not a fixed amplitude
        // threshold. Normal harmonics and noisy consonants must not be blurred.
        double nearby = 0;
        for (int i = Math.Max(start + 1, junction - 8); i < Math.Min(end, junction + 8); i++)
            if (i != junction) nearby = Math.Max(nearby, Math.Abs(audio[i] - audio[i - 1]));
        if (Math.Abs(audio[junction] - audio[junction - 1]) <= 3 * nearby + 2.0 / 32768) return false;

        var original = audio.Slice(start, end - start).ToArray();
        if (Array.Exists(original, s => !float.IsFinite(s))) return false;
        bool changed = false;
        for (int i = 1; i < original.Length - 1; i++)
        {
            int at = start + i;
            double t = at < junction ? i / (double)(junction - 1 - start) :
                (end - 1 - at) / (double)(end - 1 - junction);
            double weight = SmoothStep(t);
            double bridge = original[0] + (original[^1] - (double)original[0]) * i / (original.Length - 1);
            // Convex blend cannot exceed the input window's peak. Read only
            // from the snapshot so the result never feeds into a later sample.
            float value = (float)(original[i] * (1 - weight) + bridge * weight);
            changed |= value != audio[at];
            audio[at] = value;
        }
        return changed;
    }

    private static bool ApplyLoudnessEnvelope(Span<float> audio, int junction, int window, double deltaDb)
    {
        if (Math.Abs(deltaDb) < .001) return false;
        int start = Math.Max(0, junction - window), end = Math.Min(audio.Length, junction + window);
        double Weight(int at) => at < junction
            ? SmoothStep((at - start) / (double)Math.Max(1, junction - start - 1))
            : SmoothStep((end - 1 - at) / (double)Math.Max(1, end - junction - 1));
        // Reduce the entire local bridge if its boosted side lacks headroom.
        // Do not hard-clip individual samples or normalize the part's body.
        double magnitude = Math.Abs(deltaDb);
        for (int at = start; at < end; at++)
        {
            double weight = Weight(at), value = Math.Abs(audio[at]);
            if (!double.IsFinite(value)) return false;
            bool boost = (at < junction) == (deltaDb > 0);
            if (boost && weight > 0 && value > 0)
                magnitude = Math.Min(magnitude, Math.Max(0, -20 * Math.Log10(value)) / weight);
        }
        deltaDb = Math.CopySign(magnitude, deltaDb);
        bool changed = false;
        for (int at = start; at < end; at++)
        {
            double gainDb = Weight(at) * (at < junction ? deltaDb : -deltaDb);
            if (gainDb == 0) continue;
            float value = (float)(audio[at] * Math.Pow(10, gainDb / 20));
            changed |= value != audio[at];
            audio[at] = value;
        }
        return changed;
    }
}
