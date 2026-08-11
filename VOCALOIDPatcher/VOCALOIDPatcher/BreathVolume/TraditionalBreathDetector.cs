using System;
using System.Collections.Generic;
using System.Linq;

namespace VOCALOIDPatcher.BreathVolume;

internal readonly record struct TraditionalBreathRange(long BeginSample, long EndSample);

internal readonly record struct TraditionalBreathDetectionResult(
    IReadOnlyList<TraditionalBreathRange> Ranges,
    int PitchedOnsets,
    int EvaluatedGaps,
    int ActivityCandidates,
    int RejectedShortActivity,
    int RejectedShortLead,
    int RejectedPreviousTail,
    int ActiveFrames,
    float MaxUnpitchedRms,
    float MaxUnpitchedPeak);

internal static class TraditionalBreathDetector
{
    private const double MaximumLookbackMilliseconds = 2500.0;
    private const double MinimumGapMilliseconds = 320.0;
    private const double MinimumLeadMilliseconds = 220.0;
    private const double MinimumActiveMilliseconds = 90.0;
    private const double MaximumInternalSilenceMilliseconds = 120.0;
    private const double MinimumPreviousPitchSeparationMilliseconds = 80.0;
    private const float MinimumRms = 0.00001f;
    private const float MinimumPeak = 0.00008f;

    public static float NormalizeThumbnailPeak(short minimum, short maximum)
        => Math.Max(Math.Abs((int)minimum), Math.Abs((int)maximum)) / 32768f;

    public static bool[] BuildPitchedFrames(
        int frameCount,
        long samplesPerFrame,
        IEnumerable<TraditionalBreathRange> noteRanges)
    {
        var frames = new bool[Math.Max(0, frameCount)];
        if (frames.Length == 0 || samplesPerFrame <= 0 || noteRanges == null)
            return frames;

        foreach (var range in noteRanges)
        {
            if (range.EndSample <= range.BeginSample)
                continue;
            var beginFrame = Math.Clamp(range.BeginSample / samplesPerFrame, 0, frames.LongLength);
            var endFrame = Math.Clamp(
                1 + (range.EndSample - 1) / samplesPerFrame,
                0,
                frames.LongLength);
            for (var frame = beginFrame; frame < endFrame; frame++)
                frames[checked((int)frame)] = true;
        }
        return frames;
    }

