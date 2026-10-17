using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Formats.LibreSvip;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VAE;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Evec;

/// <summary>
/// Publishes PPS-style EVEC articulation as the rendered wave/score of a user part.
/// The user's document is never modified: an isolated sequence copy is rendered and
/// its output is published through the same override surface the extended Chinese
/// pinyin renderer uses. Every native render session is serialized by
/// <see cref="NativeRenderSession.Gate"/>, and the Chinese coordinator keeps
/// priority for a part it has already taken over.
/// </summary>
internal static class EvecRenderCoordinator
{
    private const int ScanDebounceMilliseconds = 120;
    private const int NativeRenderTimeoutSeconds = 90;
    private const int CopyRetrySeconds = 10;
    private const int RetryDelayMilliseconds = 2000;
    // A copy that produced no measurable sample must never replace the native render.
    private const int MinimumAudiblePeak = 8;

    private static long _nextJobId;
    // Last user sequence seen by a trigger, used when the editor view model cannot
    // be reached (for example while the settings window owns the active window).
    private static WeakReference<WIVSMSequence>? _lastSequence;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, RenderJob> Jobs = new();
    private static readonly Dictionary<nint, OverrideState> Overrides = new();
    private static readonly Dictionary<nint, long> SequenceGenerations = new();
    private static long _nextGeneration;
    private static readonly HashSet<nint> PendingScans = new();
    private static readonly Dictionary<nint, CancellationTokenSource> ScanDelays = new();
    private static readonly HashSet<nint> PendingPumps = new();

    static EvecRenderCoordinator()
    {
        Settings.EvecEnabledChanged += enabled =>
        {
            // Overrides must never outlive the feature they were rendered for.
            if (!enabled)
            {
                ReleaseAll("disabled");
                return;
            }

            // Enabling at runtime must publish by itself: a project loaded while
            // EVEC was off has no override and would otherwise keep playing the
            // native render until the next edit.
            try
            {
                MainViewModel? main = Application.Current?.MainWindow?.DataContext as MainViewModel;
                MusicalEditorViewModel? editor = main?.MusicalEditorVM;
                WIVSMMidiPart? active = editor?.ActivePart;
                WIVSMSequence? sequence = active?.Sequence;
                if (sequence == null
                    && _lastSequence != null
                    && _lastSequence.TryGetTarget(out WIVSMSequence? remembered)
                    && remembered.IsOpen)
                {
                    sequence = remembered;
                }

                // VSM wrappers override equality operators that reject null, so the
                // diagnostics use pattern matching instead of "!= null".
                EvecDiagnosticLog.RecordRender(
                    "enable-trigger",
                    $"main={main is not null}; editor={editor is not null}; part={active is not null}; "
                    + $"sequence={sequence is not null}");
                if (sequence != null)
                    SequenceChanged(sequence, "enabled");
            }
            catch (Exception e)
            {
                EvecDiagnosticLog.RecordRender("enable-trigger-failed", $"{e.GetType().Name}: {e.Message}");
                // The next commit, undo/redo, or render publishes normally.
            }
        };
    }

    private static readonly MethodInfo? RegisterAudioPlacementMethod = AccessTools.Method(
        typeof(AudioPlayer),
        "RegisterAudioPlacementWithFile",
        new[] { typeof(WIVSMSequence), typeof(WIVSMMidiPart) });
    private static readonly PropertyInfo? PlacementManagerProperty = AccessTools.Property(
        typeof(AudioPlayer),
        "placementManager");

    private sealed class RenderJob
    {
        public RenderJob(EvecRenderCopy copy, WIVSMMidiPart part, string signature)
        {
            Id = Interlocked.Increment(ref _nextJobId);
            Copy = copy;
            Part = part;
            Signature = signature;
        }

        private int _started;
        public long Id { get; }
        public EvecRenderCopy Copy { get; set; }
        public WIVSMMidiPart Part { get; }
        public string Signature { get; }
        public volatile bool Superseded;

        public bool IsStarted => Volatile.Read(ref _started) != 0;

        public bool TryStart()
            => Interlocked.CompareExchange(ref _started, 1, 0) == 0;
    }

    private sealed record OverrideState(
        string WavePath,
        string ScorePath,
        WIVSMMidiPart Part,
        string Signature);

