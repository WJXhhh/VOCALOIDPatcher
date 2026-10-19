using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Native baseline diagnosis only. Never touches the running Editor or
    // publishes sampled controls into these disposable fixture Parts.
    private static int ProbeSeiConsonant(WIVSMSequenceManager manager, DSEManager dse, DatabaseManager database, string directory)
    {
        NativeTransitionGain.Initialize(Environment.GetEnvironmentVariable("VOCALOID_TIMING_PROBE_NATIVE")!);
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var capture = new TimingCoefficientHook();
        var materialIds = new Dictionary<nint, int>(); var results = new List<object>();
        void Record(object row)
        {
            results.Add(row); File.WriteAllText(Path.Combine(directory, "sei-consonant.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        Record(new { kind = "metadata", sdk = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(), profile = LocalTimingNative.Profile.Id,
            bank = "BY98KLLZTDYH7YEE", pitch = 62, duration = 480, velocity = 64, customControls = false });
        foreach (string phones in new[] { "s ei", "s a", "s 7", "s iE_r", "s", "s\\ ei", "s\\ iE_r", "s 7 ei", "s e i", "ei" })
        {
            using var fixture = CreateStandaloneFixture(manager, 1, 480, 0, 4800);
            var note = fixture.Notes[0]; var part = fixture.Part; var sequence = fixture.Sequence;
            Require(part.SetVoiceBankID("BY98KLLZTDYH7YEE"), "Missing diagnosis voicebank.");
            part.MoveNote(new VSMRelTick(960), note); note.SetNoteNumber(62); note.NoteVelocity = 64; note.SetPhonemes(phones, true, 4);
            sequence.InterpolatedMasterVolume(); sequence.Commit(false);
            object? planned = null; string? planningError = null;
            using (var prepared = backend.PrepareCoefficient(note, 1))
            {
                try
                {
                    var layout = prepared.QueryTransitions(new(phones, []), 1);
                    var execution = prepared.QueryTransitionControls(layout, new(layout.Fingerprint), 1);
                    planned = execution.Segments.Select(s => new { s.From, s.To, s.Begin, s.Split, s.End, s.Stage, s.Reverse, s.MaterialCoverage,
                        sourceFirst = s.Frames.IsEmpty ? (float?)null : BitConverter.Int32BitsToSingle(s.Frames[0].SourceBits),
                        sourceLast = s.Frames.IsEmpty ? (float?)null : BitConverter.Int32BitsToSingle(s.Frames[^1].SourceBits) }).ToArray();
                }
                catch (NotSupportedException error) { planningError = error.Message; }
            }
            var rows = new Dictionary<int, DurationRow>();
            var named = new Dictionary<int, TransitionSegment>(); int consumed = 3087;
            capture.Reset(-1, false, null);
            capture.AfterStep = state =>
            {
                int cursor = Marshal.ReadInt32(state, LocalTimingNative.Profile.StateCurrentFrame);
                if (cursor <= consumed) return;
                foreach (var row in ReadDurationRows(state, materialIds, consumed, cursor)) rows[row.Begin] = row;
                foreach (var row in NativeConsonantTimingEngine.ReadTransitionSchedule(LocalTimingNative.Dse, state, 0, consumed, cursor))
                    named[row.Begin] = row;
                consumed = cursor;
            };
            string file = "native-" + results.Count + ".wav";
            try { RenderTransitionWithScore(sequence, part, Path.Combine(directory, file)); capture.Check(); }
            finally { capture.AfterStep = null; }
            Record(new { kind = "native", phones, boundaries = NativeTimingPositions.Read(note, false), planned, planningError,
                actual = rows.Values.OrderBy(r => r.Begin).ToArray(),
                actualNames = named.Values.OrderBy(r => r.Begin).Select(s => new { s.From, s.To, s.Begin, s.Split, s.End, s.Stage, s.Reverse, s.MaterialCoverage }).ToArray(),
                pcm = TimingPcmHash(Path.Combine(directory, file)), file });
            Console.WriteLine("sei.native=" + phones);
        }
        Record(new { kind = "complete" }); return 0;
    }
}
