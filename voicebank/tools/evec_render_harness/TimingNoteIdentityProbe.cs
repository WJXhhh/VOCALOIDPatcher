using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbeTimingNoteIdentity(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        Directory.CreateDirectory(directory);
        using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 2880);
        fixture.Sequence.SetMaxUndoCount(100);
        var visualNote = fixture.Notes[0];
        visualNote.SetPhonemes("k a", true, 0); fixture.Sequence.Commit(false);
        var parameterNote = fixture.Part.GetNote(0UL)!;
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var counters = new TimingDseCounters();
        var before = counters.Read();
        var states = new List<object>();
        var milliseconds = new Dictionary<string, double>();
        void Capture(string stage)
        {
            var current = fixture.Part.GetNote(0UL)!;
            var fromSequence = ((WIVSMMidiTrack)fixture.Sequence.GetTrack(0UL)!).GetPart(0UL) as WIVSMMidiPart;
            var active = fromSequence!.GetNote(0UL)!;
            long[]? boundaries = null;
            double? currentMilliseconds = null;
            if (!fixture.Sequence.IsStaged)
            {
                boundaries = backend.Query(visualNote, visualNote.NoteVelocity).Boundaries;
                currentMilliseconds = 1000 * (fixture.Sequence.GetTimeFromTick(visualNote.AbsPosTick + boundaries[1]) -
                    fixture.Sequence.GetTimeFromTick(visualNote.AbsPosTick + boundaries[0]));
                milliseconds[stage] = currentMilliseconds.Value;
            }
            states.Add(new { stage, visual = visualNote.NoteVelocity, parameter = parameterNote.NoteVelocity,
                current = current.NoteVelocity, active = active.NoteVelocity,
                sameVisualCurrent = (nint)visualNote == (nint)current,
                samePart = (nint)fromSequence == (nint)fixture.Part, staged = fixture.Sequence.IsStaged, boundaries, currentMilliseconds });
        }
        Capture("before");
        parameterNote.NoteVelocity = 0; Capture("staged");
        fixture.Sequence.Commit(true); Capture("committed");
        fixture.Sequence.Undo(); Capture("undo");
        fixture.Sequence.Redo(); Capture("redo");
        Require(visualNote.NoteVelocity == 0 && milliseconds["before"] != milliseconds["committed"] &&
            milliseconds["undo"] == milliseconds["before"] && milliseconds["redo"] == milliseconds["committed"], "Live VEL feedback did not follow commit/undo/redo.");
        var delta = counters.Read().Since(before);
        Require(delta.Starts == 0 && delta.Steps == 0 && delta.Samples == 0, "Feedback validation synthesized audio.");
        string json = JsonSerializer.Serialize(new { states, queryDse = delta }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        File.WriteAllText(Path.Combine(directory, "timing-note-identity.json"), json);
        return 0;
    }
}
