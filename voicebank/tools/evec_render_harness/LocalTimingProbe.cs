using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeLocalTiming(WIVSMSequenceManager manager, string directory, bool mapOnly)
    {
        Directory.CreateDirectory(directory);
        var sequence = manager.CreateSequence(new VSMSequenceData
        { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        try
        {
            sequence.InterpolatedMasterVolume();
            sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
            sequence.InsertTempo(VSMRelTick.Zero, 12000);
            var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "local timing")!;
            var part = track.InsertPart(VSMAbsTick.Zero, new VSMRelTick(2400), "local timing")!;
            if (!part.SetVoiceBankID("BCNFCY43LB2LZCD4")) throw new InvalidOperationException("Missing fixture bank.");
            var expression = part.GetDefaultNoteExpression();
            part.InsertNote(new VSMRelTick(480), new VSMNoteEvent(480, 60, 64), expression,
                part.GetDefaultAiNoteExpression(), "あ", "a", true, 0);
            var note = part.InsertNote(new VSMRelTick(960), new VSMNoteEvent(480, 60, 64), expression,
                part.GetDefaultAiNoteExpression(), "か", "k a", true, 0)!;
            part.InsertNote(new VSMRelTick(1440), new VSMNoteEvent(480, 62, 64), expression,
                part.GetDefaultAiNoteExpression(), "な", "n a", true, 0);
            sequence.Commit(false);
            nint vsm = ResolveEditorLibrary("vsm");
            foreach (int slot in new[] { 0x288, 0x290, 0x298, 0x2a0 })
                Console.WriteLine($"local.part_vtable.{slot:X}=0x{Marshal.ReadIntPtr(Marshal.ReadIntPtr((nint)part), slot) - vsm:X}");
            Console.WriteLine($"local.sequence_commit=0x{Marshal.ReadIntPtr(Marshal.ReadIntPtr((nint)sequence), 0x1e0) - vsm:X}");
            if (mapOnly) return 0;
            using var counters = new TimingDseCounters();
            var results = new List<object>();
            // Ground truth only: one short, private three-note reference render.
            var counterStart = counters.Read();
            RenderTransitionWithScore(sequence, part, Path.Combine(directory, "reference.wav"));
            var positiveControl = counters.Read().Since(counterStart);
            if (positiveControl.Steps == 0 || positiveControl.Samples == 0)
                throw new InvalidOperationException("Audio counter positive control failed.");
            int[] baseline = note.GetPhonemePositions().ToArray();
            foreach (string mode in new[] { "commit", "score_local", "score_full" })
            {
                note.NoteVelocity = 64;
                sequence.Commit(false);
                RenderTransitionWithScore(sequence, part, Path.Combine(directory, "reference.wav"));
                var watch = Stopwatch.StartNew();
                var beforeCounters = counters.Read();
                bool begin = mode != "commit" && part.BeginScoreEdit();
                var editingBefore = ReadEditingTiming(part);
                note.NoteVelocity = 32;
                bool update = mode == "commit" ? sequence.Commit(false) : begin &&
                    part.UpdateScoreEdit(false, mode == "score_local" ? 720 : 0,
                        mode == "score_local" ? 1680 : 2400);
                var editing = ReadEditingTiming(part);
                bool end = mode == "commit" || (begin && part.EndScoreEdit());
                int[] result = note.GetPhonemePositions().ToArray();
                long ticks = watch.ElapsedTicks;
                var delta = counters.Read().Since(beforeCounters);
                bool validScore = part.HasValidRenderedScore, staged = sequence.IsStaged;
                Console.WriteLine($"local.mode={mode};before={string.Join(',', baseline)};after={string.Join(',', result)};update={update};end={end}");
                if (sequence.IsStaged) sequence.Commit(false);
                RenderTransitionWithScore(sequence, part, Path.Combine(directory, "ground-truth.wav"));
                int[] actual = note.GetPhonemePositions().ToArray();
                results.Add(new { mode, begin, update, end, before = baseline, after = result, actual,
                    withoutAudioEqual = result.SequenceEqual(actual),
                    elapsedMicroseconds = ticks * 1e6 / Stopwatch.Frequency,
                    validScore, staged, editingBefore, editing, dse = delta });
                Console.WriteLine($"local.mode={mode};rendered={string.Join(',', actual)};without_audio_equal={result.SequenceEqual(actual)}");
            }
            File.WriteAllText(Path.Combine(directory, "local-commit.json"),
                JsonSerializer.Serialize(new { dseVtable = counters.VtableRva, positiveControl, results },
                    new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { sequence.Close(); }
    }

    private static object? ReadEditingTiming(WIVSMMidiPart part)
    {
        using var score = part.EditingScoreList;
        if (score == null) return null;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        long scanned = Math.Min(score.NumScores, 3000);
        for (long frame = 0; frame < scanned; frame++)
        {
            var phoneme = score.ScoreAtIndex(frame).PhnDur;
            writer.Write(phoneme.FwIdx);
            writer.Write(phoneme.BwIdx);
            writer.Write(phoneme.LeftDur);
            writer.Write(phoneme.RightDur);
            writer.Write(phoneme.FromPhU != 0);
            writer.Write(phoneme.ToPhU != 0);
        }
        return new { frames = score.NumScores, scanned,
            geometrySha256 = Convert.ToHexString(SHA256.HashData(stream.ToArray())) };
    }
}
