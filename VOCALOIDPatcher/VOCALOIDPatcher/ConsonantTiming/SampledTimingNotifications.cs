using System;
using System.Linq;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class SampledTimingHost
{
    internal static nint BeforeControllerRemoval(WIVSMMidiPart part, WIVSMMidiController controller)
    {
        if (Entries.IsEmpty || controller == null || part.Sequence is not { } source ||
            !Entries.TryGetValue((nint)source, out var entry) || entry.Rebuilding) return 0;
        entry.InputScope.BeforeControllerRemoval((nint)controller, (nint)part);
        return (nint)source;
    }

    internal static void FailedControllerRemoval(nint source, WIVSMMidiController controller)
    {
        if (Entries.TryGetValue(source, out var entry)) entry.InputScope.FailedControllerRemoval((nint)controller);
    }

    internal static void EndInputMutation(WIVSMSequence source)
    {
        if (Entries.TryGetValue((nint)source, out var entry)) entry.InputScope.EndMutation();
    }

    internal static void RegisterShiftInputChanged(WIVSMSequence source, WIVSMMidiPart part)
    {
        if (!Entries.TryGetValue((nint)source, out var entry) || entry.Rebuilding) return;
        // REG is patch-owned input, so no VSM note notification precedes it.
        // Retire old timing first, then publish the selector before any queued
        // timing rebuild can restart native rendering with the new selection.
        BeforeSourceChange(source);
        RegisterShift.RegisterShiftService.PublishPart(source, part);
        entry.NativeDirtyParts.Add((nint)part);
        SourceChanged(source);
    }
    // Native Undo can restore a Part's completed wave without asking the hash
    // hook again. Track actual edited Parts so only those re-enter cache lookup.
    private static void ObserveNativeInputs(Entry entry)
    {
        var observer = new WIVSMUpdateObserver();
        observer.Notified += update =>
        {
            try
            {
                if (entry.Rebuilding || TimingCacheInvalidationScope.IsActive((nint)entry.Source) ||
                    update.NotifyType is VSMNotifyType.DidUpdateWaveFile or
                    VSMNotifyType.DidUpdatePhonemePosition or
                    VSMNotifyType.DidUpdateName or VSMNotifyType.DidUpdateColor or VSMNotifyType.DidUpdateStyleName or
                    VSMNotifyType.DidUpdateStylePresetID) return;
                entry.InputNotices.Add(update.ObjectsType.Name + ":" + update.NotifyType);
                foreach (object value in update.Objects)
                {
                    if (value is WIVSMMidiController removedController && update.NotifyType == VSMNotifyType.DidRemove)
                    {
                        entry.InputScope.ControllerRemoved((nint)removedController,
                            entry.Source.MidiTracks.SelectMany(t => t.MidiParts).Select(p => (nint)p));
                        continue;
                    }
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
