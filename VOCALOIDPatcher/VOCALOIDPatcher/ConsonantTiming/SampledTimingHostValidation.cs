using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using HarmonyLib;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Explicit developer opt-in for a newly launched, empty Editor document only.
// Uses the actual command, archive and async-render paths inside the host.
internal static class SampledTimingHostValidation
{
    internal static void Queue()
    {
        if (Environment.GetEnvironmentVariable("VOCALOID_TIMING_HOST_VALIDATION") != "1") return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(Run));
    }

    private static async void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "TimingHostValidation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(directory)!, "current.txt"), directory);
        var stages = new List<object>();
        void Record(string stage, object? data = null)
        {
            stages.Add(new { stage, data });
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(stages, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            for (int i = 0; i < 120 && App.Shared?.Document?.Sequence?.VSMSequence == null; i++) await Task.Delay(250);
            var document = App.Shared?.Document ?? throw new InvalidOperationException("No Editor document.");
            var editor = document.Sequence ?? throw new InvalidOperationException("No Editor sequence.");
            var source = editor.VSMSequence ?? throw new InvalidOperationException("No native sequence.");
            Check(document.IsNew && !source.IsDirty && source.Tracks.OfType<WIVSMMidiTrack>()
                .SelectMany(t => t.Parts.OfType<WIVSMMidiPart>()).All(p => p.NumNotes == 0), "Validation requires an unmodified empty new document.");
            Record("empty_host_confirmed", new { editorVersion = typeof(App).Assembly.GetName().Version?.ToString() });
            source.StopAsyncRendering(); source.SetMaxUndoCount(100);
            var parts = new List<WIVSMMidiPart>();
            var notes = new List<WIVSMNote[]>();
            float[] coefficients = [.00390625f, .18359375f, .736328125f, .376953125f];
            for (int p = 0; p < 4; p++)
            {
                var track = (WIVSMMidiTrack)source.InsertTrackEx(source.NumTrack, VSMTrackType.Midi, "Timing validation " + p)!;
                var part = track.InsertPart(new VSMAbsTick(p < 2 ? 0 : 17003), new VSMRelTick(2880), "Timing validation")!;
                Check(part.SetVoiceBankID(p % 2 == 0 ? "BCNFCY43LB2LZCD4" : "BKKP765AEHXWSKDB"), "Missing validation bank.");
                var group = new WIVSMNote[3];
                for (int n = 0; n < group.Length; n++) group[n] = part.InsertNote(new VSMRelTick(480 + n * 480),
                    new VSMNoteEvent(480, 60, 64), part.GetDefaultNoteExpression(), part.GetDefaultAiNoteExpression(),
                    "timing", n == 0 ? "a" : n == 1 ? "k a i" : "n a", true, 0)!;
                parts.Add(part); notes.Add(group);
            }
            source.InterpolatedMasterVolume(); Check(source.Commit(true), "Fixture commit failed.");
            for (int p = 0; p < parts.Count; p++)
            {
                SampledTimingHost.SetCoefficient(notes[p][1], coefficients[p]);
                SampledTimingHost.SetCoefficient(notes[p][2], coefficients[3 - p]);
            }
            async Task Wait(WIVSMSequence sequence, IReadOnlyList<WIVSMMidiPart> targets, bool requireApplied = false)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(45))
                {
                    while (sequence.InvokeRendererObserver()) { }
                    if (targets.All(p => {
                        var s = NativeSampledTiming.ReadStatus((nint)p);
                        return p.HasValidRenderedWave && s.Failures == 0 && (!requireApplied || s.Applied > 0);
                    })) return;
                    await Task.Delay(25);
                }
                throw new TimeoutException("Host timing render did not complete: " + string.Join(";", targets.Select(p => {
                    var s = NativeSampledTiming.ReadStatus((nint)p); return $"{p.HasValidRenderedWave}/{p.HasValidRenderedScore}:{s.Epoch}/{s.Scopes}/{s.Applied}/{s.Failures}";
                })));
            }
            void Verify(WIVSMSequence sequence, IReadOnlyList<WIVSMMidiPart> targets)
            {
                var session = SampledTimingHost.Find(sequence)!;
                var bound = SampledTimingVsmBinding.Resolve(sequence, session.Document);
                foreach (var part in targets)
                    foreach (var note in part.NotesEnumerator.Where(n => bound.ContainsKey((nint)n)))
                    {
                        using var prepared = session.Prepare(note);
                        var expected = prepared.Query(bound[(nint)note], session.Revision).Boundaries;
                        Check(note.GetPhonemePositions().Select(v => (long)v).SequenceEqual(expected), "Host rendered boundary differs from query.");
                    }
            }
            await Wait(source, parts, true); Verify(source, parts);
            Record("four_parts", new { parts = 4, notes = 8, peakConcurrentScopes = NativeSampledTiming.PeakScopes,
                statuses = parts.Select(p => { var s = NativeSampledTiming.ReadStatus((nint)p); return new { s.Epoch, s.Scopes, s.Applied, s.Failures }; }).ToArray() });

            async Task RenderAgain()
            {
                // Let the coalesced edit callback invalidate before waiting on
                // a formerly valid file, then use the ordinary start entry.
                await Task.Delay(100); source.StartAsyncRendering(); await Wait(source, parts); Verify(source, parts);
            }
            float? Value() => SampledTimingHost.Find(source)!.Document.Get(SampledTimingVsmBinding.Address(notes[0][1]))?.ExecutableCoefficient(notes[0][1].Phonemes);
            SampledTimingHost.SetCoefficient(notes[0][1], .009765625f); await RenderAgain();
            string a = PcmHash(parts[0].WaveFilePath);
            notes[0][0].SetNoteNumber(61); Check(source.Commit(true), "Native history edit failed."); await RenderAgain();
            SampledTimingHost.SetCoefficient(notes[0][1], .736328125f); await RenderAgain();
            string b = PcmHash(parts[0].WaveFilePath);
            var undo = new EditUndoCommand(); var redo = new EditRedoCommand();
            undo.Execute(null); Check(Value() == .009765625f && notes[0][0].NoteNumber == 61, "UI undo consumed wrong history entry."); await RenderAgain();
            undo.Execute(null); Check(Value() == .009765625f && notes[0][0].NoteNumber == 60, "UI native undo failed."); await RenderAgain();
            Check(PcmHash(parts[0].WaveFilePath) == a, "Undo did not restore PCM A.");
            redo.Execute(null); Check(notes[0][0].NoteNumber == 61 && Value() == .009765625f, "UI native redo failed."); await RenderAgain();
            redo.Execute(null); Check(Value() == .736328125f, "UI custom redo failed."); await RenderAgain();
            Check(PcmHash(parts[0].WaveFilePath) == b && a != b, "Redo did not restore distinct PCM B.");
            Record("shared_ui_history", new { mixedNativeAndTiming = true, pcmRestored = true });

            var save = AccessTools.Method(typeof(Yamaha.VOCALOID.Sequence), "SaveSequence",
                [typeof(WIVSMSequence), typeof(string), typeof(string), typeof(string)])!;
            string path = Path.Combine(directory, "host.vpr");
            Check((bool)save.Invoke(editor, [source, directory, "host", ".vpr"])!, "Host save failed.");
            Check(!SampledTimingHost.IsDirty(source), "Timing save remained dirty.");
            using var reopenedEditor = new Yamaha.VOCALOID.Sequence();
            var load = reopenedEditor.LoadProjectSequenceFile(path, out var reopened);
            Check(load.result == Yamaha.VOCALOID.Sequence.LoadResult.Success && reopened != null, "Host load failed.");
            try
            {
                reopened!.InterpolatedMasterVolume(); Check(reopened.Commit(false), "Reopened initialization failed.");
                var loadedParts = SampledTimingHost.Find(reopened)!.Document.Snapshot().Keys.Select(k =>
                    (WIVSMMidiPart)((WIVSMMidiTrack)reopened.GetTrack((ulong)k.Track)!).GetPart((ulong)k.Part)!).DistinctBy(p => (nint)p).ToArray();
                reopened.StartAsyncRendering(); await Wait(reopened, loadedParts, true); Verify(reopened, loadedParts);
                Check(PcmHash(loadedParts[0].WaveFilePath) == b, "Reopened PCM differs.");
                Record("host_save_load", new { plans = SampledTimingHost.Find(reopened)!.Document.Snapshot().Count, pcmRestored = true });
            }
            finally { reopened?.Close(); }

            var copy = App.SequenceManager!.DuplicateSequence(source, new VSMSequenceData {
                SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 32, MaxUndoCount = 0 })!;
            try
            {
                bool saved = await Task.Run(() => (bool)save.Invoke(editor, [copy, directory, "autosave", ".vpr.tmp"])!);
                Check(saved && SampledTimingProjectArchive.Read(Path.Combine(directory, "autosave.vpr.tmp")).Count == 8, "Background backup lost timing data.");
                Record("background_backup", new { plans = 8 });
            }
            finally { copy.Close(); }
            Record("passed", new { directory });
        }
        catch (Exception e) { Record("failed", new { error = e.ToString() }); }
    }

    private static string PcmHash(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path)); reader.BaseStream.Position = 12;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            uint kind = reader.ReadUInt32(), size = reader.ReadUInt32();
            if (kind == 0x61746164) return Convert.ToHexString(SHA256.HashData(reader.ReadBytes(checked((int)size))));
            reader.BaseStream.Position += size + (size & 1);
        }
        throw new InvalidDataException("Missing rendered PCM.");
    }
}
