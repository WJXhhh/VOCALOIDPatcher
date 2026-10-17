using System;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private static int ProbePhonemeTiming(WIVSMSequenceManager manager)
    {
        WIVSMSequence? wIVSMSequence = manager.CreateSequence(new VSMSequenceData
        {
            SamplingRate = VSMSamplingRate._44100,
            MaxNumTracks = 4uL,
            MaxUndoCount = 0uL
        });
        if (wIVSMSequence == null)
        {
            return 40;
        }
        try
        {
            if (wIVSMSequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4)) == null || wIVSMSequence.InsertTempo(VSMRelTick.Zero, WIVSMTempo.DefaultValue) == null)
            {
                return 41;
            }
            bool flag = true;
            VSMTrackType[] array = new VSMTrackType[2]
            {
                VSMTrackType.Midi,
                VSMTrackType.MidiAi
            };
            foreach (VSMTrackType vSMTrackType in array)
            {
                WIVSMMidiPart? wIVSMMidiPart = ((wIVSMSequence.InsertTrackEx((ulong)(int)((vSMTrackType != VSMTrackType.Midi) ? 1u : 0u), vSMTrackType, "Timing capability") is WIVSMMidiTrack wIVSMMidiTrack) ? wIVSMMidiTrack.InsertPart(new VSMAbsTick(0L), new VSMRelTick(1920L), "Timing capability") : null);
                WIVSMNote? wIVSMNote = wIVSMMidiPart?.InsertNote(new VSMRelTick(240L), new VSMNoteEvent(960, 60, 64), wIVSMMidiPart.GetDefaultNoteExpression(), wIVSMMidiPart.GetDefaultAiNoteExpression(), "a", "a a#2 *#1", isValidPhonemes: true, 0);
                if (wIVSMNote == null || !wIVSMSequence.Commit(updateHistory: false))
                {
                    return 42;
                }
                int count = wIVSMNote.GetPhonemePositions().Count;
                bool flag2 = wIVSMNote.SetEditedPhonemePosition(1, new VSMRelTick(43L));
                VSMResult lastError = wIVSMNote.LastError;
                wIVSMSequence.Rollback();
                bool flag3 = wIVSMNote.ResetEditedPhonemePosition(1);
                VSMResult lastError2 = wIVSMNote.LastError;
                wIVSMSequence.Rollback();
                bool flag4 = ((vSMTrackType == VSMTrackType.Midi) ? (!flag2 && lastError == VSMResult.InvalidTrackType && !flag3 && lastError2 == VSMResult.InvalidTrackType) : (lastError != VSMResult.InvalidTrackType && lastError2 != VSMResult.InvalidTrackType));
                flag = flag && flag4;
                Console.WriteLine($"timing_capability.{vSMTrackType}=positions:{count};write:{flag2};write_error:{lastError};reset:{flag3};reset_error:{lastError2};passed:{flag4}");
            }
            Console.WriteLine($"timing_capability.valid={flag}");
            Console.WriteLine("timing_capability.acoustic_validation=False");
            return (!flag) ? 43 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("timing_capability.exception=" + ex.GetType().Name + ": " + ex.Message);
            return 44;
        }
        finally
        {
            wIVSMSequence.Close();
        }
    }

}

