using Yamaha.VOCALOID.VSM;
using VOCALOIDPatcher.Evec;

internal static partial class Program
{
    private static int ProbeRenderCopy(WIVSMSequenceManager manager, string directory)
    {
        var source = manager.CreateSequence(new VSMSequenceData
        {
            SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 16,
        }) ?? throw new InvalidOperationException("Cannot create copy probe sequence.");
        try
        {
            source.InterpolatedMasterVolume();
            source.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
            source.InsertTempo(VSMRelTick.Zero, 12000);
            var track = source.InsertTrackEx(0, VSMTrackType.Midi, "EVEC copy") as WIVSMMidiTrack;
            var part = track?.InsertPart(new VSMAbsTick(480), new VSMRelTick(7680), "EVEC copy")
                ?? throw new InvalidOperationException("Cannot create copy probe part.");
            if (!part.SetVoiceBankID("BCNFCY43LB2LZCD4")) return 70;
            var state = new EvecNoteState(EvecConstants.VoiceColorSoft, EvecConstants.AttackMild,
                EvecConstants.ReleaseBreathShort, 2);
            var expression = part.GetDefaultNoteExpression();
            expression.Accent = 80;
            expression.Decay = 20;
            expression.RisePort = true;
            var original = part.InsertNote(new VSMRelTick(240), new VSMNoteEvent(960, 60, 32),
                expression, part.GetDefaultAiNoteExpression(), "て",
                EvecPhonemeRecomposer.Recompose("t e", state), true, 0)!;
            original.IsProtected = true;
            original.ResetAiVibratoDefault();
            if (!source.Commit(false)) return 71;
            string before = Snapshot(original);
            bool couldUndo = source.CanUndo();
            var capabilities = EvecVoicebankDetector.GetDatabaseCapabilities("MIKU_V4X_Original_EVEC");
            if (!EvecRenderCopy.TryCreate(manager, part, capabilities, _ => state, out var renderCopy, out string? error))
                throw new InvalidOperationException(error ?? "No copy created.");
            using (renderCopy!)
            {
                var notes = renderCopy!.Part.Notes.ToArray();
                if (notes.Length != 3 || notes[0].Phonemes != "t t t t#2 e" ||
                    notes[1].Phonemes != "e#2" || notes[2].Phonemes != "*#1" ||
                    notes[0].NoteVelocity != 32 || notes[1].NoteVelocity != 64 || notes[2].NoteVelocity != 64 ||
                    notes[0].GetNoteExpression().Accent != 80 || notes[1].GetNoteExpression().Accent != 50 ||
                    notes[1].GetNoteExpression().RisePort || notes.Any(n => !n.IsProtected) ||
                    notes[^1].AbsEndTick != original.AbsEndTick)
                    throw new InvalidOperationException("Render copy lost event timing or expression.");
                RenderTransitionWithScore(renderCopy.Sequence, renderCopy.Part,
                    System.IO.Path.Combine(directory, "render-copy.wav"));
                using var score = renderCopy.Part.HoldingScoreList;
                if (score == null || score.NumScores <= 0)
                    throw new InvalidOperationException("Copy render has no score.");
            }
            if (part.NumNotes != 1 || Snapshot(original) != before || source.IsStaged ||
                source.CanUndo() != couldUndo)
                throw new InvalidOperationException("Source changed while rendering a copy.");
            var next = part.InsertNote(new VSMRelTick(1200), new VSMNoteEvent(480, 64, 64),
                expression, part.GetDefaultAiNoteExpression(), "て", "t e", true, 0);
            if (next == null || !source.Commit(false)) return 72;
            if (!EvecRenderCopy.TryCreate(manager, part, capabilities,
                    n => n.RelPosTick.Value == 240 ? state : EvecNoteState.Empty, out var connected, out error))
                throw new InvalidOperationException(error ?? "Cannot create connected copy.");
            using (connected!)
            {
                if (connected!.Part.NumNotes != 3 || connected.Part.Notes.Any(n => n.Phonemes.Contains("*#")))
                    throw new InvalidOperationException("Connected release was not suppressed in render copy.");
            }
            original.IsProtected = false;
            original.SetPhonemes("t e s", true, 0);
            source.Commit(false);
            before = Snapshot(original);
            if (EvecRenderCopy.TryCreate(manager, part, capabilities, _ => state, out var invalid, out error))
            {
                invalid?.Dispose();
                throw new InvalidOperationException("Unsupported coda entered render copy.");
            }
            if (Snapshot(original) != before || source.IsStaged)
                throw new InvalidOperationException("Failed copy modified source.");
            Console.WriteLine("render-copy.source-preserved=True;segments=3;score=True;nonzero-part-offset=True");
            Console.WriteLine("render-copy.connected-release-suppressed=True;unsupported-coda-fallback=True");
            return 0;
        }
        finally { source.Close(); }
    }

    private static string Snapshot(WIVSMNote note) =>
        $"{note.CppObjPtr}:{note.RelPosTick.Value}:{note.DurationTick.Value}:{note.Phonemes}:" +
        $"{note.NoteNumber}:{note.NoteVelocity}:{note.IsProtected}:{note.VibratoDurationTick.Value}:" +
        $"{note.GetNoteExpression().Accent}:{note.GetNoteExpression().Decay}";
}
