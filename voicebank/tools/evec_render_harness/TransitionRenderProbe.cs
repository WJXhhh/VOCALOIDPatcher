using System.Globalization;
using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.Evec;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private sealed record TransitionScenario(
        string Name, int Tempo, int DurationTicks, double? NextGapMs = null,
        int NextPitch = 60, bool TempoChange = false, string Phonemes = "k a", int Velocity = 64);

    private static int ProbeTransitions(WIVSMSequenceManager manager, DatabaseManager database,
        string outputDirectory, string bankName, bool render, bool asyncScore = false)
    {
        RenderCase? bank = Cases.FirstOrDefault(item => item.Name == bankName + "_normal");
        if (bank == null)
        {
            Console.WriteLine("transition.error=Choose miku, rin or len as the bank argument.");
            return 50;
        }
        bool available = false;
        for (ulong index = 0; index < database.NumVoiceBanks; index++)
            available |= database.GetVoiceBankByIndex(index)?.CompID == bank.ComponentId;
        Console.WriteLine($"transition.voicebank_available={available}");
        if (render && !available)
        {
            Console.WriteLine("transition.error=VoiceBankNotFound; no acoustic comparison was performed.");
            return 51;
        }

        TransitionScenario[] scenarios =
        [
            new("isolated", 12000, 960),
            new("short_80ms", 12000, 77),
            new("short_125ms", 12000, 120),
            new("budget_150ms", 12000, 144),
            new("legato", 12000, 960, 0),
            new("pitch_up", 12000, 960, 0, 64),
            new("gap_100ms", 12000, 960, 100),
            new("gap_120ms", 12000, 960, 120),
            new("slow", 6000, 480),
            new("fast", 24000, 1920),
            new("tempo_change", 12000, 960, TempoChange: true),
            new("screenshot_te", 12000, 960, Phonemes: "t e"),
            new("nasal_na", 12000, 960, Phonemes: "n a"),
            new("velocity_32", 12000, 960, Velocity: 32),
            new("velocity_96", 12000, 960, Velocity: 96),
        ];
        int total = 0;
        int failed = 0;
        foreach (var scenario in scenarios)
        foreach (int releaseId in new[] { EvecConstants.ReleaseNone,
                     EvecConstants.ReleaseBreathShort, EvecConstants.ReleaseBreathLong })
        foreach (bool split in new[] { false, true })
        {
            total++;
            string name = $"{bankName}_{scenario.Name}_{releaseId}_{(split ? "split" : "raw")}";
            if (!RunTransition(manager, outputDirectory, bank, scenario, releaseId, split, render, name, asyncScore))
            {
                failed++;
                if (asyncScore) return 52;
            }
        }
        Console.WriteLine($"transition.total={total};failed={failed};non_silent_render_validation={render && failed == 0}");
        Console.WriteLine("transition.listening_validation=False");
        return failed == 0 ? 0 : 52;
    }

    private static bool RunTransition(WIVSMSequenceManager manager, string directory, RenderCase bank,
        TransitionScenario scenario, int releaseId, bool split, bool render, string name, bool asyncScore)
    {
        var sequence = manager.CreateSequence(new VSMSequenceData
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4,
            MaxUndoCount = 0,
        });
        if (sequence == null)
            return false;
        try
        {
            if (!sequence.InterpolatedMasterVolume() ||
                sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) == null ||
                sequence.InsertTempo(VSMRelTick.Zero, scenario.Tempo) == null ||
                (scenario.TempoChange && sequence.InsertTempo(new VSMRelTick(720), 6000) == null))
                return false;
            var track = sequence.InsertTrackEx(0, VSMTrackType.Midi, "EVEC transition") as WIVSMMidiTrack;
            var part = track?.InsertPart(VSMAbsTick.Zero, new VSMRelTick(7680), name);
            if (part == null || (render && !part.SetVoiceBankID(bank.ComponentId)))
                return false;

            var start = new VSMAbsTick(240);
            var end = new VSMAbsTick(240 + scenario.DurationTicks);
            double duration = sequence.GetTimeFromTick(start, end) * 1000;
            VSMAbsTick? next = scenario.NextGapMs.HasValue
                ? sequence.GetTickFromTime(end, scenario.NextGapMs.Value / 1000) : null;
            double? actualGap = next.HasValue ? sequence.GetTimeFromTick(end, next.Value) * 1000 : null;
            var state = new EvecNoteState(EvecConstants.VoiceColorSoft,
                bank.Name.StartsWith("miku", StringComparison.Ordinal)
                    ? EvecConstants.AttackMild : EvecConstants.AttackAccentPlain,
                releaseId, 0);
            IReadOnlyList<EvecRenderSegment> plan;
            if (split)
            {
                if (!EvecRenderTimingPlan.TryCreate(scenario.Phonemes, state, duration, actualGap, true, out plan))
                    return false;
                int expectedSegments = scenario.Name switch
                {
                    "short_80ms" => 1,
                    "short_125ms" or "legato" or "pitch_up" or "gap_100ms" => 2,
                    _ => 3,
                };
                if (releaseId == EvecConstants.ReleaseNone)
                    expectedSegments = scenario.Name == "short_80ms" ? 1 : 2;
                if (plan.Count != expectedSegments)
                    throw new InvalidOperationException("Unexpected short-note or connected-release decision.");
            }
            else
            {
                plan = new[] { new EvecRenderSegment(EvecRenderSegmentKind.Base, 0, duration,
                    EvecPhonemeRecomposer.Recompose(scenario.Phonemes, state)) };
            }

            // Convert shared edges once. Never round each segment duration
            // independently, which would introduce gaps at tempo changes.
            long previousEnd = start.Value;
            var details = new List<object>();
            var expression = part.GetDefaultNoteExpression();
            for (int index = 0; index < plan.Count; index++)
            {
                var segment = plan[index];
                long segmentEnd = index == plan.Count - 1 ? end.Value
                    : sequence.GetTickFromTime(start, segment.EndMs / 1000).Value;
                if (segmentEnd <= previousEnd || segmentEnd > end.Value)
                    return false;
                var segmentExpression = expression;
                if (segment.Kind != EvecRenderSegmentKind.Base)
                {
                    // PPS continuation defaults; preserve source opening/bend.
                    segmentExpression.Accent = 50;
                    segmentExpression.Decay = 50;
                    segmentExpression.RisePort = false;
                    segmentExpression.FallPort = false;
                }
                var note = part.InsertNote(new VSMRelTick(previousEnd),
                    new VSMNoteEvent(checked((int)(segmentEnd - previousEnd)), 60,
                        segment.Kind == EvecRenderSegmentKind.Base ? scenario.Velocity : 64),
                    segmentExpression, part.GetDefaultAiNoteExpression(),
                    "か", segment.Phonemes, true, 0);
                if (note == null || !note.ResetHmmWeightDefault() || !note.ResetAiVibratoDefault())
                    return false;
                double actualBegin = sequence.GetTimeFromTick(start, new VSMAbsTick(previousEnd)) * 1000;
                double actualEnd = sequence.GetTimeFromTick(start, new VSMAbsTick(segmentEnd)) * 1000;
                // Native conversion rounds to tick resolution. This matrix uses
                // 60 BPM or faster, where one tick is at most 2.084 ms.
                if (Math.Abs(actualBegin - segment.BeginMs) > 2.1 ||
                    Math.Abs(actualEnd - segment.EndMs) > 2.1)
                    return false;
                details.Add(new { kind = segment.Kind.ToString(), segment.Phonemes,
                    beginTick = previousEnd, endTick = segmentEnd,
                    requestedBeginMs = segment.BeginMs, requestedEndMs = segment.EndMs,
                    actualBeginMs = actualBegin, actualEndMs = actualEnd });
                previousEnd = segmentEnd;
            }
            if (next.HasValue)
            {
                var note = part.InsertNote(new VSMRelTick(next.Value.Value),
                    new VSMNoteEvent(480, scenario.NextPitch, 64), expression,
                    part.GetDefaultAiNoteExpression(), "か", "k a", true, 0);
                if (note == null || !note.ResetHmmWeightDefault() || !note.ResetAiVibratoDefault())
                    return false;
            }
            if (!sequence.Commit(false))
                return false;
            File.WriteAllText(Path.Combine(directory, name + ".json"),
                JsonSerializer.Serialize(new { name, scenario, durationMs = duration,
                    requestedGapMs = scenario.NextGapMs, actualGapMs = actualGap, segments = details },
                    new JsonSerializerOptions { WriteIndented = true }));
            bool passed = true;
            if (render)
            {
                string path = Path.Combine(directory, name + ".wav");
                VSMResult result = asyncScore ? RenderTransitionWithScore(sequence, part, path) : part.Render(path);
                WaveMetrics? metrics = result == VSMResult.NoError && File.Exists(path) ? ReadWaveMetrics(path) : null;
                passed = metrics is { DataBytes: > 0, Peak: > 0.000001 };
                Console.WriteLine($"transition.{name}.render={result};peak={metrics?.Peak.ToString("R", CultureInfo.InvariantCulture)}");
                using VSMScoreList? scores = part.HoldingScoreList ?? part.RenderingScoreList;
                PrintScoreSummary(name, scores);
                if (asyncScore && (scores == null || scores.NumScores <= 0))
                    throw new InvalidOperationException("Async render produced no timing score.");
                if (asyncScore)
                {
                    var noteTimings = part.Notes.Select(note => new
                    {
                        note.Phonemes,
                        startMs = sequence.GetTimeFromTick(VSMAbsTick.Zero, note.AbsPosTick) * 1000,
                        endMs = sequence.GetTimeFromTick(VSMAbsTick.Zero, note.AbsEndTick) * 1000,
                        boundariesMs = note.GetPhonemePositions().Select(tick =>
                            sequence.GetTimeFromTick(VSMAbsTick.Zero,
                                note.GetAbsPositionFromNoteBaseTick(tick)) * 1000).ToArray()
                    }).ToArray();
                    File.WriteAllText(Path.Combine(directory, name + ".timing.json"),
                        JsonSerializer.Serialize(new { frameCount = scores!.NumScores,
                            presendMs = sequence.PresendTimeSec * 1000,
                            frameMs = sequence.NumSampleInFrame * 1000.0 / (double)sequence.GetSamplingRate(),
                            notes = noteTimings }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            Console.WriteLine($"transition.{name}.segments={plan.Count};passed={passed}");
            return passed;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"transition.{name}.exception={exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            sequence.Close();
        }
    }

    private static VSMResult RenderTransitionWithScore(WIVSMSequence sequence, WIVSMMidiPart part, string path, bool blockRendering = false)
    {
        using var observer = new WIVSMRendererObserver();
        int status = 0;
        observer.Completed += (_, args) =>
        {
            if ((IntPtr)args.MidiPart == (IntPtr)part) status = 1;
        };
        observer.Canceled += (_, args) =>
        {
            if ((IntPtr)args.MidiPart == (IntPtr)part) status = -1;
        };
        if (!sequence.AddRendererObserver(observer))
            throw new InvalidOperationException("Cannot attach isolated render observer.");
        try
        {
            sequence.BlockRenderingEnabled = blockRendering;
            sequence.StartAsyncRendering();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (status == 0 && timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                for (int count = 0; count < 8 && sequence.InvokeRendererObserver(); count++) { }
                if (status == 0) Thread.Sleep(10);
            }
            if (status != 1)
                throw new InvalidOperationException("Isolated async render canceled or timed out.");
            File.Copy(part.WaveFilePath, path, true);
            return VSMResult.NoError;
        }
        finally
        {
            sequence.StopAsyncRendering();
            sequence.RemoveRendererObserver(observer);
        }
    }
}
