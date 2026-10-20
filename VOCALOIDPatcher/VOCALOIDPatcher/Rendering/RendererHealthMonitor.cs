using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Rendering;

internal static class RendererHealthMonitor
{
    private sealed class Entry(WIVSMSequence source)
    {
        internal readonly WIVSMSequence Source = source;
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly RenderRecoveryBudget Budget = new();
        internal readonly Queue<object> Events = new();
        internal readonly Dictionary<nint, Failure> Failures = new();
        internal readonly Dictionary<nint, int> Unrecovered = new();
        internal readonly Dictionary<nint, long> Active = new();
        internal readonly Dictionary<nint, WIVSMMidiPart> Parts = new();
        internal bool RefreshParts = true;
        internal long Request, LastActivity = Environment.TickCount64;
        internal bool Recovering, Checking, StallRecorded;
        internal string? LastIncident, LastReason, Recovery;
    }
    private sealed record Failure(long Request, int Reason, long At);
    internal sealed class DispatchScope
    {
        private Entry? _entry;
        private readonly HashSet<(string, nint)> _seen = new();
        [ThreadStatic] private static Stack<DispatchScope>? _pool;
        private DispatchScope(Entry entry) => _entry = entry;
        internal static DispatchScope? Begin(WIVSMSequence source)
        {
            var entry = Entries.GetValueOrDefault((nint)source) ?? Get(source);
            if (entry == null) return null;
            if (entry.RefreshParts && !source.IsStaged)
            {
                entry.Parts.Clear();
                foreach (var track in source.TracksEnumerator.OfType<WIVSMMidiTrack>())
                    foreach (var part in track.PartsEnumerator.OfType<WIVSMMidiPart>()) entry.Parts[(nint)part] = part;
                entry.RefreshParts = false;
            }
            if (_pool is { Count: > 0 } pool)
            { var scope = pool.Pop(); scope._entry = entry; return scope; }
            return new(entry);
        }
        internal void Release()
        {
            _entry = null; _seen.Clear();
            var pool = _pool ??= new();
            if (pool.Count < 8) pool.Push(this);
        }
        internal void Observe(string kind, nint pointer, int reason)
        {
            if (!_seen.Add((kind, pointer))) return; // Native fans one event out to multiple observers.
            Handle(_entry!, kind, pointer, reason);
        }
    }
    [ThreadStatic] private static DispatchScope? _dispatch;
    private static readonly Dictionary<nint, Entry> Entries = new();
    private static DispatcherTimer? _timer;
    private static int _fatalHook;
    private static string? _lastBoundary, _lastFailure;

    private static Entry? Get(WIVSMSequence source)
    {
        if (Settings.IsFeatureDisabled(Settings.RendererAutoRecoveryKey)) return null;
        if (Application.Current?.Dispatcher.CheckAccess() != true || !source.IsOpen || NativeRenderSession.IsInternalSequence(source)) return null;
        if (Entries.TryGetValue((nint)source, out var existing)) return existing;
        var entry = new Entry(source); Entries[(nint)source] = entry;
        if (System.Threading.Interlocked.Exchange(ref _fatalHook, 1) == 0)
            AppDomain.CurrentDomain.UnhandledException += (_, e) => RendererIncidentLog.Fatal(new
            {
                utc = DateTime.UtcNow, kind = "managed-unhandled-exception", terminating = e.IsTerminating,
                exception = e.ExceptionObject.GetType().FullName,
                lastBoundary = System.Threading.Volatile.Read(ref _lastBoundary),
                lastFailure = System.Threading.Volatile.Read(ref _lastFailure)
            });
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
        return entry;
    }

