#if PATCHER_ISOLATED_VALIDATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

internal static class CompSustainTimingValidation
{
    private static T Wait<T>(Task<T> task)
    {
        while (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    internal static void Run(WIVSMSequenceManager manager, string directory)
    {
        var records = new List<object>();
        WIVSMSequence sequence;
        WIVSMMidiPart part;
        int target;
        if (Environment.GetEnvironmentVariable("VOCALOID_COMP_SUSTAIN_PROJECT") is { } project)
        {
            string path = Path.Combine(directory, "source-sequence.json");
            using (var zip = System.IO.Compression.ZipFile.OpenRead(project))
            using (var input = zip.GetEntry("Project/sequence.json")!.Open())
            using (var output = File.Create(path)) input.CopyTo(output);
            sequence = manager.OpenSequence(path, Path.GetDirectoryName(typeof(WIVSMSequence).Assembly.Location)!,
                new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 32, MaxUndoCount = 100 })
                ?? throw new InvalidOperationException("Cannot read sustain fixture.");
            int partIndex = int.Parse(Environment.GetEnvironmentVariable("VOCALOID_COMP_SUSTAIN_PART") ?? "1");
            target = int.Parse(Environment.GetEnvironmentVariable("VOCALOID_COMP_SUSTAIN_NOTE") ?? "16");
            part = ((WIVSMMidiTrack)sequence.GetTrack(0)!).GetPart((ulong)partIndex)!;
            var timing = SampledTimingProjectArchive.Read(project).Where(p => p.Key.Track == 0 && p.Key.Part == partIndex).ToDictionary();
            var transitions = TransitionProjectArchive.Read(project).Data!.KeepNotes(n => n.Address.Track == 0 && n.Address.Part == partIndex);
            SampledTimingHost.Load(sequence, timing, TransitionArchiveData.From(transitions));
        }
        else
        {
            sequence = manager.CreateSequence(new VSMSequenceData { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 100 })!;
            sequence.InsertTempo(VSMRelTick.Zero, 14600); sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
            var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "sustain-timing")!;
            part = track.InsertPart(new VSMAbsTick(26880), new VSMRelTick(7440), "sustain-timing")!;
            if (!part.SetVoiceBankID("BETDB8W6KWZPYEB9")) throw new InvalidOperationException("Missing sustain fixture bank.");
            string[] phones = ["uo", "k_h 7", "t 7", "ts` UN", "t a_n"];
            for (int i = 0; i < phones.Length; i++)
                _ = part.InsertNote(new VSMRelTick(6000 + i * 240), new VSMNoteEvent(240, (byte)(63 + i % 3), 64),
                    part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(), "fixture", phones[i], true, 4)
                    ?? throw new InvalidOperationException("Cannot create sustain fixture note.");
            sequence.InterpolatedMasterVolume();
            if (!sequence.Commit(false)) throw new InvalidOperationException("Cannot commit sustain fixture.");
            target = 3;
            var timing = new Dictionary<TimingNoteAddress, SampledTimingPlan>();
            foreach (var (index, boundary, frames) in new[] { (0, 0, -9), (0, 1, 4), (1, 0, 4), (1, 2, 4), (2, 0, 4), (3, 2, 1) })
            {
                var note = part.GetNote((ulong)index)!; var address = SampledTimingVsmBinding.Address(note);
                var edits = timing.GetValueOrDefault(address)?.Adjustments ?? [];
                timing[address] = new(note.Phonemes, edits.Append(new(TimingAdjustment.PhonemeBoundary, boundary,
                    (float)(frames / TransitionProjectBinding.FramesPerSecond))));
            }
            SampledTimingHost.Load(sequence, timing);
        }
        try
        {
            var original = SampledTimingHost.Find(sequence)!.Document.Fingerprint;
            var originalTransitions = SampledTimingHost.Find(sequence)!.Transitions.Snapshot.Fingerprint;
            var originalNotes = part.Notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value)).ToArray();
            var indices = Enumerable.Range(0, target + 1).ToArray();
            CompSourceAudio Render(double length, string label)
            {
                using var source = new CompNativeSource(manager, part, indices, extendEnd: true, holdLengthMs: length);
                var originalSession = SampledTimingHost.Find(sequence)!; var copiedSession = SampledTimingHost.Find(source.Sequence);
                foreach (int index in indices.Where(i => i != target))
                {
                    var before = originalSession.Document.Get(SampledTimingVsmBinding.Address(part.GetNote((ulong)index)!));
                    var after = copiedSession?.Document.Get(SampledTimingVsmBinding.Address(source.Part.GetNote((ulong)index)!));
                    if (before?.Fingerprint != after?.Fingerprint) throw new InvalidOperationException("Source dropped a neighboring timing plan: " + index);
                }
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                return Wait(source.RenderAsync(Path.Combine(directory, "sustain-" + label + ".wav"), cancellation.Token));
            }
            var automatic = Render(0, "automatic");
            bool passed = true;
            foreach (double length in new[] { 148d, 350d, 650d, 0d })
            {
                var changed = Render(length, length.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                var differences = automatic.Notes.Where(n => n.Index != target).Select(n => new
                {
                    n.Index, before = n.Boundaries,
                    after = changed.Notes.Single(v => v.Index == n.Index).Boundaries
                }).Where(n => !n.before.SequenceEqual(n.after)).ToArray();
                passed &= differences.Length == 0;
                if (length == 0 && !automatic.Notes.Single(n => n.Index == target).Boundaries.SequenceEqual(changed.Notes.Single(n => n.Index == target).Boundaries))
                    throw new InvalidOperationException("Returning to automatic length did not restore target boundaries.");
                var record = new { stage = "sustain-neighbor-timing", lengthMs = length, unchangedNotes = target,
                    passed = differences.Length == 0, differences };
                records.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
                File.WriteAllText(Path.Combine(directory, "sustain-timing-validation.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (SampledTimingHost.Find(sequence)!.Document.Fingerprint != original) throw new InvalidOperationException("Sustain source changed main timing controls.");
            if (SampledTimingHost.Find(sequence)!.Transitions.Snapshot.Fingerprint != originalTransitions ||
                !part.Notes.Select(n => (n.RelPosTick.Value, n.DurationTick.Value)).SequenceEqual(originalNotes))
                throw new InvalidOperationException("Sustain source changed main transitions or notes.");
            if (!passed) throw new InvalidOperationException("Source length changed neighboring phoneme boundaries.");
        }
        finally
        {
            VoiceCompHost.DrainForManagerDisposal(); sequence.StopAsyncRendering(); sequence.Close();
        }
    }
}
#endif
