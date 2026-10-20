using System;

namespace VOCALOIDPatcher.ConsonantTiming.Smoothing;

/// <summary>
/// Controls when and how the transition audio smoother intervenes.
/// </summary>
public enum TransitionSmoothingMode
{
    /// <summary>
    /// Automatically intervenes only when the measured or nominal volume difference
    /// across the junction exceeds the configured threshold.
    /// </summary>
    AutoOnVolumeDifference,

    /// <summary>
    /// Examines the junction regardless of volume difference and repairs it only when needed.
    /// </summary>
    Always,

    /// <summary>
    /// Disables smoothing entirely (bypasses processing).
    /// </summary>
    Disabled
}

/// <summary>
/// Configuration for adaptive transition audio smoothing.
/// Supports customization of smoothing timing, analysis range, micro-crossfade duration,
/// and local loudness envelope bridging.
/// </summary>
public sealed record TransitionSmootherConfig
{
    /// <summary>
    /// Volume difference threshold in decibels (dB) across the junction to trigger automatic intervention.
    /// Default is 2.5 dB (recommended 2.0 ~ 3.0 dB).
    /// </summary>
    public double VolumeThresholdDb { get; init; } = 2.5;

    /// <summary>
    /// Duration of the analysis range on each side of the junction in milliseconds.
    /// Default is 75.0 ms (recommended 50 ~ 100 ms).
    /// </summary>
    public double AnalysisRangeMs { get; init; } = 75.0;

    /// <summary>
    /// Default micro-crossfade length in milliseconds.
    /// Default is 10.0 ms (recommended 8 ~ 15 ms).
    /// </summary>
    public double DefaultCrossfadeMs { get; init; } = 10.0;

    /// <summary>
    /// Minimum adaptive micro-crossfade length in milliseconds (for transients / plosives).
    /// Default is 3.0 ms (recommended 3 ms).
    /// </summary>
    public double MinCrossfadeMs { get; init; } = 3.0;

    /// <summary>
    /// Maximum adaptive micro-crossfade length in milliseconds (for sustained vowels).
    /// Default is 25.0 ms (recommended 25 ms).
    /// </summary>
    public double MaxCrossfadeMs { get; init; } = 25.0;

    /// <summary>
    /// Duration of the local loudness envelope smoothing window in milliseconds.
    /// Default is 60.0 ms (recommended 40 ~ 100 ms).
    /// </summary>
    public double LoudnessWindowMs { get; init; } = 60.0;

    /// <summary>
    /// Maximum gain adjustment in dB applied to either side at the junction.
    /// Prevents excessive boosting or cutting that could alter original accent/intonation.
    /// Default is 12.0 dB.
    /// </summary>
    public double MaxLoudnessAdjustmentDb { get; init; } = 12.0;

    /// <summary>
    /// Whether to centre the local repair window near a zero crossing in reliable voiced sections.
    /// Default is true.
    /// </summary>
    public bool EnablePhaseAlignment { get; init; } = true;

    /// <summary>
    /// Guarantees that audio outside the local loudness window retains 100% of original volume.
    /// Global loudness normalization is prohibited.
    /// Default is true.
    /// </summary>
    public bool PreserveMainBodyGains { get; init; } = true;

    /// <summary>
    /// Customizable timing offset in milliseconds to adjust the exact point where smoothing intervenes.
    /// Default is 0.0 ms.
    /// </summary>
    public double TimingOffsetMs { get; init; } = 0.0;

    /// <summary>
    /// Operational mode governing automatic intervention.
    /// Default is AutoOnVolumeDifference.
    /// </summary>
    public TransitionSmoothingMode Mode { get; init; } = TransitionSmoothingMode.AutoOnVolumeDifference;

    /// <summary>
    /// Standard recommended default configuration.
    /// </summary>
    public static TransitionSmootherConfig Default { get; } = new();

    /// <summary>
    /// Conservative preset prioritizing minimal modification to original audio.
    /// </summary>
    public static TransitionSmootherConfig Conservative { get; } = new()
    {
        VolumeThresholdDb = 3.5,
        AnalysisRangeMs = 60.0,
        DefaultCrossfadeMs = 8.0,
        MinCrossfadeMs = 3.0,
        MaxCrossfadeMs = 15.0,
        LoudnessWindowMs = 50.0,
        MaxLoudnessAdjustmentDb = 9.0,
        EnablePhaseAlignment = true,
        PreserveMainBodyGains = true
    };

    /// <summary>
    /// Responsive preset that smooths smaller volume jumps with wider bridging.
    /// </summary>
    public static TransitionSmootherConfig Responsive { get; } = new()
    {
        VolumeThresholdDb = 1.5,
        AnalysisRangeMs = 90.0,
        DefaultCrossfadeMs = 12.0,
        MinCrossfadeMs = 4.0,
        MaxCrossfadeMs = 25.0,
        LoudnessWindowMs = 80.0,
        MaxLoudnessAdjustmentDb = 15.0,
        EnablePhaseAlignment = true,
        PreserveMainBodyGains = true
    };

    /// <summary>
    /// Validates and returns a clamped copy of this configuration ensuring all fields are finite and within safe ranges.
    /// </summary>
    public TransitionSmootherConfig Validate()
    {
        double volumeThreshold = double.IsFinite(VolumeThresholdDb) ? Math.Clamp(VolumeThresholdDb, 0.1, 30.0) : 2.5;
        double analysisRange = double.IsFinite(AnalysisRangeMs) ? Math.Clamp(AnalysisRangeMs, 20.0, 300.0) : 75.0;
        double minCrossfade = double.IsFinite(MinCrossfadeMs) ? Math.Clamp(MinCrossfadeMs, 1.0, 50.0) : 3.0;
        double maxCrossfade = Math.Clamp(double.IsFinite(MaxCrossfadeMs) ? MaxCrossfadeMs : 25.0, minCrossfade, 100.0);
        double defaultCrossfade = Math.Clamp(double.IsFinite(DefaultCrossfadeMs) ? DefaultCrossfadeMs : 10.0, minCrossfade, maxCrossfade);
        double loudnessWindow = double.IsFinite(LoudnessWindowMs) ? Math.Clamp(LoudnessWindowMs, 20.0, 300.0) : 60.0;
        double maxAdjustment = double.IsFinite(MaxLoudnessAdjustmentDb) ? Math.Clamp(MaxLoudnessAdjustmentDb, 1.0, 30.0) : 12.0;
        double timingOffset = double.IsFinite(TimingOffsetMs) ? Math.Clamp(TimingOffsetMs, -200.0, 200.0) : 0.0;

        return this with
        {
            VolumeThresholdDb = volumeThreshold,
            AnalysisRangeMs = analysisRange,
            MinCrossfadeMs = minCrossfade,
            MaxCrossfadeMs = maxCrossfade,
            DefaultCrossfadeMs = defaultCrossfade,
            LoudnessWindowMs = loudnessWindow,
            MaxLoudnessAdjustmentDb = maxAdjustment,
            TimingOffsetMs = timingOffset
        };
    }
}
