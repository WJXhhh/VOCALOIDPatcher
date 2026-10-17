using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeConsonantVelocity(WIVSMSequenceManager manager,
        DatabaseManager database, string directory, bool sweep, bool solve, bool lifecycle)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        string[] banks = sweep ? ["BCNFCY43LB2LZCD4"] : ["BCNFCY43LB2LZCD4", "BKKP765AEHXWSKDB"];
        foreach (string bank in banks)
        {
            bool available = false;
            for (ulong i = 0; i < database.NumVoiceBanks; i++)
                available |= database.GetVoiceBankByIndex(i)?.CompID == bank;
            if (!available) continue;
            foreach (string phonemes in sweep ? new[] { "k a", "n a" } : new[] { "k a", "n a", "dZ M", "k k a" })
            foreach (bool legato in new[] { false, true })
            foreach (bool shiftedTempo in solve ? new[] { false, true } : new[] { false })
            {
                if (lifecycle && (bank != banks[0] || phonemes != "k a" || legato || shiftedTempo)) continue;
                var sequence = manager.CreateSequence(new VSMSequenceData
                {
                    SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0,
                }) ?? throw new InvalidOperationException("Cannot create isolated sequence.");
                try
                {
                    sequence.InterpolatedMasterVolume();
                    sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
                    sequence.InsertTempo(VSMRelTick.Zero, 12000);
                    long partStart = shiftedTempo ? 4800 : 0;
                    if (shiftedTempo) sequence.InsertTempo(new VSMRelTick(partStart + 910), 6000);
                    var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "timing probe")!;
                    var part = track.InsertPart(new VSMAbsTick(partStart), new VSMRelTick(3840), "timing probe")!;
                    if (!part.SetVoiceBankID(bank)) throw new InvalidOperationException("Voicebank unavailable.");
                    var expression = part.GetDefaultNoteExpression();
                    if (legato)
                        part.InsertNote(new VSMRelTick(480), new VSMNoteEvent(480, 60, 64), expression,
                            part.GetDefaultAiNoteExpression(), "あ", "a", true, 0);
                    var note = part.InsertNote(new VSMRelTick(960), new VSMNoteEvent(480, 60, 64), expression,
                        part.GetDefaultAiNoteExpression(), "か", phonemes, true, 0)!;
                    foreach (int velocity in sweep ? Enumerable.Range(0, 128) : solve
                                 ? bank == banks[0] && phonemes == "k a" && !legato && !shiftedTempo
                                     ? new[] { 64, 32, 96 } : new[] { 64 }
                                 : new[] { 64, 32, 0, 96, 127, 63, 65 })
                    {
                        note.NoteVelocity = velocity;
                        if (!sequence.Commit(false)) throw new InvalidOperationException("Commit failed.");
                        int[] before = note.GetPhonemePositions().ToArray();
                        var watch = Stopwatch.StartNew();
                        RenderTransitionWithScore(sequence, part, Path.Combine(directory, "last.wav"));
                        int[] after = note.GetPhonemePositions().ToArray();
                        if (!solve && !sweep && velocity == 64 && phonemes == "k a" && !legato)
                        {
                            bool begin = part.BeginScoreEdit();
                            note.NoteVelocity = 32;
                            bool update = begin && part.UpdateScoreEdit(false, 0, 3840);
                            Console.WriteLine($"consonant.score_edit={begin}/{update};positions={string.Join(',', note.GetPhonemePositions())}");
                            note.NoteVelocity = 64;
                            if (begin) part.CancelScoreEdit();
                            sequence.Commit(false);
                        }
                        results.Add(new { bank, phonemes, legato, shiftedTempo, velocity, before, after,
                            milliseconds = after.Select(t => sequence.GetTimeFromTick(
                                note.GetAbsPositionFromNoteBaseTick(t)) * 1000).ToArray(),
                            elapsedMs = watch.ElapsedMilliseconds });
                        Console.WriteLine($"consonant.velocity={velocity};phonemes={phonemes};legato={legato};positions={string.Join(',', after)};elapsed={watch.ElapsedMilliseconds}");
                        if (solve)
                        {
                            if (after.Length == 0) throw new InvalidOperationException("Fixture has no timing.");
                            long[] original = after.Select(p => note.GetAbsPositionFromNoteBaseTick(p).Value).ToArray();
                            using var calibration = new ConsonantTimingCalibration(manager, note, velocity, original,
                                () => true, copy => { copy.Close(); NativeRenderSession.UnregisterInternalSequence(copy); });
                            foreach (long target in new long[] { -55, -130, 10, -500 }.Select(delta => note.AbsPosTick.Value + delta))
                            {
                                var timer = Stopwatch.StartNew();
                                var sample = ConsonantTimingMath.FindNearestAsync(target, velocity,
                                    calibration.MeasureAsync, () => true).GetAwaiter().GetResult();
                                if (note.NoteVelocity != velocity || !note.GetPhonemePositions().SequenceEqual(after) || sequence.IsStaged)
                                    throw new InvalidOperationException("Calibration changed source.");
                                long elapsed = timer.ElapsedMilliseconds;
                                note.NoteVelocity = sample.Velocity;
                                sequence.Commit(false);
                                RenderTransitionWithScore(sequence, part, Path.Combine(directory, "verify.wav"));
                                var actual = note.GetPhonemePositions().Select(p => note.GetAbsPositionFromNoteBaseTick(p).Value).ToArray();
                                if (!actual.SequenceEqual(sample.Boundaries)) throw new InvalidOperationException("Engine preview differs from committed render.");
                                results.Add(new { bank, phonemes, legato, shiftedTempo, initialVelocity = velocity, target, sample.Velocity, sample.Boundaries,
                                    actual, elapsedMs = elapsed, sourceUnchangedDuringProbe = true });
                                Console.WriteLine($"consonant.solver.target={target};v={sample.Velocity};actual={actual[0]};exact=True;elapsed={elapsed}");
                                note.NoteVelocity = velocity;
                                sequence.Commit(false);
                                RenderTransitionWithScore(sequence, part, Path.Combine(directory, "restore.wav"));
                            }
                            if (bank == banks[0] && phonemes == "k a" && !legato && !shiftedTempo && velocity == 64)
                            {
                                bool current = true;
                                using var canceled = new ConsonantTimingCalibration(manager, note, velocity, original,
                                    () => current, copy => { copy.Close(); NativeRenderSession.UnregisterInternalSequence(copy); });
                                var operation = canceled.MeasureAsync(velocity);
                                current = false;
                                try { operation.GetAwaiter().GetResult(); throw new InvalidOperationException("Canceled probe returned a sample."); }
                                catch (OperationCanceledException) { }
                                if (NativeRenderSession.Gate.CurrentCount != 1 || note.NoteVelocity != velocity || sequence.IsStaged)
                                    throw new InvalidOperationException("Cancellation leaked render state.");
                                Console.WriteLine("consonant.cancel=passed;source_unchanged=True;gate_released=True");
                            }
                        }
                    }
                }
                finally { sequence.Close(); }
                File.WriteAllText(Path.Combine(directory, "velocity.json"),
                    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Console.WriteLine($"consonant.cases={results.Count}");
        return results.Count > 0 ? 0 : 1;
    }
}