    private sealed class AsyncRenderState : IDisposable
    {
        public AutoResetEvent Signal { get; } = new(false);
        public int Status;

        public void Dispose() => Signal.Dispose();
    }

    /// <summary>Debounced entry point for project changes.</summary>
    internal static void SequenceChanged(WIVSMSequence? sequence, string source)
    {
        if (!EvecService.IsEnabled || sequence == null || !sequence.IsOpen)
            return;

        nint key = (nint)sequence;
        if (key == 0
            || NativeRenderSession.IsInternalSequence(sequence)
            || SegmentedPhonemeRenderCoordinator.IsInternalSequence(sequence))
        {
            return;
        }

        _lastSequence = new WeakReference<WIVSMSequence>(sequence);

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? displaced = null;
        lock (SyncRoot)
        {
            // All committed inputs matter, including tempo, bank and controllers.
            // Invalidate before debounce so an in-flight old render cannot publish.
            SequenceGenerations[key] = ++_nextGeneration;
            PendingScans.Add(key);
            if (ScanDelays.Remove(key, out CancellationTokenSource? current))
                displaced = current;
            ScanDelays[key] = cancellation;
            displaced?.Cancel();
        }

        ScheduleScan(sequence, key, source, cancellation);
    }

    /// <summary>Entry point for a native render start (unstaged project load).</summary>
    internal static void SequenceRenderingStarted(WIVSMSequence? sequence)
    {
        if (!EvecService.IsEnabled || sequence == null || !sequence.IsOpen)
            return;

        if (NativeRenderSession.IsInternalSequence(sequence)
            || SegmentedPhonemeRenderCoordinator.IsInternalSequence(sequence))
        {
            return;
        }

        // A part that already carries an override does not need another copy for
        // the render the editor is starting; the next edit triggers a rebuild.
        bool pending;
        lock (SyncRoot)
            pending = PendingScans.Contains((nint)sequence);
        if (pending)
            return;

        SequenceChanged(sequence, "render-start");
    }

