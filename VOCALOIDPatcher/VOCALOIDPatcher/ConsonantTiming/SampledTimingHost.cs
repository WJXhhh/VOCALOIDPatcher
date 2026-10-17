using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VOCALOIDPatcher.Utils;
using VOCALOIDPatcher.DurationSelection;
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Dispatcher-owned project sessions. The native side receives only immutable
// numerical snapshots, never managed callbacks or live note wrappers.
internal static partial class SampledTimingHost
{
    private sealed class Entry(WIVSMSequence source, LocalConsonantTimingBackend? backend = null)
    {
        internal readonly WIVSMSequence Source = source;
        // An injected backend is owned here as before; the normal path allocates
        // no native resources until a query actually requires them.
        internal readonly SampledTimingSession Session = backend != null ? new(source, backend, ownsBackend: true) :
            new(source, () => new LocalConsonantTimingBackend(App.SequenceManager!, App.DSEManager!, App.DatabaseManager!));
        internal IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan> Previous = new Dictionary<TimingNoteAddress, SampledTimingPlan>();
        internal readonly HashSet<TimingNoteAddress> Pending = new();
        internal readonly HashSet<nint> PublishedParts = new();
        internal long PublishedRevision = -1;
        internal long SuspendedRevision = -1;
        internal TimingNoteAddress[] DurationPrevious = [];
        internal readonly Dictionary<TimingNoteAddress, DurationNoteKey> Bindings = new();
        internal NativeNode[] LastNodes = [];
        internal NativeNode[]? BeforeNativeCommit;
        internal readonly Dictionary<DurationNoteKey, DurationNoteKey> NativeAliases = new();
        internal readonly Dictionary<nint, (string Input, NativeDurationSelection.TimeEvent[] Events)> DurationContracts = new();
        internal WIVSMUpdateObserver? InputObserver;
        internal Exception? InputObserverFault;
        internal readonly HashSet<nint> NativeDirtyParts = new();
        internal readonly HashSet<string> InputNotices = new();
        internal string[] LastInputNotices = [];
        internal int DurationContractHits, DurationContractMisses;
        internal bool Loading, Queued, Rebuilding, NativeRestart;
        internal bool AttachedDuringCopy;
        internal Exception? Fault;
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (long Revision, int[] Boundaries)> Boundaries = new();
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Entry> Entries = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (Entry Entry, WIVSMMidiPart Part)> RenderParts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan>> Copies = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Exception> CopyFaults = new();
    private static readonly Dictionary<nint, HashSet<DurationNoteKey>> CopiedTargets = new();
    private static long _epoch;

    private static Entry Get(WIVSMSequence source)
    {
        if (Entries.TryGetValue((nint)source, out var existing)) return existing;
        if (NativeRenderSession.IsInternalSequence(source)) throw new NotSupportedException("Internal render source cannot own timing controls.");
        return AttachEntry(source);
    }
    private static Entry AttachEntry(WIVSMSequence source, LocalConsonantTimingBackend? backend = null)
    {
        var entry = new Entry(source, backend);
        if (!Entries.TryAdd((nint)source, entry))
        {
            entry.Session.Dispose();
            return Entries[(nint)source];
        }
        try
        {
            entry.Session.Changed += () => Changed(entry);
            entry.LastNodes = CaptureNodes(source, []);
            // Observation is an execution capability, not a prerequisite for
            // retaining project data on a different managed SDK build.
            try { ObserveNativeInputs(entry); }
            catch (Exception error) { entry.InputObserverFault = error; }
            entry.Session.AllowsNote = pointer => entry.Bindings.Values.Any(k => k.Handle == pointer &&
                BreathVolume.BreathVolumeService.IsNoteGenerationActive(k.Handle, k.Generation));
            return entry;
        }
        catch
        {
            Entries.TryRemove((nint)source, out _);
            if (entry.InputObserver != null) { source.RemoveUpdateObserver(entry.InputObserver); entry.InputObserver.Dispose(); }
            entry.Session.Dispose(); throw;
        }
    }
    internal static SampledTimingSession Attach(WIVSMSequence source, LocalConsonantTimingBackend backend) => AttachEntry(source, backend).Session;

