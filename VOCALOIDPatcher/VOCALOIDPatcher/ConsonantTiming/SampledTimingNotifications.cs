using System;
using System.Linq;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class SampledTimingHost
{
    // Native Undo can restore a Part's completed wave without asking the hash
    // hook again. Track actual edited Parts so only those re-enter cache lookup.
    private static void ObserveNativeInputs(Entry entry)
    {
        var observer = new WIVSMUpdateObserver();
        observer.Notified += update =>
        {
            try
            {
                if (entry.Rebuilding || update.NotifyType is VSMNotifyType.DidUpdateWaveFile or
                    VSMNotifyType.DidUpdatePhonemePosition or
                    VSMNotifyType.DidUpdateName or VSMNotifyType.DidUpdateColor or VSMNotifyType.DidUpdateStyleName or
                    VSMNotifyType.DidUpdateStylePresetID) return;
                entry.InputNotices.Add(update.ObjectsType.Name + ":" + update.NotifyType);
                foreach (object value in update.Objects)
                {
                    // Removed notes remain represented by the last committed graph;
                    // never call native Parent on an object that has been removed.
                    if (value is WIVSMNote removed && update.NotifyType == VSMNotifyType.DidRemove)
                    {
                        var old = entry.LastNodes.FirstOrDefault(n => n.Key.Handle == removed.CppObjPtr);
                        if (old != null && PartFor(entry.Source, old.Address) is { } previous) entry.NativeDirtyParts.Add((nint)previous);
                        continue;
                    }
                    WIVSMMidiPart? part = value switch
                    {
                        WIVSMMidiPart midi => midi,
                        WIVSMNote note => note.Parent,
                        WIVSMMidiController controller when update.NotifyType != VSMNotifyType.DidRemove => controller.Parent,
                        WIVSMVibratoEvent vibrato when update.NotifyType != VSMNotifyType.DidRemove => vibrato.Parent?.Parent,
                        WIVSMDvqm dvqm when update.NotifyType != VSMNotifyType.DidRemove => dvqm.Parent?.Parent,
                        _ => null
                    };
                    if (part != null) { entry.NativeDirtyParts.Add((nint)part); continue; }
                    if (value is WIVSMSequence or WIVSMTempo or WIVSMTimeSig || update.IsMidiEffectType ||
                        value is WIVSMEffectManager or WIVSMEffectValue or WIVSMMidiController or WIVSMVibratoEvent or WIVSMDvqm)
                    {
                        // Sequence tempo/tuning and unknown effect ownership can
                        // affect every Part. This is an actual input notification,
                        // never a response to a cache-validity flag or render event.
                        foreach (var current in entry.Source.MidiTracks.SelectMany(t => t.MidiParts))
                            entry.NativeDirtyParts.Add((nint)current);
                    }
                }
            }
            catch (Exception error) { entry.InputObserverFault = error; }
        };
        if (!entry.Source.AddUpdateObserver(observer)) { observer.Dispose(); throw new InvalidOperationException("Cannot observe timing input changes."); }
        entry.InputObserver = observer;
    }
}