    public static TraditionalBreathDetectionResult Detect(
        IReadOnlyList<float> frameRms,
        IReadOnlyList<float> framePeaks,
        IReadOnlyList<bool> pitchedFrames,
        long samplesPerFrame,
        int sampleRate)
    {
        if (frameRms == null || framePeaks == null || pitchedFrames == null ||
            samplesPerFrame <= 0 || sampleRate <= 0)
            return new TraditionalBreathDetectionResult(
                Array.Empty<TraditionalBreathRange>(), 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var frameCount = Math.Min(frameRms.Count, Math.Min(framePeaks.Count, pitchedFrames.Count));
        if (frameCount == 0)
            return new TraditionalBreathDetectionResult(
                Array.Empty<TraditionalBreathRange>(), 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var maximumLookback = FramesForMilliseconds(MaximumLookbackMilliseconds, samplesPerFrame, sampleRate);
        var minimumGap = FramesForMilliseconds(MinimumGapMilliseconds, samplesPerFrame, sampleRate);
        var minimumLead = FramesForMilliseconds(MinimumLeadMilliseconds, samplesPerFrame, sampleRate);
        var minimumActive = FramesForMilliseconds(MinimumActiveMilliseconds, samplesPerFrame, sampleRate);
        var maximumInternalSilence = FramesForMilliseconds(
            MaximumInternalSilenceMilliseconds, samplesPerFrame, sampleRate);
        var minimumPreviousPitchSeparation = FramesForMilliseconds(
            MinimumPreviousPitchSeparationMilliseconds, samplesPerFrame, sampleRate);

        var ranges = new List<TraditionalBreathRange>();
        var pitchedOnsets = 0;
        var evaluatedGaps = 0;
        var activityCandidates = 0;
        var rejectedShortActivity = 0;
        var rejectedShortLead = 0;
        var rejectedPreviousTail = 0;
        var totalActiveFrames = 0;
        var maximumUnpitchedRms = 0f;
        var maximumUnpitchedPeak = 0f;

        for (var onset = 0; onset < frameCount; onset++)
        {
            if (!pitchedFrames[onset] || onset > 0 && pitchedFrames[onset - 1])
                continue;
            pitchedOnsets++;

            var gapStart = onset;
            var lowerBound = Math.Max(0, onset - maximumLookback);
            while (gapStart > lowerBound && !pitchedFrames[gapStart - 1])
                gapStart--;
            if (onset - gapStart < minimumGap)
                continue;
            evaluatedGaps++;

            var cursor = onset - 1;
            while (cursor >= gapStart)
            {
                while (cursor >= gapStart && !IsActive(frameRms[cursor], framePeaks[cursor]))
                {
                    maximumUnpitchedRms = Math.Max(maximumUnpitchedRms, frameRms[cursor]);
                    maximumUnpitchedPeak = Math.Max(maximumUnpitchedPeak, framePeaks[cursor]);
                    cursor--;
                }
                if (cursor < gapStart)
                    break;

                var lastActive = cursor;
                var firstActive = lastActive;
                var activeFrames = 0;
                var inactiveRun = 0;
                for (; cursor >= gapStart; cursor--)
                {
                    maximumUnpitchedRms = Math.Max(maximumUnpitchedRms, frameRms[cursor]);
                    maximumUnpitchedPeak = Math.Max(maximumUnpitchedPeak, framePeaks[cursor]);
                    if (IsActive(frameRms[cursor], framePeaks[cursor]))
                    {
                        firstActive = cursor;
                        activeFrames++;
                        inactiveRun = 0;
                        continue;
                    }

                    inactiveRun++;
                    if (inactiveRun > maximumInternalSilence)
                        break;
                }
                activityCandidates++;

                if (activeFrames < minimumActive)
                {
                    rejectedShortActivity++;
                    continue;
                }
                if (onset - firstActive < minimumLead)
                {
                    rejectedShortLead++;
                    continue;
                }
                if (gapStart > 0 && firstActive - gapStart < minimumPreviousPitchSeparation)
                {
                    rejectedPreviousTail++;
                    continue;
                }

                totalActiveFrames += activeFrames;
                ranges.Add(new TraditionalBreathRange(
                    checked(firstActive * samplesPerFrame),
                    checked((lastActive + 1L) * samplesPerFrame)));
                break;
            }
        }

        return new TraditionalBreathDetectionResult(
            Merge(ranges), pitchedOnsets, evaluatedGaps, activityCandidates,
            rejectedShortActivity, rejectedShortLead, rejectedPreviousTail, totalActiveFrames,
            maximumUnpitchedRms, maximumUnpitchedPeak);
    }

    private static bool IsActive(float rms, float peak)
        => float.IsFinite(rms) && float.IsFinite(peak) &&
           (rms >= MinimumRms || peak >= MinimumPeak);

    private static int FramesForMilliseconds(double milliseconds, long samplesPerFrame, int sampleRate)
        => Math.Max(1, (int)Math.Ceiling(milliseconds * sampleRate / 1000.0 / samplesPerFrame));

    private static IReadOnlyList<TraditionalBreathRange> Merge(
        IEnumerable<TraditionalBreathRange> ranges)
    {
        var ordered = ranges
            .Where(range => range.EndSample > range.BeginSample)
            .OrderBy(range => range.BeginSample)
            .ThenBy(range => range.EndSample)
            .ToArray();
        if (ordered.Length < 2)
            return ordered;

        var result = new List<TraditionalBreathRange> { ordered[0] };
        foreach (var range in ordered.Skip(1))
        {
            var previous = result[^1];
            if (range.BeginSample > previous.EndSample)
            {
                result.Add(range);
                continue;
            }
            result[^1] = new TraditionalBreathRange(
                previous.BeginSample, Math.Max(previous.EndSample, range.EndSample));
        }
        return result;
    }
}