    internal static void SetCoefficient(WIVSMNote note, float? coefficient)
    {
        var entry = Get(note.Sequence!);
        // Validate before creating a history entry or stopping an existing task.
        var adjustments = entry.Session.Document.Get(SampledTimingVsmBinding.Address(note))?.Adjustments
            .Where(a => a.Kind != TimingAdjustment.NoteCoefficient).ToList() ?? [];
        if (coefficient.HasValue) adjustments.Add(new(TimingAdjustment.NoteCoefficient, -1, coefficient.Value));
        var plan = new SampledTimingPlan(note.Phonemes, adjustments);
        if (entry.Session.Document.Get(SampledTimingVsmBinding.Address(note))?.Fingerprint ==
            (adjustments.Count == 0 ? null : plan.Fingerprint)) return;
        if (plan.HasPhonemeControls) NativePhonemeTiming.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        using (var prepared = entry.Session.Prepare(note))
            _ = prepared.Query(plan, entry.Session.Revision);
        NativeSampledTiming.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        entry.Source.StopAsyncRendering();
        entry.Session.SetCoefficient(note, coefficient);
    }

    internal static SampledTimingSession? Find(WIVSMSequence source) => Entries.GetValueOrDefault((nint)source)?.Session;
    internal static SampledTimingSession ForDuration(WIVSMSequence source) => Get(source).Session;
    internal static void ForCopiedDuration(WIVSMNote target)
    {
        var source = target.Sequence ?? throw new InvalidOperationException("Copied note has no source.");
        bool existed = Entries.ContainsKey((nint)source);
        var entry = Get(source);
        if (!existed && source.IsStaged) entry.AttachedDuringCopy = true;
        if (!entry.AttachedDuringCopy) return;
        // This session can first appear after native insertion but before the
        // paste transaction commits. Its before-graph excludes copied targets,
        // including the remaining notes of a whole-Part copy.
        var keys = CopiedTargets.GetValueOrDefault((nint)source) ?? [Key(target)];
        entry.LastNodes = entry.LastNodes.Where(n => !keys.Contains(n.Key)).ToArray();
        CopiedTargets.Remove((nint)source);
    }
    internal static void TrackCopiedTarget(WIVSMNote target)
    {
        if (target.Sequence is not { } source || NativeRenderSession.IsInternalSequence(source)) return;
        var key = Key(target);
        if (Entries.TryGetValue((nint)source, out var entry))
        {
            if (entry.AttachedDuringCopy) entry.LastNodes = entry.LastNodes.Where(n => n.Key != key).ToArray();
            return;
        }
        if (!CopiedTargets.TryGetValue((nint)source, out var keys)) CopiedTargets[(nint)source] = keys = new();
        keys.Add(key);
    }
    internal static void ValidateDurationPart(WIVSMMidiPart part)
    {
        _ = DurationContract(Get(part.Sequence!), part);
    }
    private static NativeDurationSelection.TimeEvent[] DurationContract(Entry entry, WIVSMMidiPart part)
    {
        string input = LocalConsonantTimingBackend.DurationInputKey(part);
        if (entry.DurationContracts.TryGetValue((nint)part, out var hit) && hit.Input == input) { entry.DurationContractHits++; return hit.Events; }
        if (part.NumNotes > 33) throw new NotSupportedException("Independent duration currently requires a complete Part of at most 33 notes.");
        // Center the adaptive window: a 33-note Part needs sixteen notes on
        // either side, rather than asking a first-note window to reach 32.
        using var prepared = entry.Session.Prepare(part.GetNote(part.NumNotes / 2) ?? throw new NotSupportedException("Empty duration Part."));
        var result = prepared.CaptureDurationContract(NativeDurationSelection.Notes(part, new Dictionary<nint, (float, uint)>(), true), entry.Session.Revision);
        entry.DurationContractMisses++; entry.DurationContracts[(nint)part] = (input, result); return result;
    }
    internal static void DurationChanged(WIVSMNote note)
    {
        var entry = Get(note.Sequence!);
        entry.Source.StopAsyncRendering();
        entry.Pending.Add(SampledTimingVsmBinding.Address(note));
        entry.Session.SelectionChanged();
    }
    internal static SampledTimingSession ForPhonemeEditing(WIVSMSequence source)
    {
        string path = Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll");
        NativePhonemeTiming.Initialize(path); NativeSampledTiming.Initialize(path);
        return Get(source).Session;
    }

