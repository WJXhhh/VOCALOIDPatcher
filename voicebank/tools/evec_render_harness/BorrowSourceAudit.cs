using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Audit the donor before further reconstruction. The material split is an
    // alignment label, not proof of the extent of a perceptual consonant cue.
    private static int ProbeBorrowSourceAudit(WIVSMSequenceManager manager, TimingCoefficientHook capture,
        List<BorrowDonor> donors, string directory)
    {
        using var gain = new TransitionGainHook();
        var results = new List<object>();
        foreach (var source in donors)
        {
            using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 4800);
            Require(fixture.Part.SetVoiceBankID("BY98KLLZTDYH7YEE"), "Missing audit voicebank.");
            var note = fixture.Notes[0];
            fixture.Part.MoveNote(new VSMRelTick(960), note); note.SetNoteNumber(62);
            note.SetPhonemes("s " + source.Vowel, true, 4);
            fixture.Sequence.InterpolatedMasterVolume();
            int begin = BitConverter.ToInt32(source.View, 0), end = BitConverter.ToInt32(source.View, 4),
                split = begin + BitConverter.ToInt32(source.View, 8);
            var target = new DurationRow(begin, end, BitConverter.ToInt32(source.View, 0x10),
                BitConverter.ToInt32(source.View, 0x14), BitConverter.ToInt32(source.View, 0x28),
                0, 4, 6, new(0, 0, 0, 0, 0, []));
            string? reference = null; int[]? referenceBounds = null;
            foreach (string pass in new[] { "warmup", "reference", "left-muted", "primary-muted", "auxiliary-muted",
                "right-first4-muted", "right-late-muted", "all-muted", "restored" })
            {
                note.NoteVelocity = 63; fixture.Sequence.Commit(false);
                note.NoteVelocity = 64; fixture.Sequence.Commit(false);
                gain.Reset(target, 0, 0, 1);
                gain.GainFrameRange = null;
                gain.GainTransform = (time, lane, original) =>
                {
                    bool mute = pass switch
                    {
                        "left-muted" => time < split,
                        "primary-muted" => time < split && lane == "primary",
                        "auxiliary-muted" => time < split && lane == "auxiliary",
                        "right-first4-muted" => time >= split && time < split + 4,
                        "right-late-muted" => time >= split + 4,
                        "all-muted" => true,
                        _ => false
                    };
                    return mute ? 0 : original;
                };
                capture.Reset(-1, false, null);
                capture.BeforeSynthesis = state => TransitionGainHook.State = state;
                capture.AfterOutput = (_, _) => TransitionGainHook.State = 0;
                string file = "source-" + source.Vowel + "-" + pass + ".wav";
                try { RenderTransitionWithScore(fixture.Sequence, fixture.Part, Path.Combine(directory, file)); capture.Check(); gain.Check(); }
                finally { capture.BeforeSynthesis = null; capture.AfterOutput = null; TransitionGainHook.State = 0; }
                var bounds = NativeTimingPositions.Read(note, false);
                string pcm = TimingPcmHash(Path.Combine(directory, file));
                if (pass == "reference") { reference = pcm; referenceBounds = bounds; }
                if (pass != "warmup") Require(bounds.SequenceEqual(referenceBounds!), "Audit timing changed.");
                if (pass == "restored") Require(pcm == reference, "Audit did not restore exactly.");
                File.WriteAllText(Path.Combine(directory, "source-" + source.Vowel + "-" + pass + "-gain.json"), JsonSerializer.Serialize(gain.Trace));
                results.Add(new { vowel = source.Vowel, pass, file, begin, split, end, bounds, pcm,
                    lanes = gain.Trace.Where(f => f.Selected).GroupBy(f => f.Lane)
                        .Select(g => new { lane = g.Key, calls = g.Count(), first = g.Min(f => f.Time), last = g.Max(f => f.Time) }).ToArray() });
                File.WriteAllText(Path.Combine(directory, "source-audit.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("borrow.source-audit=" + source.Vowel + ":" + pass);
            }
        }
        return 0;
    }
}
