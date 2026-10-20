using System;
using System.Linq;
using Yamaha.VOCALOID.VSM;
using VOCALOIDPatcher.DurationSelection;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class SampledTimingHost
{
    internal sealed class RenderVerificationException(string code, string message, object evidence) : InvalidOperationException(message)
    {
        internal string Code { get; } = code;
        internal object Evidence { get; } = evidence;
    }

    internal static bool HasNativeRenderFailure(nint part)
    {
        if (!RenderParts.ContainsKey(part)) return false;
        var state = ReadNativeSnapshot(part);
        return state.Failures != 0 || state.TransitionFailures != 0 || state.DurationFailures != 0;
    }

    internal static void RestartFailedRender(WIVSMSequence source, System.Collections.Generic.IReadOnlyList<WIVSMMidiPart> parts)
    {
        if (!source.IsFinishedRendering || source.IsStaged || parts.Any(p => (nint)p.Sequence! != (nint)source))
            throw new InvalidOperationException("Render recovery requires an idle owning sequence.");
        if (!Entries.TryGetValue((nint)source, out var entry))
        {
            foreach (var part in parts) SampledTimingVsmBinding.InvalidateRenderedPart(part, part.NotesEnumerator.Take(33));
            return;
        }
        bool wasRebuilding = entry.Rebuilding; entry.Rebuilding = true;
        try
        {
            foreach (var part in parts) RetainNativeSnapshot(entry, (nint)part);
            entry.Session.ResetRenderBackend();
            Detach(entry); entry.PublishedRevision = -1;
            entry.DurationContracts.Clear(); entry.Pending.Clear(); entry.NativeRestart = false;
            entry.Fault = null; entry.SuspendedRevision = -1;
            foreach (var part in parts) SampledTimingVsmBinding.InvalidateRenderedPart(part, part.NotesEnumerator.Take(33));
            Publish(entry);
        }
        catch (Exception error) { Recover(source, error); throw; }
        finally { entry.Rebuilding = wasRebuilding; }
    }

    // Read counters only at explicit diagnostic/publication boundaries. Do not
    // start a planner or synthesize audio to explain a failed render.
    private sealed record NativeRenderSnapshot(DateTime CapturedUtc, ulong Epoch, ulong Scopes,
        ulong Applied, ulong Failures, ulong CacheKeys, ulong TransitionCalls, ulong TransitionApplied,
        ulong TransitionFailures, ulong Primary, ulong Auxiliary, string Cursor, string? Error = null, ulong DurationFailures = 0, object? TransitionMismatch = null);
    private sealed record RenderCancellationSnapshot(DateTime DeliveredUtc, string Reason, NativeRenderSnapshot DeliveryState);

    internal static void ObserveRenderCancellation(nint part, VSMRenderCancelReason reason)
    {
        if (!RenderParts.TryGetValue(part, out var route)) return;
        // A delayed callback can belong to an earlier task. Report delivery
        // state explicitly; it does not identify the canceled task's epoch and
        // must never suspend or retire the currently published controls.
        route.Entry.LastCancellation = new(DateTime.UtcNow, reason.ToString(), ReadNativeSnapshot(part));
    }

    private static NativeRenderSnapshot ReadNativeSnapshot(nint part)
    {
        NativeSampledTiming.Status timing = default;
        NativeTransitionGain.Status transition = default;
        ulong durationFailures = 0;
        object? transitionMismatch = null;
        string cursor = "unavailable";
        var errors = new System.Collections.Generic.List<string>();
        try { timing = NativeSampledTiming.ReadStatus(part); cursor = NativeSampledTiming.PhonemeDiagnostic(part); }
        catch (Exception error) { errors.Add("timing:" + error.GetType().Name); }
        try
        {
            if (NativeTransitionGain.IsInitialized)
            {
                transition = NativeTransitionGain.ReadStatus(part);
                if (transition.Failures != 0) transitionMismatch = NativeTransitionGain.ReadFailure(part);
            }
        }
        catch (Exception error) { errors.Add("transition:" + error.GetType().Name); }
        try { if (NativeDurationSelection.TryReadStatus(part, out var duration)) durationFailures = duration.Failures; }
        catch (Exception error) { errors.Add("duration:" + error.GetType().Name); }
        return new(DateTime.UtcNow, timing.Epoch, timing.Scopes, timing.Applied, timing.Failures, timing.CacheKeys,
            transition.Calls, transition.Applied, transition.Failures, transition.Primary, transition.Auxiliary,
            cursor, errors.Count == 0 ? null : string.Join(";", errors), durationFailures, transitionMismatch);
    }

    private static void RetainNativeSnapshot(Entry entry, nint part)
    {
        var snapshot = ReadNativeSnapshot(part);
        if (snapshot.Epoch == 0 || snapshot.Scopes == 0 && snapshot.CacheKeys == 0 && snapshot.Failures == 0) return;
        // An unused replacement must not erase the last consumed/failed epoch.
        if (!entry.RetiredNative.ContainsKey(part) && entry.RetiredNative.Count >= 8)
            entry.RetiredNative.Remove(entry.RetiredNative.Keys.First());
        entry.RetiredNative[part] = snapshot;
    }

    internal static object? FailureControls(WIVSMSequence source, nint part)
    {
        if (!Entries.TryGetValue((nint)source, out var entry)) return null;
        var plans = entry.Session.Document.Snapshot();
        var transitions = entry.Session.Transitions.Snapshot.Data ?? TransitionProjectData.Empty;
        var notes = transitions.Notes.Where(n => (nint)PartFor(source, n.Address)! == part).Take(33).ToArray();
        var ids = notes.Select(n => n.Id).ToHashSet();
        bool Owns(TransitionProjectEdge edge) => edge.Left is { } left && ids.Contains(left.Note) || edge.Right is { } right && ids.Contains(right.Note);
        return new
        {
            document = entry.Session.Document.Fingerprint, transitions = entry.Session.Transitions.Snapshot.Fingerprint,
            plans = plans.Where(p => (nint)PartFor(source, p.Key)! == part).Take(33).Select(p => new
                { address = p.Key, total = p.Value.Adjustments.Count, adjustments = p.Value.Adjustments.Take(16).ToArray() }).ToArray(),
            notes = notes.Select(n => new { n.Id, n.Address }).ToArray(),
            junctions = transitions.Junctions.Where(j => j.Aliases.Any(a => Owns(a.Edge))).Take(64).Select(j => new
                { j.OffsetSeconds, aliases = j.Aliases.Take(8).Select(a => new { key = TransitionProjectData.EdgeKey(a.Edge), a.Endpoint }).ToArray() }).ToArray(),
            gains = transitions.Gains.Where(g => Owns(g.Edge)).Take(64).Select(g => new { key = TransitionProjectData.EdgeKey(g.Edge), g.Decibels }).ToArray(),
            phases = transitions.Phases.Where(p => Owns(p.Edge)).Take(64).Select(p => new { key = TransitionProjectData.EdgeKey(p.Edge), p.Amount }).ToArray(),
            onsets = transitions.Onsets.Where(p => Owns(p.Edge)).Take(64).Select(p => new { key = TransitionProjectData.EdgeKey(p.Edge), p.OffsetSeconds }).ToArray(),
            coverage = transitions.Coverages.Where(p => Owns(p.Edge)).Take(64).Select(p => new { key = TransitionProjectData.EdgeKey(p.Edge), p.Sides }).ToArray(),
            bounded = true
        };
    }
}