    internal static void SetPhonemePlan(WIVSMNote note, SampledTimingPlan? plan, int? editedBoundary = null)
    {
        var session = ForPhonemeEditing(note.Sequence!);
        if (editedBoundary == null && session.Document.Get(SampledTimingVsmBinding.Address(note))?.Fingerprint == plan?.Fingerprint) return;
        IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan?>? related = null;
        using (var prepared = session.Prepare(note))
        {
            if (editedBoundary.HasValue)
                related = prepared.QueryPhonemes(plan ?? new(note.Phonemes, []), session.Revision, editedBoundary: editedBoundary).Plans;
            else _ = prepared.Query(plan ?? new(note.Phonemes, []), session.Revision);
        }
        note.Sequence!.StopAsyncRendering(); session.SetPlan(note, plan, related);
    }

    internal static List<int>? VerifiedPositions(WIVSMNote note)
    {
        if (note.Sequence is not { } source || !Entries.TryGetValue((nint)source, out var entry) ||
            !entry.Boundaries.TryGetValue((nint)note, out var value) || value.Revision != entry.Session.Revision) return null;
        return value.Boundaries.ToList();
    }
    internal static bool IsDirty(WIVSMSequence source) => Find(source)?.Document.IsDirty == true || DurationSelectionHost.IsDirty(source);

    internal static object? Diagnostics(WIVSMSequence? source)
    {
        if (source == null || !Entries.TryGetValue((nint)source,out var entry)) return null;
        return new {
            revision=entry.Session.Revision, suspended=entry.SuspendedRevision==entry.Session.Revision,
            fault=entry.Fault?.GetType().Name, reason=entry.Fault?.Message,
            storedPlans=entry.Session.Document.Snapshot().Count,
            activePlans=SampledTimingVsmBinding.ResolveActivePlans(source,entry.Session.Document).Count,
            nativeParts=entry.PublishedParts.Count,pending=entry.Pending.Count,
            durationContractHits=entry.DurationContractHits,durationContractMisses=entry.DurationContractMisses,
            backendCreated=entry.Session.HasBackend, backendFault=entry.Session.BackendFault?.Message,
            compatibility=LocalTimingNative.Status,
            capabilities=new { projectData=true, planner=entry.Session.HasBackend,
                routing=NativeSampledTiming.IsInitialized, phonemeMapping=NativePhonemeTiming.IsInitialized,
                independentDuration=NativeDurationSelection.IsInitialized },
            inputNotices=entry.LastInputNotices
        };
    }