    internal static void Request(WIVSMSequence source)
    {
        var entry = Get(source);
        if (entry == null || entry.Recovering) return;
        NextRequest(entry); entry.StallRecorded = false;
        Record(entry, "request", 0, 0);
    }
    internal static void InputChanging(WIVSMSequence source)
    {
        if (!Entries.TryGetValue((nint)source, out var entry) || entry.Recovering) return;
        NextRequest(entry); entry.Active.Clear(); entry.StallRecorded = false;
        Record(entry, "input-change", 0, 0);
    }
    internal static void Stopping(WIVSMSequence source)
    {
        if (!Entries.TryGetValue((nint)source, out var entry)) return;
        if (entry.Failures.Count != 0 || entry.Recovering)
            foreach (nint part in entry.Active.Keys.Concat(entry.Parts.Values
                         .Where(p => p.NumNotes != 0 && (!p.HasValidRenderedScore || !p.HasValidRenderedWave))
                         .Select(p => (nint)p)).Distinct())
            {
                entry.Unrecovered.TryAdd(part, -4); // Another Part interrupted by this sequence-wide stop.
                entry.Failures.TryAdd(part, new(entry.Request, -4, Environment.TickCount64));
            }
        entry.Active.Clear(); Record(entry, "stop-request", 0, 0);
    }
    internal static void PreparationFailed(WIVSMSequence source, Exception error)
    {
        try
        {
            if (Get(source) is not { } entry) return;
            Record(entry, "preparation-rejected", 0, error.HResult);
            System.Threading.Volatile.Write(ref _lastFailure, RendererIncidentLog.Capture(new
            {
                utc = DateTime.UtcNow, kind = "preparation-rejected", session = entry.Id,
                request = entry.Request, exception = error.GetType().FullName, error.HResult,
                verification = error is SampledTimingHost.RenderVerificationException verification ? new { verification.Code, verification.Evidence } : null,
                frames = new System.Diagnostics.StackTrace(error, false).GetFrames()?.Take(16)
                    .Select(f => f.GetMethod()?.DeclaringType?.FullName + "." + f.GetMethod()?.Name).ToArray(),
                controls = SampledTimingHost.Diagnostics(source), recent = entry.Events.ToArray()
            }));
        }
        catch { }
    }
    private static void NextRequest(Entry entry)
    {
        entry.Request++; entry.Failures.Clear(); entry.RefreshParts = true;
        foreach (var pair in entry.Unrecovered)
            entry.Failures[pair.Key] = new(entry.Request, pair.Value, Environment.TickCount64);
    }
    internal static DispatchScope? BeginDispatch(WIVSMSequence source)
    {
        var previous = _dispatch;
        _dispatch = DispatchScope.Begin(source);
        return previous;
    }
    internal static void EndDispatch(DispatchScope? previous)
    {
        _dispatch?.Release(); _dispatch = previous;
    }
    internal static void Observe(string kind, nint part, int reason = 0)
    {
        try { _dispatch?.Observe(kind, part, reason); }
        catch (Exception e) { Utils.Debug.Print("Renderer health observation: " + e.GetType().Name); }
    }
    internal static void Close(nint source)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        { dispatcher.BeginInvoke(new Action(() => Close(source))); return; }
        Entries.Remove(source);
        if (Entries.Count == 0) _timer?.Stop();
    }
    internal static object? Diagnostics(WIVSMSequence source)
        => Entries.GetValueOrDefault((nint)source) is { } entry ? new
        {
            session = entry.Id, request = entry.Request, active = entry.Active.Count,
            failures = entry.Failures.Count, unrecovered = entry.Unrecovered.Count, entry.LastIncident, entry.LastReason, entry.Recovery
        } : null;

    private static void Record(Entry entry, string kind, nint part, int reason)
    {
        entry.LastActivity = Environment.TickCount64;
        if (entry.Events.Count >= 32) entry.Events.Dequeue();
        entry.Events.Enqueue(new { utc = DateTime.UtcNow, kind, part = part.ToString("X"), reason, request = entry.Request });
        System.Threading.Volatile.Write(ref _lastBoundary, $"{entry.Id:N}/{entry.Request}/{kind}/{part:X}/{reason}");
    }
    private static WIVSMMidiPart? Resolve(Entry entry, nint pointer)
    {
        foreach (var track in entry.Source.TracksEnumerator.OfType<WIVSMMidiTrack>())
            foreach (var part in track.PartsEnumerator.OfType<WIVSMMidiPart>())
                if ((nint)part == pointer) return part;
        return null;
    }
    private static void Handle(Entry entry, string kind, nint pointer, int reason)
    {
        if (kind == "progress") { entry.LastActivity = Environment.TickCount64; return; }
        Record(entry, kind, pointer, reason);
        if (kind == "started") { entry.Active[pointer] = entry.Request; entry.StallRecorded = false; return; }
        entry.Active.Remove(pointer);
        if (kind == "canceled" && reason is 0 or 1 or 20) return;
        if (kind == "completed")
        {
            // Refresh the membership map once per input request, not once per
            // callback across every Part. Deferred recovery resolves it again.
            if (!entry.Parts.TryGetValue(pointer, out var part) || part.NumNotes == 0) return;
            bool nativeFailure = SampledTimingHost.HasNativeRenderFailure(pointer);
            if (!nativeFailure && part.HasValidRenderedScore && part.HasValidRenderedWave)
            {
                entry.Failures.Remove(pointer);
                entry.Unrecovered.Remove(pointer);
                if (entry.Recovery == "restart-submitted" && entry.Unrecovered.Count == 0)
                { entry.Recovery = "recovered"; LogRecovery(entry); }
                return;
            }
            reason = nativeFailure ? -1 : -5; // Keep native failure evidence distinct from not-yet-valid output.
        }
        if (kind is not ("completed" or "canceled")) return;
        Incident(entry, kind, pointer, reason);
        entry.Unrecovered[pointer] = reason;
        entry.Failures[pointer] = new(entry.Request, reason, Environment.TickCount64);
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => Check(entry)));
    }

    private static void Incident(Entry entry, string kind, nint pointer, int reason)
    {
        entry.LastIncident = Guid.NewGuid().ToString("N"); entry.LastReason = kind + ":" + reason;
        var part = Resolve(entry, pointer);
        System.Threading.Volatile.Write(ref _lastFailure, RendererIncidentLog.Capture(new
        {
            schema = 1, utc = DateTime.UtcNow, incident = entry.LastIncident, session = entry.Id,
            kind, reason, request = entry.Request, part = pointer.ToString("X"),
            editor = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(),
            finished = entry.Source.IsFinishedRendering, enabled = entry.Source.CanAsyncRendering(),
            staged = entry.Source.IsStaged, notes = part?.NumNotes, ai = part?.IsAi,
            validScore = part?.HasValidRenderedScore, validWave = part?.HasValidRenderedWave,
            // No lyrics, project filename, PCM or complete project serialization.
            controls = SampledTimingHost.Diagnostics(entry.Source),
            failedPartControls = SampledTimingHost.FailureControls(entry.Source, pointer),
            temporaryDirectory = reason == 8 ? RenderTemporaryDirectory.Inspect(entry.Source) : null,
            activeParts = entry.Active.Keys.Take(64).Select(p => p.ToString("X")).ToArray(), recent = entry.Events.ToArray()
        }));
    }
    private static void Check(Entry entry)
    {
        if (entry.Checking || !Entries.TryGetValue((nint)entry.Source, out var live) || live != entry || !entry.Source.IsOpen) return;
        entry.Checking = true;
        try
        {
            foreach (var pair in entry.Failures.ToArray())
            {
                if (pair.Value.Request != entry.Request) { entry.Failures.Remove(pair.Key); continue; }
                if (!entry.Source.IsFinishedRendering || entry.Source.IsStaged || System.Windows.Input.Mouse.Captured != null)
                {
                    if (Environment.TickCount64 - pair.Value.At > 10_000)
                    { entry.Recovery = "not-quiescent"; entry.Failures.Remove(pair.Key); LogRecovery(entry); }
                    continue;
                }
                var part = Resolve(entry, pair.Key);
                entry.Failures.Remove(pair.Key);
                if (part == null || part.NumNotes == 0) { entry.Unrecovered.Remove(pair.Key); continue; }
                if (pair.Value.Reason != -1 && part.HasValidRenderedScore && part.HasValidRenderedWave && !SampledTimingHost.HasNativeRenderFailure(pair.Key))
                { entry.Unrecovered.Remove(pair.Key); entry.Recovery = "obsolete-failure"; LogRecovery(entry); continue; }
                if (!Settings.RendererAutoRecovery || !Retryable(pair.Value.Reason))
                { entry.Recovery = "retry-disabled-or-nontransient"; LogRecovery(entry); continue; }
                if (!entry.Budget.TryAcquire(entry.Request, Environment.TickCount64))
                { entry.Recovery = "retry-budget-exhausted"; LogRecovery(entry); continue; }
                entry.Recovering = true;
                try
                {
                    var failures = entry.Failures.Where(p => p.Value.Request == entry.Request &&
                        Retryable(p.Value.Reason)).Prepend(pair).ToArray();
                    var parts = failures.Where(f => f.Value.Reason == -1 || Resolve(entry, f.Key) is { } p &&
                            (!p.HasValidRenderedScore || !p.HasValidRenderedWave || SampledTimingHost.HasNativeRenderFailure((nint)p)))
                        .Select(p => Resolve(entry, p.Key)).OfType<WIVSMMidiPart>().Where(p => p.NumNotes != 0)
                        .ToArray();
                    var retrying = parts.Select(p => (nint)p).ToHashSet();
                    foreach (var failed in failures)
                    {
                        entry.Failures.Remove(failed.Key);
                        if (!retrying.Contains(failed.Key)) entry.Unrecovered.Remove(failed.Key);
                    }
                    entry.Recovery = "restarting"; LogRecovery(entry);
                    if (failures.Any(p => p.Value.Reason == 8)) RenderTemporaryDirectory.Repair(entry.Source);
                    // Only finished work can reach here: Stop must not join a stuck native call on the UI thread.
                    entry.Source.StopAsyncRendering();
                    SampledTimingHost.RestartFailedRender(entry.Source, parts);
                    foreach (var failedPart in parts) RenderedWaveCachePatch.InvalidateAndRefreshPart(failedPart);
                    entry.Source.StartAsyncRendering();
                    entry.Recovery = "restart-submitted";
                }
                catch (Exception e) { entry.Recovery = "restart-failed:" + e.GetType().Name; }
                finally { entry.Recovering = false; LogRecovery(entry); }
                break;
            }
        }
        catch (Exception e) { entry.Recovery = "check-failed:" + e.GetType().Name; LogRecovery(entry); }
        finally { entry.Checking = false; }
    }
    private static void LogRecovery(Entry entry) => RendererIncidentLog.Capture(new
    { utc = DateTime.UtcNow, kind = "recovery", incident = entry.LastIncident, session = entry.Id, request = entry.Request, state = entry.Recovery });
    private static bool Retryable(int reason) => reason is -1 or -4 or -5 || RenderRecoveryBudget.CanRetry(reason);

    private static void Tick()
    {
        foreach (var pair in Entries.ToArray())
        {
            var entry = pair.Value;
            try
            {
                if (!entry.Source.IsOpen) { Close(pair.Key); continue; }
                Check(entry);
                if (entry.Active.Count == 0 || entry.StallRecorded || Environment.TickCount64 - entry.LastActivity < 30_000) continue;
                entry.StallRecorded = true;
                Incident(entry, "no-progress-30s", entry.Active.Keys.First(), -2);
                // A slow or hung native call is diagnostic evidence, not proof that a destructive reset is safe.
                if (entry.Source.IsFinishedRendering)
                {
                    foreach (nint pointer in entry.Active.Keys)
                        if (Resolve(entry, pointer) is { NumNotes: > 0 } part && (!part.HasValidRenderedScore || !part.HasValidRenderedWave))
                        {
                            entry.Failures[pointer] = new(entry.Request, -5, Environment.TickCount64);
                            entry.Unrecovered[pointer] = -5;
                        }
                    entry.Active.Clear(); Check(entry);
                }
            }
            catch (Exception e) { Utils.Debug.Print("Renderer health timer: " + e.GetType().Name); }
        }
    }
#if PATCHER_ISOLATED_VALIDATION
    internal static void ExpireProgressForValidation(WIVSMSequence source)
    {
        Entries[(nint)source].LastActivity = Environment.TickCount64 - 30_001;
        Tick();
    }
#endif
}