    private static void ScheduleScan(
        WIVSMSequence sequence,
        nint key,
        string source,
        CancellationTokenSource cancellation)
    {
        EvecDiagnosticLog.RecordRender(
            "scan-queued",
            $"sequence=0x{key:X}; source={source}; debounceMs={ScanDebounceMilliseconds}");

        void Scan()
        {
            try
            {
                if (cancellation.IsCancellationRequested || !EvecService.IsEnabled || !sequence.IsOpen)
                    return;

                foreach (WIVSMMidiTrack track in sequence.MidiTracks)
                {
                    foreach (WIVSMMidiPart part in track.MidiParts)
                    {
                        if (cancellation.IsCancellationRequested)
                            return;
                        Begin(part);
                    }
                }
            }
            catch (Exception e)
            {
                EvecDiagnosticLog.RecordRender("scan-failed", $"sequence=0x{key:X}; {e.GetType().Name}: {e.Message}");
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ScanDebounceMilliseconds, cancellation.Token).ConfigureAwait(false);
                Application? application = Application.Current;
                if (application == null || application.Dispatcher.HasShutdownStarted)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Scan();
                }
                else
                {
                    await application.Dispatcher.InvokeAsync(Scan, DispatcherPriority.Background, cancellation.Token)
                        .Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                EvecDiagnosticLog.RecordRender("scan-schedule-failed", $"{e.GetType().Name}: {e.Message}");
            }
            finally
            {
                lock (SyncRoot)
                {
                    if (ScanDelays.TryGetValue(key, out CancellationTokenSource? current)
                        && ReferenceEquals(current, cancellation))
                    {
                        ScanDelays.Remove(key);
                        PendingScans.Remove(key);
                    }
                }

                cancellation.Dispose();
            }
        });
    }

    private static void Begin(WIVSMMidiPart? part)
    {
        if (part == null)
            return;

        nint key = (nint)part;
        Abandon(RemoveJob(key));
        if (!EvecService.IsEnabled)
            return;

        try
        {
            WIVSMSequence? sequence = part.Sequence;
            WIVSMSequenceManager? manager = App.SequenceManager;
            if (sequence == null || manager == null || part.IsAi || !sequence.IsOpen || sequence.IsStaged)
            {
                RemoveOverrideAndRestoreNative(part);
                return;
            }

            // A part owned by the extended Chinese pinyin renderer keeps native
            // priority; the two renderers must never publish for the same part.
            if (Settings.ExtendedChinesePinyin
                && SegmentedPhonemeRenderCoordinator.TryGetOverridePath(part, out _))
            {
                return;
            }

            if (!part.Notes.Any(EvecService.HasEvec))
            {
                RemoveOverrideAndRestoreNative(part);
                return;
            }

            EvecVoicebankCapabilities capabilities = EvecVoicebankDetector.GetCapabilities(part.VoiceBank());
            if (!capabilities.IsSupported)
            {
                RemoveOverrideAndRestoreNative(part);
                return;
            }

            // A published artifact that still describes the current notes is left
            // alone: one edit must not re-duplicate and re-render every EVEC part.
            string signature = Signature(part) ?? string.Empty;
            lock (SyncRoot)
            {
                if (Overrides.TryGetValue(key, out OverrideState? existing)
                    && string.Equals(existing.Signature, signature, StringComparison.Ordinal)
                    && File.Exists(existing.WavePath))
                {
                    return;
                }
            }

            // A stale override must not mask the editor's newly rendered input if
            // preparing or rendering its replacement fails.
            RemoveOverrideAndRestoreNative(part);
            if (!EvecRenderCopy.TryCreate(manager, part, capabilities, EvecService.GetState, out EvecRenderCopy? copy, out string? error)
                || copy == null)
            {
                EvecDiagnosticLog.RecordRender("copy-failed", $"part=0x{key:X}; {error}");
                RemoveOverrideAndRestoreNative(part);
                return;
            }

            var job = new RenderJob(copy, part, signature);
            RenderJob? displacedJob;
            lock (SyncRoot)
            {
                Jobs.Remove(key, out displacedJob);
                Jobs[key] = job;
            }

            Abandon(displacedJob);
            EvecDiagnosticLog.RecordRender(
                "prepared",
                $"job={job.Id}; part=0x{key:X}; notes={part.Notes.Count}");
            Complete(part);
        }
        catch (Exception e)
        {
            EvecDiagnosticLog.RecordRender("prepare-failed", $"part=0x{key:X}; {e.GetType().Name}: {e.Message}");
            // The native render stays authoritative when a copy cannot be prepared.
        }
    }

    private static void Complete(WIVSMMidiPart? part)
    {
        if (part == null)
            return;

        RenderJob? job;
        lock (SyncRoot)
            Jobs.TryGetValue((nint)part, out job);
        if (job == null)
            return;

        if (job.Superseded || !EvecService.IsEnabled)
        {
            RemoveJobIfCurrent((nint)part, job);
            Abandon(job);
            return;
        }

        if (job.TryStart())
            _ = Task.Run(() => RenderAndPublish(job));
    }

    private static RenderJob? RemoveJob(nint key)
    {
        lock (SyncRoot)
        {
            Jobs.Remove(key, out RenderJob? job);
            return job;
        }
    }

    private static void RemoveJobIfCurrent(nint key, RenderJob job)
    {
        lock (SyncRoot)
        {
            if (Jobs.TryGetValue(key, out RenderJob? current) && ReferenceEquals(current, job))
                Jobs.Remove(key);
        }
    }

    private static void Abandon(RenderJob? job)
    {
        if (job == null)
            return;

        job.Superseded = true;
        EvecDiagnosticLog.RecordRender("superseded", $"job={job.Id}; started={job.IsStarted}");
        if (!job.IsStarted)
            ReleaseCopy(job.Copy);
    }

    private static void RenderAndPublish(RenderJob job)
    {
        string? wavePath = null;
        string? scorePath = null;
        bool published = false;
        try
        {
            if (job.Superseded || !EvecService.IsEnabled)
                return;

            bool rendered = BoundedRenderRetry.Run(
                () => job.Superseded || !EvecService.IsEnabled,
                attempt =>
                {
                    if (TryRenderCopy(job, out wavePath, out scorePath, out string failure))
                        return true;
                    if (!string.IsNullOrEmpty(wavePath)) FileManager.RemoveFile(wavePath);
                    if (!string.IsNullOrEmpty(scorePath)) FileManager.RemoveFile(scorePath);
                    wavePath = scorePath = null;
                    EvecDiagnosticLog.RecordRender("render-failed", $"job={job.Id}; attempt={attempt}; reason={failure}");
                    return false;
                },
                () => WaitForRetry(job),
                () => RebuildRetryCopy(job));
            if (!rendered) return;

            if (job.Superseded || !EvecService.IsEnabled)
                return;

            // The render can take up to a minute; never publish an override that no
            // longer describes the current note data.
            if (!string.Equals(RunOnUi(() => Signature(job.Part)) ?? string.Empty, job.Signature, StringComparison.Ordinal))
            {
                EvecDiagnosticLog.RecordRender("render-stale", $"job={job.Id}");
                return;
            }

            nint key = (nint)job.Part;
            OverrideState? displacedState = null;
            RunOnUi(() =>
            {
                // Check and publish in one UI turn; an edit must not slip between them.
                if (!string.Equals(Signature(job.Part), job.Signature, StringComparison.Ordinal)) return;
                lock (SyncRoot)
                {
                    if (Jobs.TryGetValue(key, out RenderJob? current) && ReferenceEquals(current, job)
                        && !job.Superseded && EvecService.IsEnabled)
                    {
                        if (Overrides.Remove(key, out OverrideState? displaced))
                            displacedState = displaced;
                        Overrides[key] = new OverrideState(wavePath!, scorePath!, job.Part, job.Signature);
                        Jobs.Remove(key);
                        published = true;
                    }
                }
            });

            if (displacedState != null)
                RemoveOverrideFiles(displacedState);
            if (!published)
                return;

            string publishedWave = wavePath!;
            string publishedScore = scorePath ?? string.Empty;
            wavePath = null;
            scorePath = null;
            EvecDiagnosticLog.RecordRender(
                "published",
                $"job={job.Id}; part=0x{key:X}; waveBytes={SafeFileLength(publishedWave)}; "
                + $"scorePresent={!string.IsNullOrEmpty(publishedScore)}; scoreBytes={SafeFileLength(publishedScore)}");
            RefreshPlaybackAndWaveform(job.Part, publishedWave, job.Id);
        }
        catch (Exception e)
        {
            EvecDiagnosticLog.RecordRender("publish-failed", $"job={job.Id}; {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            ReleaseCopy(job.Copy);
            RemoveJobIfCurrent((nint)job.Part, job);
            if (!string.IsNullOrEmpty(wavePath))
                FileManager.RemoveFile(wavePath);
            if (!string.IsNullOrEmpty(scorePath))
                FileManager.RemoveFile(scorePath);
        }
    }

    private static bool TryRenderCopy(
        RenderJob job,
        out string? wavePath,
        out string? scorePath,
        out string failure)
    {
        wavePath = null;
        scorePath = null;
        failure = "unknown";
        WIVSMSequence sequence = job.Copy.Sequence;
        WIVSMMidiPart part = job.Copy.Part;

        while (!NativeRenderSession.Gate.Wait(30))
        {
            if (job.Superseded || !EvecService.IsEnabled)
            {
                failure = "superseded";
                return false;
            }
        }
        bool gateHeld = true;
        WIVSMRendererObserver? observer = null;
        var renderState = new AsyncRenderState();
        bool renderingStarted = false;
        try
        {
            if (job.Superseded)
            {
                failure = "superseded";
                return false;
            }

            if (!EvecService.IsEnabled)
            {
                failure = "disabled";
                return false;
            }

            if (!sequence.IsOpen)
            {
                failure = "copy-closed";
                return false;
            }

            observer = RunOnUi(() => new WIVSMRendererObserver()) ?? throw new InvalidOperationException("Cannot create renderer observer.");
            nint targetPart = (nint)part;
            observer.Completed += (_, args) =>
            {
                if ((nint)args.MidiPart != targetPart)
                    return;
                Volatile.Write(ref renderState.Status, 1);
                renderState.Signal.Set();
            };
            observer.Canceled += (_, args) =>
            {
                if ((nint)args.MidiPart != targetPart)
                    return;
                Volatile.Write(ref renderState.Status, -1);
                renderState.Signal.Set();
            };

            bool started = RunOnUi(() =>
            {
                sequence.BlockRenderingEnabled = false;
                if (!sequence.AddRendererObserver(observer))
                    return false;
                sequence.StartAsyncRendering();
                renderingStarted = true;
                return true;
            });

            if (!started)
            {
                failure = "observer";
                return false;
            }

            if (!WaitForNativeRender(job, sequence, renderState, TimeSpan.FromSeconds(NativeRenderTimeoutSeconds), out failure))
                return false;

            string candidateWave = FileManager.TemporaryWaveFilePath;
            if (!CopyRenderedWave(job, sequence, part, candidateWave))
            {
                FileManager.RemoveFile(candidateWave);
                failure = "wave";
                return false;
            }

            failure = string.Empty;
            wavePath = candidateWave;
            string candidateScore = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.score");
            scorePath = CopyRenderedScore(job, sequence, part, candidateScore) ? candidateScore : null;
            if (scorePath == null)
            {
                FileManager.RemoveFile(candidateScore);
                failure = "score";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            failure = $"error:{e.GetType().Name}";
            EvecDiagnosticLog.RecordRender("render-error", $"job={job.Id}; {e.GetType().Name}: {e.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (renderingStarted && sequence.IsOpen)
                    RunOnUi(() => sequence.StopAsyncRendering());
            }
            catch
            {
            }

            try
            {
                if (observer != null)
                {
                    RunOnUi(() => sequence.RemoveRendererObserver(observer));
                    observer.Dispose();
                }
            }
            catch
            {
            }

            renderState.Dispose();
            if (gateHeld)
                NativeRenderSession.Gate.Release();
        }
    }

    private static bool WaitForNativeRender(
        RenderJob job,
        WIVSMSequence sequence,
        AsyncRenderState state,
        TimeSpan timeout,
        out string failure)
    {
        failure = "timeout";
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (job.Superseded)
            {
                failure = "superseded";
                return false;
            }

            if (!EvecService.IsEnabled)
            {
                failure = "disabled";
                return false;
            }

            PumpRendererCallbacks(sequence);
            int status = Volatile.Read(ref state.Status);
            if (status != 0)
            {
                // A load-time render is routinely canceled by the editor; report it
                // distinctly so the retry is observable in the diagnostic log.
                failure = status > 0 ? string.Empty : "canceled";
                return status > 0;
            }

            state.Signal.WaitOne(30);
        }

        EvecDiagnosticLog.RecordRender("native-timeout", $"job={job.Id}; seconds={timeout.TotalSeconds}");
        return false;
    }

    /// <summary>
    /// One delayed retry for a failed copy render. A project load can cancel the
    /// native renderer before the copy is ready, and the user should not have to
    /// edit a note before EVEC audio appears.
    /// </summary>
    private static bool WaitForRetry(RenderJob job)
    {
        // Keep the same job registered throughout the delay. ReleasePart/Sequence/
        // All and newer edits can still supersede it; no detached retry can revive it.
        EvecDiagnosticLog.RecordRender("retry-scheduled", $"job={job.Id}");
        long deadline = Environment.TickCount64 + RetryDelayMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (job.Superseded || !EvecService.IsEnabled) return false;
            Thread.Sleep(30);
        }
        return !job.Superseded && EvecService.IsEnabled;
    }

    private static bool RebuildRetryCopy(RenderJob job)
        => RunOnUi(() =>
        {
            // StartAsyncRendering on a completed/canceled copy may produce no
            // observer event. Each attempt needs a newly committed native sequence.
            // Keep the existing job identity so cancellation and the retry budget survive.
            if (job.Superseded || !EvecService.IsEnabled
                || !string.Equals(Signature(job.Part), job.Signature, StringComparison.Ordinal))
                return false;
            lock (SyncRoot)
                if (!Jobs.TryGetValue((nint)job.Part, out var current) || !ReferenceEquals(current, job))
                    return false;
            var manager = App.SequenceManager;
            if (manager is null) return false;
            var capabilities = EvecVoicebankDetector.GetCapabilities(job.Part.VoiceBank());
            if (!EvecRenderCopy.TryCreate(manager, job.Part, capabilities, EvecService.GetState,
                    out var replacement, out string? error) || replacement is null)
            {
                EvecDiagnosticLog.RecordRender("retry-copy-failed", $"job={job.Id}; {error}");
                return false;
            }
            EvecRenderCopy previous = job.Copy;
            job.Copy = replacement;
            ReleaseCopy(previous);
            EvecDiagnosticLog.RecordRender("retry-copy-prepared", $"job={job.Id}");
            return !job.Superseded && EvecService.IsEnabled;
        });

    private static void PumpRendererCallbacks(WIVSMSequence sequence)
    {
        const int callbacksPerSlice = 8;

        int PumpCore()
        {
            int count = 0;
            while (count < callbacksPerSlice && sequence.IsOpen && sequence.InvokeRendererObserver())
                count++;
            return count;
        }

        Application? application = Application.Current;
        if (application == null
            || application.Dispatcher.HasShutdownStarted
            || application.Dispatcher.CheckAccess())
        {
            PumpCore();
            return;
        }

        nint key = (nint)sequence;
        lock (SyncRoot)
        {
            if (!PendingPumps.Add(key))
                return;
        }

        try
        {
            application.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    PumpCore();
                }
                catch (Exception e)
                {
                    EvecDiagnosticLog.RecordRender("native-pump-failed", $"{e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    lock (SyncRoot)
                        PendingPumps.Remove(key);
                }
            }), DispatcherPriority.Background);
        }
        catch
        {
            lock (SyncRoot)
                PendingPumps.Remove(key);
        }
    }

    private static bool CopyRenderedWave(RenderJob job, WIVSMSequence sequence, WIVSMMidiPart part, string outputPath)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(CopyRetrySeconds);
        int lastPeak = -1;
        while (DateTime.UtcNow < deadline)
        {
            if (job.Superseded)
                return false;

            PumpRendererCallbacks(sequence);
            string sourcePath = RunOnUi(() => part.WaveFilePath) ?? string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, outputPath, true);
                    lastPeak = ReadWavePeak(outputPath);
                    if (lastPeak >= MinimumAudiblePeak)
                    {
                        EvecDiagnosticLog.RecordRender(
                            "wave-copied",
                            $"job={job.Id}; peak={lastPeak}; bytes={SafeFileLength(outputPath)}");
                        return true;
                    }
                }
            }
            catch
            {
            }

            FileManager.RemoveFile(outputPath);
            Thread.Sleep(30);
        }

        EvecDiagnosticLog.RecordRender(
            "wave-copy-failed",
            $"job={job.Id}; validRenderedWave={RunOnUi(() => part.HasValidRenderedWave)}; lastPeak={lastPeak}");
        return false;
    }

    private static bool CopyRenderedScore(RenderJob job, WIVSMSequence sequence, WIVSMMidiPart part, string outputPath)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(CopyRetrySeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (job.Superseded)
                return false;

            PumpRendererCallbacks(sequence);
            string sourcePath = RunOnUi(() => part.ScoreFilePath) ?? string.Empty;
            try
            {
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, outputPath, true);
                    long frames = RunOnUi(() =>
                    {
                        using var scoreFile = new VSMScoreFile(outputPath);
                        return (long)scoreFile.NumScores;
                    });
                    if (frames > 0 && SafeFileLength(outputPath) > 0)
                        return true;
                }
            }
            catch
            {
            }

            FileManager.RemoveFile(outputPath);
            Thread.Sleep(30);
        }

        EvecDiagnosticLog.RecordRender("score-copy-failed", $"job={job.Id}");
        return false;
    }

    /// <summary>
    /// Returns the peak magnitude of a PCM wave, or -1 when it cannot be read.
    /// V6 renders 16-bit PCM; anything else is treated as unreadable instead of
    /// silently publishing an unknown artifact.
    /// </summary>
    private static int ReadWavePeak(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 44 || ReadChunkId(reader) != "RIFF")
                return -1;
            reader.ReadInt32();
            if (ReadChunkId(reader) != "WAVE")
                return -1;

            int bits = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                long chunkStart = stream.Position;
                string chunk = ReadChunkId(reader);
                int size = reader.ReadInt32();
                if (size < 0)
                    return -1;

                // A chunk body is not padded past an odd size, so account for the
                // pad byte before advancing; never compare against a position this
                // loop already consumed field by field.
                long next = chunkStart + 8 + size + (size % 2);
                if (chunk == "fmt ")
                {
                    reader.ReadInt16();
                    reader.ReadInt16();
                    reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                }
                else if (chunk == "data")
                {
                    if (bits != 16)
                        return -1;

                    int peak = 0;
                    long remaining = Math.Min(size, stream.Length - stream.Position);
                    for (long offset = 0; offset + 1 < remaining; offset += 2)
                    {
                        short sample = reader.ReadInt16();
                        int magnitude = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
                        if (magnitude > peak)
                            peak = magnitude;
                    }

                    return peak;
                }

                if (next <= chunkStart)
                    return -1;
                stream.Position = Math.Min(next, stream.Length);
            }

            return -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string ReadChunkId(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return bytes.Length == 4 ? Encoding.ASCII.GetString(bytes) : string.Empty;
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string? Signature(WIVSMMidiPart? part)
    {
        if (part == null)
            return null;

        try
        {
            var builder = new StringBuilder();
            WIVSMSequence? sequence = part.Sequence;
            if (sequence is null || !sequence.IsOpen) return null;
            lock (SyncRoot)
                builder.Append(SequenceGenerations.GetValueOrDefault((nint)sequence)).Append('|');
            foreach (WIVSMNote note in part.Notes)
            {
                EvecNoteState state = EvecService.GetState(note);
                builder.Append(note.RelPosTick.Value).Append(':')
                    .Append(note.DurationTick.Value).Append(':')
                    .Append(note.NoteNumber).Append(':')
                    .Append(note.LangID).Append(':')
                    .Append(EvecPhonemeRecomposer.StripEvec(note.Phonemes)).Append(':')
                    .Append(state.VoiceColorId).Append('/').Append(state.AttackId).Append('/')
                    .Append(state.ReleaseId).Append('/').Append(state.ConsonantExtension).Append(';');
            }

            return builder.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static void RefreshPlaybackAndWaveform(WIVSMMidiPart part, string path, long jobId)
    {
        Application? application = Application.Current;
        if (application == null)
            return;

        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!EvecService.IsEnabled
                    || !TryGetOverridePath(part, out string currentPath)
                    || !string.Equals(currentPath, path, StringComparison.Ordinal))
                {
                    return;
                }

                WIVSMSequence? sequence = part.Sequence;
                AudioPlayer? audioPlayer = App.AudioPlayer;
                var placementManager = audioPlayer == null
                    ? null
                    : PlacementManagerProperty?.GetValue(audioPlayer) as AudioPlacementManager;
                if (sequence != null && placementManager != null)
                {
                    placementManager.AddOrReplacePlacement(sequence, part, PlacementStorageType.File);
                }
                else if (sequence != null && audioPlayer != null && RegisterAudioPlacementMethod != null)
                {
                    RegisterAudioPlacementMethod.Invoke(audioPlayer, new object[] { sequence, part });
                }

                int pitchViews = SegmentedPhonemeRenderCoordinator.RefreshPitchCurves(part);
                WaveformSvState.Invalidate();
                RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
                EvecDiagnosticLog.RecordRender(
                    "placement",
                    $"job={jobId}; sequence={sequence != null}; manager={placementManager != null}; "
                    + $"validWave={part.HasValidRenderedWave}; bytes={SafeFileLength(path)}; pitchViews={pitchViews}");
            }
            catch (Exception e)
            {
                EvecDiagnosticLog.RecordRender("placement-failed", $"job={jobId}; {e.GetType().Name}: {e.Message}");
                // The override stays available to the next playback or redraw.
            }
        }), DispatcherPriority.Background);
    }

    private static void RefreshNativePlaybackAndWaveform(WIVSMMidiPart part)
    {
        Application? application = Application.Current;
        if (application == null)
            return;

        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (TryGetOverridePath(part, out _))
                    return;

                WIVSMSequence? sequence = part.Sequence;
                AudioPlayer? audioPlayer = App.AudioPlayer;
                if (sequence != null && audioPlayer != null && RegisterAudioPlacementMethod != null)
                    RegisterAudioPlacementMethod.Invoke(audioPlayer, new object[] { sequence, part });

                WaveformSvState.Invalidate();
                RenderedWaveCachePatch.InvalidateAndRefreshPart(part);
                SegmentedPhonemeRenderCoordinator.RefreshPitchCurves(part);
            }
            catch
            {
                // The next native playback or redraw refreshes this part.
            }
        }), DispatcherPriority.Background);
    }

    private static void RemoveOverrideAndRestoreNative(WIVSMMidiPart part)
    {
        OverrideState? state;
        lock (SyncRoot)
            Overrides.Remove((nint)part, out state);
        if (state == null)
            return;

        RemoveOverrideFiles(state);
        EvecDiagnosticLog.RecordRender("override-released", $"part=0x{(nint)part:X}");
        RefreshNativePlaybackAndWaveform(part);
    }

    private static void RemoveOverrideFiles(OverrideState state)
    {
        FileManager.RemoveFile(state.WavePath);
        if (!string.IsNullOrEmpty(state.ScorePath))
            FileManager.RemoveFile(state.ScorePath);
    }

    private static void ReleaseCopy(EvecRenderCopy? copy)
    {
        if (copy == null)
            return;

        WIVSMSequence sequence = copy.Sequence;
        try
        {
            copy.ReleaseToCloseWindow();
        }
        catch
        {
        }

        // The internal registration is cleared when the shared close path closes
        // the sequence, so a closing duplicate is never mistaken for a document.
        SegmentedPhonemeRenderCoordinator.CloseSequenceSafely(sequence);
    }

    private static T RunOnUi<T>(Func<T> action)
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.CheckAccess())
            return action();

        return application.Dispatcher.Invoke(action, DispatcherPriority.Background);
    }

    private static void RunOnUi(Action action)
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        application.Dispatcher.Invoke(action, DispatcherPriority.Background);
    }

    internal static bool TryGetOverridePath(WIVSMMidiPart part, out string path)
    {
        lock (SyncRoot)
        {
            if (Overrides.TryGetValue((nint)part, out OverrideState? state))
            {
                path = state.WavePath;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    internal static bool TryGetOverrideScorePath(WIVSMMidiPart part, out string path)
    {
        lock (SyncRoot)
        {
            if (Overrides.TryGetValue((nint)part, out OverrideState? state)
                && !string.IsNullOrEmpty(state.ScorePath))
            {
                path = state.ScorePath;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>Drops jobs and overrides for a part that is being removed.</summary>
    internal static void ReleasePart(WIVSMMidiPart? part)
    {
        if (part == null)
            return;

        nint key = (nint)part;
        RenderJob? job;
        OverrideState? state;
        lock (SyncRoot)
        {
            Jobs.Remove(key, out job);
            Overrides.Remove(key, out state);
        }

        Abandon(job);
        if (state != null)
        {
            RemoveOverrideFiles(state);
            EvecDiagnosticLog.RecordRender("override-released", $"part=0x{key:X}; reason=part-removed");
        }
    }

    /// <summary>Drops jobs and overrides that belong to a sequence being closed.</summary>
    internal static void ReleaseSequence(WIVSMSequence? sequence)
    {
        if (sequence == null || NativeRenderSession.IsInternalSequence(sequence))
            return;

        lock (SyncRoot)
        {
            nint key = (nint)sequence;
            if (ScanDelays.Remove(key, out var scan)) scan.Cancel();
            PendingScans.Remove(key);
            SequenceGenerations.Remove(key);
        }

        var parts = new List<WIVSMMidiPart>();
        try
        {
            foreach (WIVSMMidiTrack track in sequence.MidiTracks)
                parts.AddRange(track.MidiParts);
        }
        catch
        {
            // The sequence may already be torn down; overrides are released below.
        }

        foreach (WIVSMMidiPart part in parts)
            ReleasePart(part);
    }

    /// <summary>Releases every EVEC override; used when the feature is switched off.</summary>
    internal static void ReleaseAll(string reason)
    {
        List<RenderJob> jobs;
        List<OverrideState> overrides;
        List<CancellationTokenSource> scans;
        lock (SyncRoot)
        {
            jobs = new List<RenderJob>(Jobs.Values);
            overrides = new List<OverrideState>(Overrides.Values);
            scans = new List<CancellationTokenSource>(ScanDelays.Values);
            foreach (CancellationTokenSource scan in scans)
                scan.Cancel();
            Jobs.Clear();
            Overrides.Clear();
            ScanDelays.Clear();
            PendingScans.Clear();
        }

        foreach (RenderJob job in jobs)
            Abandon(job);
        foreach (OverrideState state in overrides)
            RemoveOverrideFiles(state);

        EvecDiagnosticLog.RecordRender(
            "released-all",
            $"reason={reason}; jobs={jobs.Count}; overrides={overrides.Count}");
        if (overrides.Count > 0)
            WaveformSvState.Invalidate();
        foreach (OverrideState state in overrides)
            RefreshNativePlaybackAndWaveform(state.Part);
    }
}