    private static void Changed(Entry entry)
    {
        BindCurrentPlans(entry);
        entry.Boundaries.Clear();
        ConsonantTimingOverlay.SourceChanged(entry.Source);
        var current = entry.Session.Document.Snapshot();
        foreach (var address in entry.Previous.Keys.Concat(current.Keys).Distinct())
            if (entry.Previous.GetValueOrDefault(address)?.Fingerprint != current.GetValueOrDefault(address)?.Fingerprint)
            {
                if (PartFor(entry.Source, address) is { } part) entry.DurationContracts.Remove((nint)part);
                if (!entry.Loading) entry.Pending.Add(address);
            }
        entry.Previous = current;
        if (entry.Loading || entry.Rebuilding || entry.Queued || (entry.Pending.Count == 0 && !entry.NativeRestart) || NativeRenderSession.IsInternalSequence(entry.Source)) return;
        entry.Queued = true;
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Timing edits require the Editor Dispatcher.");
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            entry.Queued = false;
            if (!Entries.TryGetValue((nint)entry.Source, out var live) || live != entry || !entry.Source.IsOpen) return;
            try { Flush(entry); }
            catch (Exception e) { Recover(entry.Source,e,true); }
        }));
    }

    private static void Flush(Entry entry, bool startRendering = true)
    {
        if (entry.Rebuilding || (entry.Pending.Count == 0 && !entry.NativeRestart)) return;
        entry.Rebuilding = true;
        try
        {
            entry.Source.StopAsyncRendering();
            var affected = entry.Pending.ToArray();
            InvalidateTargets(entry,affected);
            entry.Pending.Clear(); entry.NativeRestart = false; entry.Fault = null;
            Publish(entry);
            // CanAsyncRendering reports the service's current enabled state;
            // StopAsyncRendering above sets it false. It is not permission to
            // restart, and using it as a guard permanently disables rendering.
            if (startRendering && entry.Source.IsOpen && !entry.Source.IsStaged) entry.Source.StartAsyncRendering();
        }
        finally { entry.Rebuilding = false; }
    }

    private static void InvalidateTargets(Entry entry,IEnumerable<TimingNoteAddress> affected)
    {
        foreach (var address in affected)
            {
                var part = entry.Source.GetTrack((ulong)address.Track) is WIVSMMidiTrack track && (ulong)address.Part < track.NumParts
                    ? track.GetPart((ulong)address.Part) as WIVSMMidiPart : null;
                var target = part != null && (ulong)address.Note < part.NumNotes ? part.GetNote((ulong)address.Note) : null;
                if (part == null || target == null || target.Phonemes != address.Topology || target.RelPosTick.Value != address.Position ||
                    target.NoteNumber != address.Pitch || target.DurationTick.Value != address.Duration || part.VoiceBankID != address.Bank)
                    continue; // Deleted/moved notes must not prevent native rendering.
                var notes = new List<WIVSMNote> { target };
                var before = target.Prev; var after = target.Next;
                for (int i = 0; i < 16; i++)
                {
                    if (before != null) { notes.Add(before); before = before.Prev; }
                    if (after != null) { notes.Add(after); after = after.Next; }
                }
                SampledTimingVsmBinding.InvalidateRenderedPart(part, notes);
                RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
            }
    }

    internal static void BeforeRendering(WIVSMSequence source)
    {
        if (CopyFaults.TryGetValue((nint)source, out var copyError)) throw new NotSupportedException("Render-copy controls could not be prepared.", copyError);
        DurationSelectionHost.ValidateCopy(source);
        if (DurationSelectionHost.HasUnbound(source) || PhonemeVolume.PhonemeVolumeHost.HasUnbound(source))
            throw new NotSupportedException("Unbound project controls are preserved but cannot render against a different note identity.");
        if (Copies.TryGetValue((nint)source, out var copiedPlans) &&
            (!Entries.TryGetValue((nint)source, out var copiedEntry) ||
            SampledTimingVsmBinding.ResolveActivePlans(source, copiedEntry.Session.Document).Count != copiedPlans.Count))
            throw new NotSupportedException("The render copy changed a timing-controlled note's identity or phoneme topology.");
        if (!Entries.TryGetValue((nint)source, out var entry)) return;
        if (entry.Session.Document.Snapshot().Keys.Any(a => !entry.Bindings.ContainsKey(a)))
            throw new NotSupportedException("Unbound timing project controls are preserved but cannot render against a different note identity.");
        if (entry.InputObserverFault != null) throw new InvalidOperationException("Native input ownership notification failed.", entry.InputObserverFault);
        if (entry.SuspendedRevision == entry.Session.Revision)
        {
            throw new InvalidOperationException("Custom timing or duration rendering is suspended; project controls are preserved.", entry.Fault);
        }
        if (!entry.Rebuilding && (entry.Pending.Count != 0 || entry.NativeRestart)) { Flush(entry, false); return; }
        Publish(entry);
    }

    // Keep project data intact. A controlled render must never be presented as
    // successful after its controls were skipped. Plain native songs can render.
    internal static bool Recover(WIVSMSequence source,Exception error,bool requestRender=false)
    {
        if (!Entries.TryGetValue((nint)source,out var entry))
        {
            if (DurationSelectionHost.HasState(source) || CopyFaults.ContainsKey((nint)source) || PhonemeVolume.PhonemeVolumeHost.HasUnbound(source))
            {
                Debug.Print("Custom controls render rejected: " + error.GetType().Name);
                return false;
            }
            Debug.Print("Custom timing unavailable for this render source; native rendering retained: "+error.GetType().Name);
            return true;
        }
        if (DurationSelectionHost.HasState(source) || PhonemeVolume.PhonemeVolumeHost.HasUnbound(source) ||
            entry.Session.Document.Snapshot().Count != 0)
        {
            source.StopAsyncRendering(); Detach(entry); entry.Boundaries.Clear(); entry.Fault = error; entry.SuspendedRevision = entry.Session.Revision;
            DurationSelectionHost.RenderFailed(source, error);
            ConsonantTimingOverlay.SourceChanged(source);
            Debug.Print("Custom controls render rejected; document retained: " + error.GetType().Name);
            return false;
        }
        bool wasRebuilding=entry.Rebuilding; entry.Rebuilding=true;
        try
        {
            try { source.StopAsyncRendering(); }
            catch (Exception e) { Debug.Print("Timing stop recovery: "+e.GetType().Name); }
            Detach(entry);
            try { if(!source.IsStaged) InvalidateTargets(entry,entry.Session.Document.Snapshot().Keys); }
            catch (Exception e) { Debug.Print("Timing cache recovery: "+e.GetType().Name); }
            entry.Pending.Clear(); entry.Boundaries.Clear();
            entry.Fault=error;
            entry.SuspendedRevision=entry.Session.Revision;
            entry.PublishedRevision=entry.Session.Revision;
            ConsonantTimingOverlay.SourceChanged(source);
            Debug.Print("Custom timing suspended; native rendering retained: "+error.GetType().Name);
        }
        finally { entry.Rebuilding=wasRebuilding; }
        if(requestRender)
        {
            var dispatcher=Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(DispatcherPriority.ContextIdle,new Action(() => {
                if (Entries.TryGetValue((nint)source,out var live) && live==entry && source.IsOpen && !source.IsStaged)
                    source.StartAsyncRendering();
            }));
        }
        return true;
    }

    private static void Detach(Entry entry)
    {
        foreach(nint part in entry.PublishedParts)
        { RenderParts.TryRemove(part,out _); NativeSampledTiming.Remove(part); NativeDurationSelection.Remove(part); }
        entry.PublishedParts.Clear(); entry.Boundaries.Clear();
    }

    // The native cache key includes the immutable numerical timing controls.
    // A cache hit is valid without DSE synthesis, provided that key was used.
    internal static bool AcceptRenderCompletion(nint pointer)
    {
        if (!RenderParts.TryGetValue(pointer, out var route)) return true;
        var entry = route.Entry; var part = route.Part; var source = entry.Source;
        if (DurationSelectionHost.HasScope(part) && NativeDurationSelection.ReadStatus((nint)part).Failures != 0)
            return Recover(source, new InvalidOperationException("Independent duration render failed: " + NativeDurationSelection.ReadStatus((nint)part)), true);
        var status = NativeSampledTiming.ReadStatus((nint)part);
        if (status.Failures != 0)
            return Recover(source,new InvalidOperationException(
                $"Native timing render failed (epoch={status.Epoch}, scopes={status.Scopes}, applied={status.Applied}, failures={status.Failures}; {NativeSampledTiming.PhonemeDiagnostic((nint)part)})."),true);
        if (status.Scopes != 0 || (status.CacheKeys != 0 && part.HasValidRenderedWave && part.HasValidRenderedScore))
        { DurationSelectionHost.RenderSucceeded(source); return true; }
        return Recover(source,new InvalidOperationException($"Timing render bypassed its native cache identity (epoch={status.Epoch}, scopes={status.Scopes}, keys={status.CacheKeys}, finished={source.IsFinishedRendering}, wave={part.HasValidRenderedWave}, score={part.HasValidRenderedScore})."),true);
    }

    private static void Publish(Entry entry)
    {
        if (entry.PublishedRevision == entry.Session.Revision) return;
        var plans = SampledTimingVsmBinding.ResolveActivePlans(entry.Source, entry.Session.Document)
            .Where(p => entry.Session.AllowsNote?.Invoke(p.Key) != false).ToDictionary(p => p.Key, p => p.Value);
        var values=plans.Where(p=>p.Value.PhonemeCoefficient(p.Value.Topology).HasValue)
            .ToDictionary(p=>p.Key,p=>p.Value.PhonemeCoefficient(p.Value.Topology)!.Value);
        var durationAddresses = DurationSelectionHost.Snapshot(entry.Source).Select(e => e.Address).ToArray();
        if(plans.Count==0 && durationAddresses.Length==0) { Detach(entry); entry.PublishedRevision=entry.Session.Revision; return; }
        entry.Source.StopAsyncRendering();
        if (plans.Count != 0) NativeSampledTiming.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
        var parts = new HashSet<nint>();
        var geometry=new Dictionary<nint,int[]>();
        for(ulong t=0;t<entry.Source.NumTrack;t++)
        foreach(var part in entry.Source.GetTrack(t) is WIVSMMidiTrack track ? track.PartsEnumerator.OfType<WIVSMMidiPart>() : [])
        {
            var duration = DurationSelectionHost.Controls(part);
            bool durationScope = DurationSelectionHost.HasScope(part);
            if(!part.NotesEnumerator.Any(n=>plans.ContainsKey((nint)n)) && !durationScope) continue;
            if (!parts.Add((nint)part)) continue;
            var anchors = new SortedDictionary<int, int>();
            foreach (var note in part.NotesEnumerator)
            {
                if (!plans.TryGetValue((nint)note, out var plan) || !plan.HasPhonemeControls) continue;
                NativePhonemeTiming.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
                using var prepared = entry.Session.Prepare(note);
                var layout = prepared.QueryPhonemes(plan, entry.Session.Revision);
                geometry[(nint)note]=layout.Result.Boundaries.Select(v=>checked((int)v)).ToArray();
                foreach (var point in layout.Anchors)
                {
                    if (anchors.TryGetValue(point.From, out int previous) && previous != point.To)
                    {
                        if (point.From == point.To) continue;
                        if (previous != point.From) throw new NotSupportedException("Conflicting Part phoneme controls.");
                    }
                    anchors[point.From] = point.To;
                }
            }
            var epoch = checked((ulong)Interlocked.Increment(ref _epoch));
            if (durationScope)
            {
                NativeDurationSelection.Initialize(Path.Combine(Patcher.DataDir, "native", "v6patch_clock.dll"));
                var notes = NativeDurationSelection.Notes(part, duration, true);
                var contract = DurationContract(entry, part);
                NativeDurationSelection.Publish((nint)part, epoch, notes, contract);
            }
            else NativeDurationSelection.Remove((nint)part);
            NativeSampledTiming.Publish(part, epoch, values,
                anchors.Count == 0 ? null : anchors.Select(p => new NativePhonemeTiming.Point(p.Key, p.Value)).ToArray());
            RenderParts[(nint)part] = (entry, part);
            entry.PublishedParts.Add((nint)part);
        }
        foreach (nint old in entry.PublishedParts.Except(parts))
        { RenderParts.TryRemove(old, out _); NativeSampledTiming.Remove(old); NativeDurationSelection.Remove(old); }
        entry.PublishedParts.Clear(); entry.PublishedParts.UnionWith(parts);
        entry.Boundaries.Clear();
        foreach(var pair in geometry) entry.Boundaries[pair.Key]=(entry.Session.Revision,pair.Value);
        entry.PublishedRevision = entry.Session.Revision;
        entry.DurationPrevious = durationAddresses;
    }

    internal static void BeforeSourceChange(WIVSMSequence source)
    {
        CopiedTargets.Remove((nint)source);
        if (!Entries.TryGetValue((nint)source, out var entry) || entry.Rebuilding) return;
        entry.BeforeNativeCommit = entry.LastNodes;
        entry.AttachedDuringCopy = false;
        if (entry.PublishedParts.Count == 0 && entry.Session.Document.Snapshot().Count == 0) return;
        // Commit can start native work before its managed postfix. Pause first
        // so an obsolete route cannot be consumed, nor the new completion be
        // discarded by stopping the service after it has already finished.
        source.StopAsyncRendering();
        Detach(entry);
    }

    internal static void SourceChanged(WIVSMSequence source)
    {
        if (Entries.TryGetValue((nint)source, out var entry))
        {
            // Flush commits its bounded cache invalidation after restoring
            // the original values. That internal commit is not another edit.
            if (entry.Rebuilding) return;
            RelocateCurrentPlans(entry);
            entry.LastNodes = CaptureNodes(source, entry.LastNodes);
            entry.Boundaries.Clear();
            var stored = entry.Session.Document.Snapshot();
            if (entry.PublishedParts.Count != 0 || stored.Count != 0 || entry.DurationPrevious.Length != 0 || DurationSelectionHost.Snapshot(source).Length != 0)
            {
                source.StopAsyncRendering();
                Detach(entry);
                // Native Commit normally wakes the already-running service;
                // it does not call managed StartAsyncRendering again. Queue
                // a fresh publication AND restart even when the document's
                // custom values did not change (VEL, move, delete, etc.).
                // Native commits already invalidate their own Part cache. Only
                // custom-only changes need a bounded native invalidation edit.
                // Other Parts keep their native key and reuse existing PCM.
                entry.NativeRestart = true;
                entry.Pending.UnionWith(stored.Keys.Concat(entry.DurationPrevious)
                    .Concat(DurationSelectionHost.Snapshot(source).Select(e => e.Address))
                    .Where(a => PartFor(source, a) is { } part && entry.NativeDirtyParts.Contains((nint)part)));
            }
            entry.NativeDirtyParts.Clear();
            entry.LastInputNotices = entry.InputNotices.ToArray(); entry.InputNotices.Clear();
            entry.Session.SourceChanged();
            // An undo can repair a formerly rejected source identity.
            entry.Fault = null;
            entry.SuspendedRevision=-1;
        }
    }

    private static WIVSMMidiPart? PartFor(WIVSMSequence source, TimingNoteAddress address)
    {
        var track = (ulong)address.Track < source.NumTrack ? source.GetTrack((ulong)address.Track) as WIVSMMidiTrack : null;
        return track != null && (ulong)address.Part < track.NumParts ? track.GetPart((ulong)address.Part) as WIVSMMidiPart : null;
    }

    internal static void Load(WIVSMSequence source, IReadOnlyDictionary<TimingNoteAddress, SampledTimingPlan>? data)
    {
        if (data == null || data.Count == 0) return;
        var entry = Get(source); entry.Loading = true;
        try { entry.Session.Document.Load(data); }
        finally { entry.Loading = false; }
        // BeforeRendering publishes before VSM checks its native cache. Its
        // cache key now distinguishes saved controls without a forced render.
    }

    private static DurationNoteKey Key(WIVSMNote note) => new(note.CppObjPtr, BreathVolume.BreathVolumeService.NoteGeneration(note));
    private static void BindCurrentPlans(Entry entry)
    {
        foreach (var pair in entry.Session.Document.Snapshot())
        {
            if (entry.Bindings.ContainsKey(pair.Key)) continue;
            var a = pair.Key;
            var track = (ulong)a.Track < entry.Source.NumTrack ? entry.Source.GetTrack((ulong)a.Track) as WIVSMMidiTrack : null;
            var part = track != null && (ulong)a.Part < track.NumParts ? track.GetPart((ulong)a.Part) as WIVSMMidiPart : null;
            var note = part != null && (ulong)a.Note < part.NumNotes ? part.GetNote((ulong)a.Note) : null;
            if (note != null && note.RelPosTick.Value == a.Position && note.DurationTick.Value == a.Duration &&
                note.NoteNumber == a.Pitch && part!.VoiceBankID == a.Bank && note.Phonemes == a.Topology)
                entry.Bindings.Add(a, Key(note));
        }
    }
    private static void RelocateCurrentPlans(Entry entry)
    {
        var live = new Dictionary<DurationNoteKey, TimingNoteAddress>();
        for (ulong t = 0; t < entry.Source.NumTrack; t++)
        if (entry.Source.GetTrack(t) is WIVSMMidiTrack track)
        for (ulong p = 0; p < track.NumParts; p++)
        if (track.GetPart(p) is WIVSMMidiPart part)
        for (ulong n = 0; n < part.NumNotes; n++)
        if (part.GetNote(n) is { } note && !string.IsNullOrWhiteSpace(note.Phonemes))
            live[Key(note)] = new((int)t, (int)p, (int)n, note.RelPosTick.Value, (int)note.DurationTick.Value, note.NoteNumber, part.VoiceBankID, note.Phonemes);
        var changes = entry.Bindings.Where(p => live.TryGetValue(p.Value, out var current) && current != p.Key && current.Topology == p.Key.Topology)
            .ToDictionary(p => p.Key, p => live[p.Value]);
        entry.Session.Document.Relocate(changes);
        var rebound = entry.Bindings.ToArray(); entry.Bindings.Clear();
        foreach (var pair in rebound) entry.Bindings[changes.GetValueOrDefault(pair.Key) ?? pair.Key] = pair.Value;
    }

    internal static void Save(WIVSMSequence source, string path, bool markSaved)
    {
        if (Copies.TryGetValue((nint)source, out var copied)) { SampledTimingProjectArchive.Write(path, copied); return; }
        if (!Entries.TryGetValue((nint)source, out var entry)) return;
        SampledTimingProjectArchive.Write(path, SavePlans(entry));
        if (markSaved) entry.Session.Document.MarkSaved();
    }
    internal static void MarkSaved(WIVSMSequence source) => Find(source)?.Document.MarkSaved();

    internal static void Close(WIVSMSequence source)
    {
        source.StopAsyncRendering();
        Close((nint)source);
    }

    // Native Close publishes the Part cache before releasing the sequence.
    // Preserve numerical routes until that publication has finished. The
    // managed wrapper clears its pointer on success, so capture it beforehand.
    internal static void Close(nint pointer)
    {
        CopiedTargets.Remove(pointer);
        DurationSelectionHost.Close(pointer);
        CopyFaults.TryRemove(pointer, out _);
        Copies.TryRemove(pointer, out _);
        if (!Entries.TryRemove(pointer, out var entry)) return;
        entry.InputObserver?.Dispose();
        foreach (nint part in entry.PublishedParts)
        { RenderParts.TryRemove(part, out _); NativeSampledTiming.Remove(part); NativeDurationSelection.Remove(part); }
        entry.Session.Dispose();
    }

    internal static bool HasData(WIVSMSequence source) => Entries.ContainsKey((nint)source) || Copies.ContainsKey((nint)source);
    internal static void EnsureForExport(WIVSMSequence source)
    {
        if (!Entries.TryGetValue((nint)source, out var entry)) return;
        if (entry.Session.Document.Snapshot().Count == 0 && DurationSelectionHost.Snapshot(source).Length == 0) return;
        bool pending = entry.Pending.Count != 0 || entry.NativeRestart || entry.PublishedRevision != entry.Session.Revision;
        BeforeRendering(source);
        if (!pending && entry.PublishedParts.All(p => RenderParts.TryGetValue(p, out var route) &&
            route.Part.HasValidRenderedWave && route.Part.HasValidRenderedScore) && source.IsFinishedRendering) return;
        source.StartAsyncRendering();
        throw new InvalidOperationException("Custom timing or duration audio is still updating.");
    }
    internal static void Copy(WIVSMSequence source, WIVSMSequence? copy)
    {
        if (copy == null) return;
        try
        {
            DurationSelectionHost.Copy(source, copy);
            PhonemeVolume.PhonemeVolumeHost.CopyUnbound(source, copy);
            var timing = Entries.TryGetValue((nint)source, out var original) ? SavePlans(original) : null;
            if (timing is { Count: > 0 }) Copies[(nint)copy] = timing;
            var duration = DurationSelectionHost.Snapshot(copy);
            if (timing?.Count is not > 0 && duration.Length == 0) return;
            var entry = AttachEntry(copy); entry.Loading = true;
            try
            {
                if (timing is { Count: > 0 }) entry.Session.Document.Load(timing);
                DurationSelectionHost.Load(copy, duration);
            }
            finally { entry.Loading = false; }
        }
        catch (Exception e) { CopyFaults[(nint)copy] = e; Debug.Print("Render-copy controls rejected: " + e.GetType().Name); }
    }
}
