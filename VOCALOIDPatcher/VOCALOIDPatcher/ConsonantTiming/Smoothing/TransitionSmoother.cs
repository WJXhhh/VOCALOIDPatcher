using System;
using System.Collections.Generic;

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
        double measuredStep = rightDb - leftDb;
        double step = nominal ? (rightNominalGainDb ?? 0) - (leftNominalGainDb ?? 0) : measuredStep;
        float difference = (float)Math.Abs(step);
        if (!double.IsFinite(step) || !double.IsFinite(measuredStep))
            return Result(leftDb: leftDb, rightDb: rightDb, difference: difference);

        // OLA and upstream phoneme processing can already have bridged the
        // requested gain. Never impose the nominal step on continuous PCM.
        double gainStep = 0;
        bool bridge = Math.Abs(measuredStep) >= cfg.VolumeThresholdDb &&
            TryGetGainStep(audio, target, sampleRate, measuredStep, cfg.VolumeThresholdDb, out gainStep);
        // A phase seam or impulse can have equal RMS on both sides. Inspect
        // a DSE hop on either side, independently of the loudness threshold.
        int radius = Math.Max(4, (int)Math.Round(sampleRate * 256.0 / 44100));
        var discontinuities = FindDiscontinuities(audio, target, radius);
        if (!bridge && discontinuities.Count == 0)
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
            int phaseRadius = Math.Min(Math.Max(leftPitch, rightPitch) / 2, fadeSamples / 4);
            centre = AudioAnalysisUtils.FindZeroCrossing(audio, target, phaseRadius);
            aligned = centre != target;
        }
        bool changed = bridge && ApplyLoudnessEnvelope(audio, target, Samples(cfg.LoudnessWindowMs),
            gainStep, cfg.MaxLoudnessAdjustmentDb);
        // Recheck after gain correction: a genuine gain step may now be gone,
        // while a phase discontinuity must still be repaired.
        discontinuities = FindDiscontinuities(audio, target, radius);
        bool repaired = false;
        int repairStart = audio.Length, repairEnd = 0;
        foreach (int seam in discontinuities)
        {
            if (!IsDiscontinuity(audio, seam)) continue;
            int repairCentre = seam == target ? centre : seam;
            int start = Math.Max(0, repairCentre - fadeSamples / 2);
            int end = Math.Min(audio.Length, repairCentre + (fadeSamples + 1) / 2);
            if (!RepairDiscontinuity(audio, seam, start, end)) continue;
            repaired = true;
            repairStart = Math.Min(repairStart, start);
            repairEnd = Math.Max(repairEnd, end);
        }
        changed |= repaired;
        int repairMiddle = repaired ? (repairStart + repairEnd) / 2 : target;
        return Result(changed, changed ? repairMiddle - junctionSample : 0,
            repaired ? (repairEnd - repairStart) * 1000.0 / sampleRate : 0, leftDb, rightDb, difference,
            repaired && aligned && discontinuities.Contains(target));
    }

    private static double SmoothStep(double t) => t * t * (3 - 2 * t);

    private static bool TryGetGainStep(ReadOnlySpan<float> audio, int at, int sampleRate, double measuredDb, double thresholdDb, out double stepDb)
    {
        stepDb = 0;
        if (at < 2 || at + 1 >= audio.Length) return false;
        // Extrapolate both sides to the same half-sample seam. A gradual
        // attack can have very different window RMS without any local step.
        double left = 1.5 * audio[at - 1] - .5 * audio[at - 2];
        double right = 1.5 * audio[at] - .5 * audio[at + 1];
        double slopeLeft = (double)audio[at - 1] - audio[at - 2];
        double slopeRight = (double)audio[at + 1] - audio[at];
        if (Math.Min(Math.Abs(left), Math.Abs(right)) < .1 * Math.Max(Math.Abs(slopeLeft), Math.Abs(slopeRight)))
        {
            left = slopeLeft;
            right = slopeRight;
        }
        if (left * right <= 0 || Math.Abs(left) < 1e-8 || Math.Abs(right) < 1e-8) return false;
        double localDb = 20 * Math.Log10(Math.Abs(right / left));
        if (Math.Abs(localDb) < thresholdDb || Math.Abs(localDb - measuredDb) > 1.5) return false;
        // Four samples near a quiet zero crossing can suggest a large ratio
        // even on a continuous harmonic decay. Confirm that the immediate
        // neighbourhood carries the same gain change; the broad analysis
        // window alone also measures the vowel's natural release envelope.
        int confirmation = Math.Min(Math.Max(4, sampleRate / 100), Math.Min(at, audio.Length - at));
        if (confirmation < 4) return false;
        double leftRms = AudioAnalysisUtils.ComputeRms(audio.Slice(at - confirmation, confirmation));
        double rightRms = AudioAnalysisUtils.ComputeRms(audio.Slice(at, confirmation));
        if (leftRms <= 1e-6 || rightRms <= 1e-6) return false;
        double immediateDb = 20 * Math.Log10(rightRms / leftRms);
        if (Math.Abs(immediateDb - localDb) > 1.5) return false;
        stepDb = localDb;
        return true;
    }

    private static bool IsDiscontinuity(ReadOnlySpan<float> audio, int at)
    {
        if (at <= 0 || at >= audio.Length) return false;
        double nearby = 0;
        // Exclude both edges of a 1-3 sample impulse. Including its return
        // edge in the reference made such clicks impossible to detect.
        for (int i = Math.Max(1, at - 32); i < Math.Min(audio.Length, at + 33); i++)
        {
            if (Math.Abs(i - at) <= 4) continue;
            double delta = Math.Abs((double)audio[i] - audio[i - 1]);
            if (!double.IsFinite(delta)) return false;
            nearby = Math.Max(nearby, delta);
        }
        double jump = Math.Abs((double)audio[at] - audio[at - 1]);
        return double.IsFinite(jump) && jump > 3 * nearby + 2.0 / 32768;
    }

    private static List<int> FindDiscontinuities(ReadOnlySpan<float> audio, int target, int radius)
    {
        var result = new List<int>();
        for (int at = Math.Max(1, target - radius); at <= Math.Min(audio.Length - 1, target + radius); at++)
            if (IsDiscontinuity(audio, at)) result.Add(at);
        return result;
    }

    private static bool RepairDiscontinuity(Span<float> audio, int junction, int start, int end)
    {
        if (junction - start < 2 || end - junction < 2) return false;

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

    private static bool ApplyLoudnessEnvelope(Span<float> audio, int junction, int window, double stepDb, double maximumDb)
    {
        if (Math.Abs(stepDb) < .001) return false;
        int start = Math.Max(0, junction - window), end = Math.Min(audio.Length, junction + window);
        double Weight(int at) => at < junction
            ? SmoothStep((at - start) / (double)Math.Max(1, junction - start - 1))
            : SmoothStep((end - 1 - at) / (double)Math.Max(1, end - junction - 1));
        // Allocate unavailable boost to the louder side's attenuation. A
        // single peak must not disable the cut and leave the gain step intact.
        double boostDb = Math.Min(Math.Abs(stepDb) / 2, maximumDb);
        for (int at = start; at < end; at++)
        {
            double weight = Weight(at), value = Math.Abs(audio[at]);
            if (!double.IsFinite(value)) return false;
            bool boost = (at < junction) == (stepDb > 0);
            if (boost && weight > 0 && value > 0)
                boostDb = Math.Min(boostDb, Math.Max(0, -20 * Math.Log10(value)) / weight);
        }
        double cutDb = Math.Min(Math.Abs(stepDb) - boostDb, maximumDb);
        bool changed = false;
        for (int at = start; at < end; at++)
        {
            bool boost = (at < junction) == (stepDb > 0);
            double gainDb = Weight(at) * (boost ? boostDb : -cutDb);
            if (gainDb == 0) continue;
            float value = (float)(audio[at] * Math.Pow(10, gainDb / 20));
            changed |= value != audio[at];
            audio[at] = value;
        }
        return changed;
    }
}
