using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HarmonyLib;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Patch.Patches;
using VOCALOIDPatcher.RegisterShift;
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace VOCALOIDPatcher.BreathVolume;

internal readonly record struct BreathRegion(
    long BeginSample,
    long EndSample,
    long BeginTick,
    long EndTick,
    IntPtr NoteHandle);

internal readonly record struct BreathSampleRange(long BeginSample, long EndSample);

internal sealed record BreathNativeObjectHandles(IntPtr[] NoteHandles, IntPtr[] PartHandles);

internal enum BreathVolumeChangeKind
{
    Display,
    Values,
    Regions,
    Selection
}

internal enum BreathRegionStatus
{
    Unknown,
    Loading,
    Ready,
    Faulted
}

internal static class BreathVolumeService
{
    public const int DefaultValue = 127;
    public const int MinValue = 0;
    public const int MaxValue = 127;
    public const string ProjectEntryPath = BreathProjectArchive.EntryPath;

    public static readonly ControlParameterTypeEnum ParameterType = (ControlParameterTypeEnum)0x42564c;

    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, NoteKey> ActiveNoteKeys = new();
    private static readonly Dictionary<NoteKey, byte> Values = new();
    private static readonly HashSet<NoteKey> Selection = new();
    private static readonly Dictionary<NoteKey, IntPtr> NoteOwners = new();
    private static readonly HashSet<NoteKey> ClipboardNotes = new();
    private static readonly Dictionary<IntPtr, PartState> Parts = new();
    private static readonly Dictionary<IntPtr, SequenceHistory> Histories = new();
    private static readonly HashSet<string> CreatedCacheFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "BreathVolume");

    private static readonly MethodInfo? RegisterFilePlacementMethod = AccessTools.Method(
        typeof(AudioPlayer), "RegisterAudioPlacementWithFile",
        new[] { typeof(WIVSMSequence), typeof(WIVSMMidiPart) });

    [ThreadStatic]
    private static int _wavePathBypass;

    private static long _nextNoteGeneration;

    public static event Action<BreathVolumeChangeKind, WIVSMMidiPart?>? Changed;
    public static event Action<WIVSMMidiPart, int, bool>? RebuildCompleted;

    private static void NotifyChanged(BreathVolumeChangeKind kind, WIVSMMidiPart? part)
    {
        void Notify()
        {
            try
            {
                Changed?.Invoke(kind, part);
            }
            catch (Exception e)
            {
                BreathVolumeDiagnosticsLog.Write(
                    $"change notification failed kind={kind}: {e.GetType().Name}: {e.Message}");
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            Notify();
            return;
        }

        try
        {
            dispatcher.BeginInvoke((Action)Notify);
        }
        catch (Exception e)
        {
            BreathVolumeDiagnosticsLog.Write(
                $"change dispatch failed kind={kind}: {e.GetType().Name}: {e.Message}");
        }
    }

    static BreathVolumeService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupCreatedFiles();
    }

    public static void InitializeDiagnostics()
    {
        BreathVolumeDiagnosticsLog.Initialize();
        var nativeMixer = NativeBreathCapture.TryInitialize();
        BreathVolumeDiagnosticsLog.Write(
            $"initialize detector=native-mixer+score-pcm nativeMixer={nativeMixer} " +
            $"log='{BreathVolumeDiagnosticsLog.FilePath}'");
        BreathVolumeDiagnosticsLog.WriteNativeSnapshot("initialize", force: true);
    }

    public static bool IsActive(ControlParameterTypeEnum type)
        => Settings.IndividualBreathVolume && type.Equals(ParameterType);

    public static byte GetValue(IntPtr noteHandle)
    {
        lock (Sync)
            return TryGetNoteKeyCore(noteHandle, out var key) && Values.TryGetValue(key, out var value)
                ? value
                : (byte)DefaultValue;
    }

    public static byte GetValue(WIVSMNote? note)
    {
        if (note == null)
            return DefaultValue;
        RegisterNote(note);
        return GetValue(note.CppObjPtr);
    }

    public static IReadOnlyList<BreathRegion> GetRegions(WIVSMMidiPart? part)
    {
        if (part == null)
            return Array.Empty<BreathRegion>();

        lock (Sync)
            return Parts.TryGetValue((IntPtr)part, out var state)
                ? state.Regions.ToArray()
                : Array.Empty<BreathRegion>();
    }

    public static BreathRegionStatus GetRegionStatus(WIVSMMidiPart? part)
    {
        if (part == null)
            return BreathRegionStatus.Unknown;

        lock (Sync)
            return Parts.TryGetValue((IntPtr)part, out var state)
                ? state.RegionStatus
                : BreathRegionStatus.Unknown;
    }

    public static void EnsureRegionsAsync(WIVSMSequence sequence, WIVSMMidiPart part)
        => QueueRegionRefresh(sequence, part, force: false, rebuildAfterRefresh: false);

    public static void RefreshRegionsAsync(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        bool rebuildAfterRefresh = false)
        => QueueRegionRefresh(sequence, part, force: true, rebuildAfterRefresh);

    private static void QueueRegionRefresh(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        bool force,
        bool rebuildAfterRefresh)
    {
        if (!Settings.IndividualBreathVolume || sequence == null || part == null)
            return;

        PartState state;
        int generation;
        lock (Sync)
        {
            state = GetPartState(part);
            state.Sequence = sequence;
            state.Part = part;
            state.RebuildAfterRegionRefresh |= rebuildAfterRefresh;
            if (state.RegionRefreshPending || !force && state.RegionStatus == BreathRegionStatus.Ready)
                return;

            state.RegionRefreshPending = true;
            state.RegionStatus = BreathRegionStatus.Loading;
            generation = ++state.RegionRefreshGeneration;
        }

        NotifyChanged(BreathVolumeChangeKind.Regions, part);
        _ = Task.Run(async () =>
        {
            var rebuild = false;
            try
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    RefreshRegionsCore(sequence, part, state, generation);
                    if (GetRegionStatus(part) != BreathRegionStatus.Faulted)
                        break;
                    await Task.Delay(50 << attempt).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_ScoreFailed", e.Message));
                lock (Sync)
                {
                    if (Parts.TryGetValue((IntPtr)part, out var current) &&
                        ReferenceEquals(current, state) && current.RegionRefreshGeneration == generation)
                        current.RegionStatus = BreathRegionStatus.Faulted;
                }
            }
            finally
            {
                lock (Sync)
                {
                    if (Parts.TryGetValue((IntPtr)part, out var current) &&
                        ReferenceEquals(current, state) && current.RegionRefreshGeneration == generation)
                    {
                        current.RegionRefreshPending = false;
                        rebuild = current.RebuildAfterRegionRefresh;
                        current.RebuildAfterRegionRefresh = false;
                    }
                }
            }

            if (rebuild)
            {
                try { RebuildNow(sequence, part); }
                catch (Exception e)
                {
                    Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", e.Message));
                    RemoveCache(part);
                    RefreshAudioPlacement(sequence, part);
                }
            }

            NotifyChanged(BreathVolumeChangeKind.Regions, part);
        });
    }

    public static IReadOnlyCollection<IntPtr> GetSelection()
    {
        lock (Sync)
            return Selection.Where(IsActiveNoteKeyCore).Select(key => key.Handle).ToArray();
    }

    public static bool IsSelected(IntPtr noteHandle)
    {
        lock (Sync)
            return TryGetNoteKeyCore(noteHandle, out var key) && Selection.Contains(key);
    }

    public static void SetSelection(IEnumerable<IntPtr> handles, bool additive = false)
    {
        lock (Sync)
        {
            if (!additive)
                Selection.Clear();
            foreach (var handle in handles.Where(handle => handle != IntPtr.Zero))
                Selection.Add(GetOrCreateNoteKeyCore(handle));
        }

        NotifyChanged(BreathVolumeChangeKind.Selection, null);
    }

    public static void ToggleSelection(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        lock (Sync)
        {
            var key = GetOrCreateNoteKeyCore(handle);
            if (!Selection.Remove(key))
                Selection.Add(key);
        }

        NotifyChanged(BreathVolumeChangeKind.Selection, null);
    }

    public static void ClearSelection()
    {
        lock (Sync)
            Selection.Clear();
        NotifyChanged(BreathVolumeChangeKind.Selection, null);
    }

    public static Dictionary<IntPtr, byte> Snapshot(IEnumerable<IntPtr> handles)
        => handles.Distinct().Where(handle => handle != IntPtr.Zero)
            .ToDictionary(handle => handle, GetValue);

    public static void SetPreviewValues(IEnumerable<IntPtr> handles, int value)
    {
        var normalized = (byte)Math.Clamp(value, MinValue, MaxValue);
        lock (Sync)
        {
            foreach (var handle in handles.Where(handle => handle != IntPtr.Zero))
                SetValueCore(handle, normalized);
        }

        NotifyChanged(BreathVolumeChangeKind.Display, null);
    }

    public static void SetPreviewValues(IEnumerable<KeyValuePair<IntPtr, byte>> values)
    {
        lock (Sync)
        {
            foreach (var pair in values)
            {
                if (pair.Key != IntPtr.Zero)
                    SetValueCore(pair.Key, pair.Value);
            }
        }

        NotifyChanged(BreathVolumeChangeKind.Display, null);
    }

    public static void CommitValues(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        IReadOnlyDictionary<IntPtr, byte> before)
    {
        lock (Sync)
        {
            var owner = (IntPtr)sequence;
            var keyedBefore = new Dictionary<NoteKey, byte>();
            var keyedAfter = new Dictionary<NoteKey, byte>();
            foreach (var pair in before)
            {
                if (pair.Key == IntPtr.Zero)
                    continue;
                var key = GetOrCreateNoteKeyCore(pair.Key);
                NoteOwners[key] = owner;
                keyedBefore[key] = pair.Value;
                keyedAfter[key] = Values.TryGetValue(key, out var value) ? value : (byte)DefaultValue;
            }

            if (keyedBefore.Count == keyedAfter.Count &&
                keyedBefore.All(pair => keyedAfter[pair.Key] == pair.Value))
                return;

            var history = GetHistory(sequence);
            history.Undo.Add(new TimelineEntry(new ValueEdit(keyedBefore, keyedAfter)));
            history.Redo.Clear();
            history.Revision++;
        }

        NotifyChanged(BreathVolumeChangeKind.Values, part);
        RequestRebuild(sequence, part);
    }

    public static void SetValues(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        IEnumerable<IntPtr> handles,
        int value)
    {
        var distinct = handles.Distinct().Where(handle => handle != IntPtr.Zero).ToArray();
        if (distinct.Length == 0)
            return;

        var before = Snapshot(distinct);
        SetPreviewValues(distinct, value);
        CommitValues(sequence, part, before);
    }

    public static void ResetSelected(WIVSMSequence sequence, WIVSMMidiPart part)
        => SetValues(sequence, part, GetSelection(), DefaultValue);

    public static void CopyNoteValue(WIVSMNote? source, WIVSMNote? target)
    {
        if (source == null || target == null)
            return;

        var value = GetValue(source);
        RegisterCopiedNote(target, clipboard: false);
        SetValueCore(target.CppObjPtr, value);
    }

    public static void CopyClipboardNoteValue(WIVSMNote? source, WIVSMNote? target)
    {
        if (source == null || target == null)
            return;

        var value = GetValue(source);
        RegisterCopiedNote(target, clipboard: true);
        SetValueCore(target.CppObjPtr, value);
    }

    public static void CopyPartValues(WIVSMMidiPart? source, WIVSMMidiPart? target)
    {
        if (source == null || target == null)
            return;

        var count = Math.Min(source.NumNotes, target.NumNotes);
        for (ulong index = 0; index < count; index++)
            CopyNoteValue(source.GetNote(index), target.GetNote(index));
        CopyDetectedBreathRanges(source, target);
    }

    public static void CopyClipboardPartValues(WIVSMMidiPart? source, WIVSMMidiPart? target)
    {
        if (source == null || target == null)
            return;

        var count = Math.Min(source.NumNotes, target.NumNotes);
        for (ulong index = 0; index < count; index++)
            CopyClipboardNoteValue(source.GetNote(index), target.GetNote(index));
        CopyDetectedBreathRanges(source, target);
    }

    private static void CopyDetectedBreathRanges(WIVSMMidiPart source, WIVSMMidiPart target)
    {
        lock (Sync)
        {
            if (!Parts.TryGetValue((IntPtr)source, out var sourceState) ||
                sourceState.NativeBreathMarkers.Count == 0 &&
                sourceState.TraditionalBreathRanges.Count == 0)
                return;
            var targetState = GetPartState(target);
            targetState.NativeBreathMarkers = sourceState.NativeBreathMarkers
                .Select(marker => marker with { PartHandle = (IntPtr)target })
                .ToList();
            targetState.TraditionalBreathRanges = sourceState.TraditionalBreathRanges.ToList();
            targetState.NativeBreathSequences.Clear();
            targetState.NativeBreathSequences.UnionWith(
                targetState.NativeBreathMarkers.Select(marker => marker.Sequence));
            targetState.ScoreCount = -1;
            targetState.RegionStatus = BreathRegionStatus.Unknown;
        }
    }

    public static void CopyTrackValues(WIVSMTrack? source, WIVSMTrack? target)
    {
        if (source is not WIVSMMidiTrack sourceTrack || target is not WIVSMMidiTrack targetTrack)
            return;

        var count = Math.Min(sourceTrack.NumParts, targetTrack.NumParts);
        for (ulong index = 0; index < count; index++)
        {
            if (sourceTrack.GetPart(index) is WIVSMMidiPart sourcePart &&
                targetTrack.GetPart(index) is WIVSMMidiPart targetPart)
                CopyPartValues(sourcePart, targetPart);
        }
    }

    public static void CopySequenceValues(WIVSMSequence? source, WIVSMSequence? target)
    {
        if (source == null || target == null)
            return;

        var count = Math.Min(source.NumTrack, target.NumTrack);
        for (ulong index = 0; index < count; index++)
            CopyTrackValues(source.GetTrack(index), target.GetTrack(index));
    }

    public static IntPtr[] CapturePartNoteHandles(WIVSMMidiPart? part)
    {
        if (part == null)
            return Array.Empty<IntPtr>();
        try
        {
            return EnumerateNotes(part).Select(note => note.CppObjPtr)
                .Where(handle => handle != IntPtr.Zero).Distinct().ToArray();
        }
        catch
        {
            return Array.Empty<IntPtr>();
        }
    }

    public static BreathNativeObjectHandles CapturePartObjects(WIVSMMidiPart? part)
        => part == null
            ? new BreathNativeObjectHandles(Array.Empty<IntPtr>(), Array.Empty<IntPtr>())
            : new BreathNativeObjectHandles(CapturePartNoteHandles(part), new[] { (IntPtr)part });

    public static BreathNativeObjectHandles CaptureTrackObjects(WIVSMTrack? track)
    {
        if (track is not WIVSMMidiTrack midiTrack)
            return new BreathNativeObjectHandles(Array.Empty<IntPtr>(), Array.Empty<IntPtr>());
        try
        {
            var noteHandles = new HashSet<IntPtr>();
            var partHandles = new HashSet<IntPtr>();
            for (ulong index = 0; index < midiTrack.NumParts; index++)
            {
                if (midiTrack.GetPart(index) is not WIVSMMidiPart part)
                    continue;
                partHandles.Add((IntPtr)part);
                noteHandles.UnionWith(CapturePartNoteHandles(part));
            }
            return new BreathNativeObjectHandles(noteHandles.ToArray(), partHandles.ToArray());
        }
        catch
        {
            return new BreathNativeObjectHandles(Array.Empty<IntPtr>(), Array.Empty<IntPtr>());
        }
    }

    public static IntPtr[] CaptureClipboardNoteHandles(
        WIVSMClipboard? clipboard,
        bool includeNotes,
        bool includeMidiParts)
    {
        if (clipboard == null)
            return Array.Empty<IntPtr>();
        try
        {
            var handles = new HashSet<IntPtr>();
            if (includeNotes)
                for (ulong index = 0; index < clipboard.NumNote; index++)
                    if (clipboard.GetNote(index) is { } note)
                        handles.Add(note.CppObjPtr);
            if (includeMidiParts)
                for (ulong index = 0; index < clipboard.NumMidiPart; index++)
                    if (clipboard.GetMidiPart(index) is { } part)
                        handles.UnionWith(CapturePartNoteHandles(part));
            return handles.ToArray();
        }
        catch
        {
            return Array.Empty<IntPtr>();
        }
    }

    public static void ReleaseNoteHandles(IEnumerable<IntPtr>? handles)
    {
        if (handles == null)
            return;
        lock (Sync)
            ReleaseHandlesCore(handles);
    }

    public static void ReleaseNativeObjects(BreathNativeObjectHandles? handles)
    {
        if (handles == null)
            return;
        lock (Sync)
        {
            ReleaseHandlesCore(handles.NoteHandles);
            foreach (var handle in handles.PartHandles)
            {
                if (!Parts.Remove(handle, out var state))
                    continue;
                Interlocked.Increment(ref state.RebuildGeneration);
                RemoveCacheCore(state);
            }
        }
    }

    public static void ReleaseMissingPartNotes(WIVSMMidiPart? part, IEnumerable<IntPtr>? previousHandles)
    {
        if (previousHandles == null)
            return;
        var liveHandles = CapturePartNoteHandles(part).ToHashSet();
        ReleaseNoteHandles(previousHandles.Where(handle => !liveHandles.Contains(handle)));
    }

    public static void PruneSequence(WIVSMSequence? sequence)
    {
        if (sequence == null)
            return;

        var liveNotes = new HashSet<IntPtr>();
        var partHandles = new HashSet<IntPtr>();
        CollectSequenceHandles(sequence, liveNotes, partHandles);
        var sequenceHandle = (IntPtr)sequence;
        lock (Sync)
        {
            ReleaseHandlesCore(NoteOwners.Where(pair => pair.Value == sequenceHandle &&
                                                        !liveNotes.Contains(pair.Key.Handle))
                .Select(pair => pair.Key.Handle).ToArray());
            foreach (var pair in Parts.Where(pair => pair.Value.Sequence?.Equals(sequence) == true &&
                                                     !partHandles.Contains(pair.Key)).ToArray())
            {
                Interlocked.Increment(ref pair.Value.RebuildGeneration);
                RemoveCacheCore(pair.Value);
                Parts.Remove(pair.Key);
            }
            foreach (var handle in liveNotes)
                RegisterNoteCore(handle, sequenceHandle, clipboard: false);
        }
    }

    public static bool RefreshRegions(WIVSMSequence sequence, WIVSMMidiPart part)
        => RefreshRegionsCore(sequence, part, expectedState: null, expectedGeneration: 0);

    private static bool RefreshRegionsCore(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        PartState? expectedState,
        int expectedGeneration)
    {
        if (!Settings.IndividualBreathVolume || sequence == null || part == null)
            return false;
        if (!IsRegionRefreshCurrent(part, expectedState, expectedGeneration))
            return false;

        try
        {
            var path = part.ScoreFilePath;
            if (part.HasValidRenderedScore && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var wavePath = ReadOriginalWavePath(part);
                var file = new VSMScoreFile(path) { RawState = 0 };
                try
                {
                    var signature = new ScoreSignature(
                        ScoreSourceKind.RenderedFile,
                        file.NumScores,
                        0,
                        path,
                        SafeLength(path),
                        SafeWriteTime(path).Ticks,
                        wavePath,
                        SafeLength(wavePath),
                        SafeWriteTime(wavePath).Ticks);
                    DetectTraditionalBreathRangesFromWave(
                        sequence, part, file, wavePath, signature,
                        expectedState, expectedGeneration);
                    return RefreshRegions(
                        sequence, part, file, signature,
                        expectedState: expectedState, expectedGeneration: expectedGeneration);
                }
                finally
                {
                    SafeDisposeScore(file, "rendered-file");
                }
            }

            VSMScoreList? rendering = null;
            VSMScoreList? holding = null;
            try
            {
                rendering = part.RenderingScoreList;
                if (rendering != null && !rendering.IsEmpty)
                {
                    rendering.RawState = 2;
                    holding = part.HoldingScoreList;
                    if (holding != null)
                        holding.RawState = 3;

                    var signature = new ScoreSignature(
                        ScoreSourceKind.CombinedRendering,
                        rendering.NumScores,
                        holding?.NumScores ?? -1,
                        null,
                        0,
                        0);
                    var combined = new VSMCombinedScore(rendering, holding);
                    rendering = null;
                    holding = null;
                    try
                    {
                        return RefreshRegions(
                            sequence, part, combined, signature,
                            expectedState: expectedState, expectedGeneration: expectedGeneration);
                    }
                    finally
                    {
                        SafeDisposeScore(combined, "combined-rendering");
                    }
                }

                SafeDisposeScore(rendering, "empty-rendering");
                rendering = null;
                holding = part.HoldingScoreList;
                if (holding != null && !holding.IsEmpty)
                {
                    holding.RawState = 4;
                    var signature = new ScoreSignature(
                        ScoreSourceKind.Holding,
                        holding.NumScores,
                        0,
                        null,
                        0,
                        0);
                    return RefreshRegions(
                        sequence, part, holding, signature,
                        expectedState: expectedState, expectedGeneration: expectedGeneration);
                }
            }
            finally
            {
                SafeDisposeScore(rendering, "rendering");
                SafeDisposeScore(holding, "holding");
            }

            return StoreRegions(
                part, Array.Empty<BreathRegion>(), 0, ScoreSignature.Unavailable,
                expectedState, expectedGeneration);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_ScoreFailed", e.Message));
            BreathVolumeDiagnosticsLog.Write(
                $"score refresh failed: {e.GetType().Name}: {e.Message}");
            return MarkRegionRefreshFaulted(part, expectedState, expectedGeneration);
        }
    }

    private static void SafeDisposeScore(IDisposable? score, string source)
    {
        if (score == null)
            return;
        try
        {
            score.Dispose();
        }
        catch (Exception e)
        {
            BreathVolumeDiagnosticsLog.Write(
                $"score dispose failed source={source}: {e.GetType().Name}: {e.Message}");
        }
    }

    public static bool RefreshRegions(WIVSMSequence sequence, WIVSMMidiPart part, IVSMScoreEnumerator score)
        => RefreshRegions(
            sequence,
            part,
            score,
            new ScoreSignature(ScoreSourceKind.External, score?.NumScores ?? 0, 0, null, 0, 0));

    private static bool RefreshRegions(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        IVSMScoreEnumerator score,
        ScoreSignature signature,
        PartState? expectedState = null,
        int expectedGeneration = 0)
    {
        if (!IsRegionRefreshCurrent(part, expectedState, expectedGeneration))
            return false;
        if (!Settings.IndividualBreathVolume || score == null || score.NumScores <= 0 || sequence.NumSampleInFrame <= 0)
            return StoreRegions(
                part, Array.Empty<BreathRegion>(), score?.NumScores ?? 0, signature,
                expectedState, expectedGeneration);

        bool? cachedResult = null;
        var statusChanged = false;
        lock (Sync)
        {
            if (Parts.TryGetValue((IntPtr)part, out var cached) &&
                cached.ScoreCount == score.NumScores &&
                cached.ScoreSignature == signature &&
                cached.Generation != 0)
            {
                statusChanged = cached.RegionStatus != BreathRegionStatus.Ready;
                cached.RegionStatus = BreathRegionStatus.Ready;
                cachedResult = cached.Regions.Count > 0;
            }
        }
        if (cachedResult.HasValue)
        {
            if (statusChanged)
                NotifyChanged(BreathVolumeChangeKind.Regions, part);
            return cachedResult.Value;
        }

        var sampleRate = (double)sequence.GetSamplingRate();
        var waveBeginTime = sequence.GetTimeFromTick(part.AbsBeginTick) - sequence.PresendTimeSec;
        var nativeRanges = ScanNativeBreathRanges(
            part, score.NumScores, sequence.NumSampleInFrame);
        IEnumerable<BreathSampleRange> detectedRanges = nativeRanges.Count > 0
            ? nativeRanges
            : ScanTraditionalBreathRanges(part);
        if (IsAiPart(part) && IsAutomaticBreathEnabled(part, out _))
        {
            detectedRanges = ScanBreathRanges(score, sequence.NumSampleInFrame)
                .Concat(detectedRanges);
        }
        var ranges = MergeBreathRanges(detectedRanges);

        var notes = EnumerateNotes(part)
            .Select(note =>
            {
                RegisterNote(note, sequence);
                return new NotePosition(note, GetNoteBeginSample(sequence, part, note));
            })
            .OrderBy(item => item.BeginSample)
            .ToArray();
        var regions = new List<BreathRegion>();

        foreach (var range in ranges)
        {
            var next = notes.FirstOrDefault(note => note.BeginSample >= range.EndSample);
            if (next.Note == null)
                next = notes.FirstOrDefault(note => note.BeginSample >= range.BeginSample);
            if (next.Note == null)
                continue;

            var beginTime = waveBeginTime + range.BeginSample / sampleRate;
            var endTime = waveBeginTime + range.EndSample / sampleRate;
            var beginTick = Math.Max(0, sequence.GetTickFromTime(beginTime).Value);
            var endTick = Math.Max(beginTick, sequence.GetTickFromTime(endTime).Value);
            regions.Add(new BreathRegion(
                range.BeginSample,
                range.EndSample,
                beginTick,
                endTick,
                next.Note.CppObjPtr));
        }

        BreathVolumeDiagnosticsLog.Write(
            $"regions scoreFrames={score.NumScores} samplesPerFrame={sequence.NumSampleInFrame} " +
            $"ranges={ranges.Count} notes={notes.Length} mappedRegions={regions.Count}");
        BreathVolumeDiagnosticsLog.WriteRegions(
            (IntPtr)part,
            signature.Kind.ToString(),
            score.NumScores,
            ranges.Count,
            notes.Length,
            regions.Count);

        var preserveStableAutomaticRegions = ranges.Count > 0 &&
                                             IsAutomaticBreathEnabled(part, out _);
        return StoreRegions(
            part, regions, score.NumScores, signature, expectedState, expectedGeneration,
            preserveStableAutomaticRegions);
    }

    public static void BeginRenderedBlock(WIVSMMidiPart part, int progress)
    {
        lock (Sync)
        {
            var state = GetPartState(part);
            if (state.LastProgress >= 0 && progress >= state.LastProgress)
            {
                state.LastProgress = progress;
                return;
            }

            state.Generation++;
            state.ScoreCount = -1;
            state.RegionStatus = BreathRegionStatus.Loading;
            state.StableRegions = state.Regions.ToList();
            state.RenderMarkerRegions.Clear();
            state.LastProgress = progress;
            RemoveCacheCore(state);
        }
    }

    private static int DrainNativeBreathMarkers(
        string phase,
        WIVSMMidiPart? activePart = null,
        bool discardActivePart = false)
    {
        IReadOnlyList<NativeBreathMarker> markers = NativeBreathCapture.ReadPending();
        if (markers.Count == 0)
        {
            BreathVolumeDiagnosticsLog.WriteNativeSnapshot(phase);
            return 0;
        }

        var activeHandle = activePart == null ? IntPtr.Zero : (IntPtr)activePart;
        var matched = 0;
        var discarded = 0;
        var unmatched = 0;
        var changedParts = new HashSet<WIVSMMidiPart>();
        lock (Sync)
        {
            foreach (var marker in markers)
            {
                if (discardActivePart && marker.PartHandle == activeHandle)
                {
                    discarded++;
                    continue;
                }

                if (marker.PartHandle == IntPtr.Zero ||
                    !Parts.TryGetValue(marker.PartHandle, out var state) ||
                    state.Part == null)
                {
                    unmatched++;
                    continue;
                }

                if (state.NativeBreathSequences.Add(marker.Sequence))
                {
                    state.NativeBreathMarkers.Add(marker);
                    state.ScoreCount = -1;
                    state.ScoreSignature = default;
                    changedParts.Add(state.Part);
                    matched++;
                }
            }
        }

        BreathVolumeDiagnosticsLog.WriteMarkers(phase, markers);
        BreathVolumeDiagnosticsLog.Write(
            $"native mixer drain phase={phase} total={markers.Count} matched={matched} " +
            $"discarded={discarded} unmatched={unmatched}");
        BreathVolumeDiagnosticsLog.WriteNativeSnapshot(phase, force: true);
        foreach (var part in changedParts)
            NotifyChanged(BreathVolumeChangeKind.Regions, part);
        return matched;
    }

    public static void StartRender(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        NativeBreathCapture.TryInitialize();
        DrainNativeBreathMarkers("renderStart", part, discardActivePart: true);
        var traditionalEnabled = IsAutomaticBreathEnabled(part, out var breathEffect);
        BreathVolumeDiagnosticsLog.Write(
            $"render start notes={part.NumNotes} automaticBreath={traditionalEnabled} {breathEffect}");
        lock (Sync)
        {
            var state = GetPartState(part);
            state.Sequence = sequence;
            state.Part = part;
            state.Generation++;
            state.ScoreCount = -1;
            state.RegionStatus = BreathRegionStatus.Loading;
            state.StableRegions = state.Regions.ToList();
            state.RenderMarkerRegions.Clear();
            state.NativeBreathMarkers.Clear();
            state.TraditionalBreathRanges.Clear();
            state.TraditionalWaveDetectionAttempted = false;
            state.NativeBreathSequences.Clear();
            state.RegionRefreshGeneration++;
            state.RegionRefreshPending = false;
            state.RebuildAfterRegionRefresh = false;
            state.LastProgress = 0;
            Interlocked.Increment(ref state.RebuildGeneration);
            RemoveCacheCore(state);
        }
        NotifyChanged(BreathVolumeChangeKind.Regions, part);
    }

    public static void CancelRender(WIVSMMidiPart part)
    {
        DrainNativeBreathMarkers("renderCancel", part, discardActivePart: true);
        lock (Sync)
        {
            var state = GetPartState(part);
            state.ScoreCount = -1;
            state.RegionRefreshGeneration++;
            state.RegionRefreshPending = false;
            state.RebuildAfterRegionRefresh = false;
            state.RenderMarkerRegions.Clear();
            state.NativeBreathMarkers.Clear();
            state.TraditionalBreathRanges.Clear();
            state.TraditionalWaveDetectionAttempted = false;
            state.NativeBreathSequences.Clear();
            state.Regions = state.StableRegions.ToList();
            state.RegionStatus = state.StableRegions.Count > 0
                ? BreathRegionStatus.Ready
                : BreathRegionStatus.Unknown;
            state.LastProgress = -1;
        }
        NotifyChanged(BreathVolumeChangeKind.Regions, part);
    }

    public static void ProcessRenderedBlock(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        WIVSMAudioBufferList buffers,
        VSMScoreList score,
        int progress)
    {
        if (!Settings.IndividualBreathVolume || buffers == null || score == null)
            return;

        var nativeMixerAvailable = UseNativeBreathMixer(part);
        DrainNativeBreathMarkers("renderBlock");
        BeginRenderedBlock(part, progress);
        if (!nativeMixerAvailable && !HasNativeBreathMarkers(part))
            DetectTraditionalBreathRanges(sequence, part, buffers, score, progress);
        RefreshRegions(
            sequence,
            part,
            score,
            new ScoreSignature(ScoreSourceKind.RenderBlock, score.NumScores, progress, null, 0, 0));
    }

    public static void CompleteRender(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        if (!Settings.IndividualBreathVolume)
            return;

        try
        {
            DrainNativeBreathMarkers("renderComplete");
            lock (Sync)
            {
                var state = GetPartState(part);
                state.LastProgress = -1;
                state.ScoreCount = -1;
                RemoveCacheCore(state);
            }

            RefreshRegionsAsync(sequence, part, rebuildAfterRefresh: true);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", e.Message));
            RemoveCache(part);
        }
    }

    public static void RequestRebuild(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        if (!Settings.IndividualBreathVolume)
            return;

        var state = GetPartState(part);
        var generation = Interlocked.Increment(ref state.RebuildGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                // Parameter edits can arrive much faster than rebuilding a wave file.
                // Let a short burst settle and discard superseded generations before
                // they enter the per-part rebuild lock.
                await Task.Delay(250).ConfigureAwait(false);
                if (Volatile.Read(ref state.RebuildGeneration) != generation)
                    return;
                RebuildNow(sequence, part, generation);
            }
            catch (Exception e)
            {
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", e.Message));
                RemoveCache(part);
                RefreshAudioPlacement(sequence, part);
            }
            finally
            {
                RebuildCompleted?.Invoke(part, generation, Volatile.Read(ref state.RebuildGeneration) == generation);
            }
        });
    }

    internal static void CompleteExternalMutation(WIVSMSequence sequence, IEnumerable<WIVSMMidiPart> parts)
    {
        WIVSMMidiPart[] targets = parts.Distinct().ToArray();
        NotifyChanged(BreathVolumeChangeKind.Values, null);
        foreach (WIVSMMidiPart part in targets)
            RequestRebuild(sequence, part);
    }

    public static string SubstituteWavePath(WIVSMMidiPart part, string originalPath)
    {
        if (_wavePathBypass != 0 || !Settings.IndividualBreathVolume || part == null)
            return originalPath;

        lock (Sync)
        {
            if (!Parts.TryGetValue((IntPtr)part, out var state) || state.Cache == null)
                return originalPath;

            var cache = state.Cache;
            return cache.SourcePath.Equals(originalPath, StringComparison.OrdinalIgnoreCase) &&
                   cache.SourceLength == SafeLength(originalPath) &&
                   cache.SourceWriteTimeUtc == SafeWriteTime(originalPath) &&
                   File.Exists(cache.DerivedPath)
                ? cache.DerivedPath
                : originalPath;
        }
    }

    public static string ReadOriginalWavePath(WIVSMMidiPart part)
    {
        try
        {
            _wavePathBypass++;
            return part.WaveFilePath;
        }
        finally
        {
            _wavePathBypass--;
        }
    }

    public static void DisableAndCleanup()
    {
        NativeBreathCapture.ClearPending();
        List<(WIVSMSequence Sequence, WIVSMMidiPart Part)> refresh;
        lock (Sync)
        {
            refresh = Parts.Values
                .Where(state => state.Cache != null && state.Sequence != null && state.Part != null)
                .Select(state => (state.Sequence!, state.Part!))
                .ToList();
            foreach (var state in Parts.Values)
            {
                Interlocked.Increment(ref state.RebuildGeneration);
                RemoveCacheCore(state);
            }
            Selection.Clear();
        }

        foreach (var item in refresh)
            RefreshAudioPlacement(item.Sequence, item.Part);
        NotifyChanged(BreathVolumeChangeKind.Display, null);
    }

    public static void CloseSequence(WIVSMSequence sequence)
    {
        var noteHandles = new HashSet<IntPtr>();
        var partHandles = new HashSet<IntPtr>();
        CollectSequenceHandles(sequence, noteHandles, partHandles);

        lock (Sync)
        {
            var sequenceHandle = (IntPtr)sequence;
            noteHandles.UnionWith(NoteOwners.Where(pair => pair.Value == sequenceHandle)
                .Select(pair => pair.Key.Handle));
            ReleaseHandlesCore(noteHandles);
            foreach (var pair in Parts.Where(pair =>
                         partHandles.Contains(pair.Key) || pair.Value.Sequence?.Equals(sequence) == true).ToArray())
            {
                Interlocked.Increment(ref pair.Value.RebuildGeneration);
                RemoveCacheCore(pair.Value);
                Parts.Remove(pair.Key);
            }
            Histories.Remove((IntPtr)sequence);
        }
    }

    public static BreathProjectData ReadProjectData(string filePath)
        => BreathProjectArchive.Read(filePath);

    public static void LoadProjectData(WIVSMSequence sequence, BreathProjectData? data)
    {
        var noteHandles = new HashSet<IntPtr>();
        var partHandles = new HashSet<IntPtr>();
        CollectSequenceHandles(sequence, noteHandles, partHandles);

        lock (Sync)
        {
            foreach (var pair in Parts.Where(pair =>
                         partHandles.Contains(pair.Key) || pair.Value.Sequence?.Equals(sequence) == true).ToArray())
            {
                Interlocked.Increment(ref pair.Value.RebuildGeneration);
                RemoveCacheCore(pair.Value);
                Parts.Remove(pair.Key);
            }
            var sequenceHandle = (IntPtr)sequence;
            noteHandles.UnionWith(NoteOwners.Where(pair => pair.Value == sequenceHandle)
                .Select(pair => pair.Key.Handle));
            ReleaseHandlesCore(noteHandles);
        }
        ResetHistory(sequence);
        if (data == null || data.Version != 1 || data.Entries == null || data.NativeMarkers == null)
        {
            if (data is { Version: not 1 })
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_UnknownVersion", data.Version));
            return;
        }

        foreach (var entry in data.Entries)
        {
            if (entry == null)
                continue;
            var note = FindPersistedNote(sequence, entry);
            if (note != null)
            {
                RegisterNote(note, sequence);
                SetValueCore(note.CppObjPtr, (byte)Math.Clamp(entry.Value, MinValue, MaxValue));
            }
        }

        var persistedSequence = ulong.MaxValue;
        foreach (var marker in data.NativeMarkers)
        {
            if (marker == null || marker.Track < 0 || marker.Part < 0 ||
                marker.BeginFrame < 0 || marker.EndFrame <= marker.BeginFrame ||
                marker.EndFrame > BreathProjectArchive.MaxNativeFrame)
                continue;
            try
            {
                if (sequence.GetTrack(checked((ulong)marker.Track)) is not WIVSMMidiTrack track ||
                    track.GetPart(checked((ulong)marker.Part)) is not WIVSMMidiPart part)
                    continue;

                lock (Sync)
                {
                    var state = GetPartState(part);
                    state.Sequence = sequence;
                    state.Part = part;
                    if (state.NativeBreathMarkers.Any(existing =>
                            existing.BeginFrame == marker.BeginFrame &&
                            existing.EndFrame == marker.EndFrame))
                        continue;
                    var nativeMarker = new NativeBreathMarker(
                        persistedSequence--, (IntPtr)part,
                        marker.BeginFrame, marker.EndFrame);
                    state.NativeBreathMarkers.Add(nativeMarker);
                    state.NativeBreathSequences.Add(nativeMarker.Sequence);
                    state.ScoreCount = -1;
                    state.RegionStatus = BreathRegionStatus.Unknown;
                }
            }
            catch
            {
                // A stale track/part index is ignored; note values still load independently.
            }
        }

        lock (Sync)
        {
            var history = GetHistory(sequence);
            history.Revision = 0;
            history.SavedRevision = 0;
        }
        NotifyChanged(BreathVolumeChangeKind.Values, null);
    }

    public static void RebuildProject(WIVSMSequence sequence)
    {
        if (!Settings.IndividualBreathVolume)
            return;

        for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
        {
            RebuildTrack(sequence, sequence.GetTrack(trackIndex));
        }
    }

    public static void RebuildTrack(WIVSMSequence sequence, WIVSMTrack? track)
    {
        if (!Settings.IndividualBreathVolume || track is not WIVSMMidiTrack midiTrack)
            return;

        for (ulong partIndex = 0; partIndex < midiTrack.NumParts; partIndex++)
        {
            if (midiTrack.GetPart(partIndex) is not WIVSMMidiPart part ||
                !EnumerateNotes(part).Any(note => GetValue(note) != DefaultValue))
                continue;
            RefreshRegionsAsync(sequence, part, rebuildAfterRefresh: true);
        }
    }

    public static BreathProjectData BuildProjectData(WIVSMSequence sequence)
    {
        var data = new BreathProjectData();
        for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
        {
            if (sequence.GetTrack(trackIndex) is not WIVSMMidiTrack track)
                continue;

            for (ulong partIndex = 0; partIndex < track.NumParts; partIndex++)
            {
                if (track.GetPart(partIndex) is not WIVSMMidiPart part)
                    continue;

                var occurrences = new Dictionary<(long Tick, int Pitch), int>();
                for (ulong noteIndex = 0; noteIndex < part.NumNotes; noteIndex++)
                {
                    var note = part.GetNote(noteIndex);
                    if (note == null)
                        continue;
                    var key = (note.RelPosTick.Value, note.NoteNumber);
                    occurrences.TryGetValue(key, out var occurrence);
                    occurrences[key] = occurrence + 1;
                    var value = GetValue(note);
                    if (value == DefaultValue)
                        continue;

                    data.Entries.Add(new BreathProjectEntry
                    {
                        Track = checked((int)trackIndex),
                        Part = checked((int)partIndex),
                        Note = checked((int)noteIndex),
                        RelPosTick = note.RelPosTick.Value,
                        NoteNumber = note.NoteNumber,
                        Occurrence = occurrence,
                        Value = value
                    });
                }

                NativeBreathMarker[] nativeMarkers;
                lock (Sync)
                    nativeMarkers = Parts.TryGetValue((IntPtr)part, out var state)
                        ? state.NativeBreathMarkers.ToArray()
                        : Array.Empty<NativeBreathMarker>();
                foreach (var marker in nativeMarkers
                             .Where(marker => marker.BeginFrame >= 0 &&
                                              marker.EndFrame > marker.BeginFrame &&
                                              marker.EndFrame <= BreathProjectArchive.MaxNativeFrame)
                             .DistinctBy(marker => (marker.BeginFrame, marker.EndFrame)))
                {
                    data.NativeMarkers.Add(new BreathProjectNativeMarker
                    {
                        Track = checked((int)trackIndex),
                        Part = checked((int)partIndex),
                        BeginFrame = marker.BeginFrame,
                        EndFrame = marker.EndFrame
                    });
                }
            }
        }

        return data;
    }

    public static void WriteProjectData(string filePath, WIVSMSequence sequence)
    {
        WriteProjectData(filePath, sequence, BuildProjectData(sequence));
    }

    public static void WriteProjectData(string filePath, WIVSMSequence sequence, BreathProjectData data)
    {
        BreathProjectArchive.Write(filePath, data);
        MarkSaved(sequence);
    }

    public static bool RequiresProjectDataWrite(WIVSMSequence sequence, BreathProjectData data)
        => data.Entries is { Count: > 0 } || data.NativeMarkers is { Count: > 0 } ||
           IsProjectDirty(sequence);

    public static void OnNativeCommit(WIVSMSequence sequence, bool updateHistory, bool result)
    {
        if (!updateHistory || !result)
            return;

        PruneSequence(sequence);

        lock (Sync)
        {
            var history = GetHistory(sequence);
            history.Undo.Add(TimelineEntry.Native);
            history.Redo.Clear();
        }
    }

    internal static void PushExternalHistory(
        WIVSMSequence sequence,
        ICustomParameterHistoryEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (Sync)
        {
            var history = GetHistory(sequence);
            history.Undo.Add(new TimelineEntry(null, edit));
            history.Redo.Clear();
            history.Revision++;
            RegisterShiftDiagnosticsLog.Write(
                $"history timeline push sequence=0x{((IntPtr)sequence).ToInt64():X} " +
                $"undo={history.Undo.Count} redo={history.Redo.Count}");
        }
    }

    public static bool CanUndo(WIVSMSequence sequence, bool nativeResult)
    {
        lock (Sync)
            return nativeResult || GetHistory(sequence).Undo.LastOrDefault().HasEdit;
    }

    public static bool CanRedo(WIVSMSequence sequence, bool nativeResult)
    {
        lock (Sync)
            return nativeResult || GetHistory(sequence).Redo.LastOrDefault().HasEdit;
    }

    public static bool IsProjectDirty(WIVSMSequence sequence)
    {
        lock (Sync)
            return Histories.TryGetValue((IntPtr)sequence, out var history) &&
                   history.Revision != history.SavedRevision;
    }

    public static bool HandleUndo(WIVSMSequence sequence)
        => HandleHistory(sequence, undo: true);

    public static bool HandleRedo(WIVSMSequence sequence)
        => HandleHistory(sequence, undo: false);

    public static bool HandlePatchOwnedUndo(WIVSMSequence sequence)
        => HandlePatchOwnedHistory(sequence, undo: true);

    public static bool HandlePatchOwnedRedo(WIVSMSequence sequence)
        => HandlePatchOwnedHistory(sequence, undo: false);

    public static void ResetHistory(WIVSMSequence sequence)
    {
        lock (Sync)
            Histories[(IntPtr)sequence] = new SequenceHistory();
    }

    public static void MarkSaveFailed(WIVSMSequence sequence, Exception exception)
    {
        sequence.IsModifiedOutsideOfEditHistory = true;
        Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_SaveFailed", exception.Message));
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            Debug.ShowMessageBox(TranslationManager.Tr("VOCALOIDPatcher_BreathVolume_SaveFailed"))));
    }

    private static bool HandleHistory(WIVSMSequence sequence, bool undo)
    {
        TimelineEntry entry;
        ICustomParameterHistoryEdit? externalEdit = null;
        lock (Sync)
        {
            var history = GetHistory(sequence);
            var source = undo ? history.Undo : history.Redo;
            var destination = undo ? history.Redo : history.Undo;
            if (source.Count == 0)
                return false;

            entry = source[^1];
            source.RemoveAt(source.Count - 1);
            destination.Add(entry);
            if (!entry.HasEdit)
                return false;

            history.Revision += undo ? -1 : 1;
            if (entry.Edit is { } edit)
            {
                foreach (var pair in undo ? edit.Before : edit.After)
                    if (IsActiveNoteKeyCore(pair.Key))
                        SetValueCore(pair.Key, pair.Value);
            }
            else
            {
                externalEdit = entry.External;
            }
        }

        if (entry.Edit != null)
        {
            NotifyChanged(BreathVolumeChangeKind.Values, null);
            RebuildAllCached(sequence);
        }
        else
        {
            try
            {
                // External parameters have their own synchronization and may notify UI code.
                // Never invoke them while holding the BVL history lock: apart from lock-order
                // hazards, callbacks must be allowed to complete before AfterApply refreshes
                // the editor from their new values.
                if (externalEdit == null)
                    throw new InvalidOperationException("The custom parameter history entry is missing.");
                if (undo)
                    externalEdit.ApplyBefore();
                else
                    externalEdit.ApplyAfter();
            }
            catch (Exception exception)
            {
                RollBackHistoryMove(sequence, entry, undo);
                RegisterShiftDiagnosticsLog.Write(
                    $"history replay failed direction={(undo ? "undo" : "redo")} " +
                    $"sequence=0x{((IntPtr)sequence).ToInt64():X} " +
                    $"exception={exception}");
                Debug.Print($"Custom parameter {(undo ? "undo" : "redo")} failed: " +
                            $"{exception.GetType().Name}: {exception.Message}");
                // This was a patch-owned entry. Do not fall through to native Undo/Redo after
                // restoring it, or an unrelated native edit would be consumed instead.
                return true;
            }
        }

        try
        {
            entry.External?.AfterApply();
        }
        catch (Exception exception)
        {
            // The value has already been restored. A refresh/render failure must not corrupt
            // timeline ordering or cause the native history underneath it to be consumed.
            Debug.Print($"Custom parameter history refresh failed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
        }
        return true;
    }

    private static bool HandlePatchOwnedHistory(WIVSMSequence sequence, bool undo)
    {
        string top;
        int sourceCount;
        int destinationCount;
        lock (Sync)
        {
            var history = GetHistory(sequence);
            var source = undo ? history.Undo : history.Redo;
            var destination = undo ? history.Redo : history.Undo;
            sourceCount = source.Count;
            destinationCount = destination.Count;
            top = source.Count == 0 ? "empty" : source[^1].External != null
                ? "external" : source[^1].Edit != null ? "bvl" : "native";
            if (source.Count == 0 || !source[^1].HasEdit)
            {
                RegisterShiftDiagnosticsLog.Write(
                    $"history command direction={(undo ? "undo" : "redo")} " +
                    $"sequence=0x{((IntPtr)sequence).ToInt64():X} top={top} " +
                    $"source={sourceCount} destination={destinationCount} handled=False");
                return false;
            }
        }

        RegisterShiftDiagnosticsLog.Write(
            $"history command direction={(undo ? "undo" : "redo")} " +
            $"sequence=0x{((IntPtr)sequence).ToInt64():X} top={top} " +
            $"source={sourceCount} destination={destinationCount} handled=True");
        return HandleHistory(sequence, undo);
    }

    private static void RollBackHistoryMove(WIVSMSequence sequence, TimelineEntry entry, bool undo)
    {
        lock (Sync)
        {
            var history = GetHistory(sequence);
            var source = undo ? history.Undo : history.Redo;
            var destination = undo ? history.Redo : history.Undo;
            if (destination.Count > 0 && destination[^1].Equals(entry))
            {
                destination.RemoveAt(destination.Count - 1);
                source.Add(entry);
                history.Revision += undo ? 1 : -1;
                RegisterShiftDiagnosticsLog.Write(
                    $"history timeline rollback direction={(undo ? "undo" : "redo")} " +
                    $"sequence=0x{((IntPtr)sequence).ToInt64():X} " +
                    $"source={source.Count} destination={destination.Count}");
            }
        }
    }

    private static void MarkSaved(WIVSMSequence sequence)
    {
        lock (Sync)
        {
            var history = GetHistory(sequence);
            history.SavedRevision = history.Revision;
        }
    }

    private static bool StoreRegions(
        WIVSMMidiPart part,
        IReadOnlyList<BreathRegion> regions,
        long scoreCount,
        ScoreSignature signature,
        PartState? expectedState = null,
        int expectedGeneration = 0,
        bool preserveStableAutomaticRegions = false)
    {
        var detectedRegions = regions.ToList();
        var nextRegions = detectedRegions;
        var liveHandles = preserveStableAutomaticRegions && detectedRegions.Count > 0
            ? EnumerateNotes(part).Select(note => note.CppObjPtr).ToHashSet()
            : null;
        var nextStatus = signature.Kind == ScoreSourceKind.Faulted
            ? BreathRegionStatus.Faulted
            : BreathRegionStatus.Ready;
        bool changed;
        lock (Sync)
        {
            PartState state;
            if (expectedState != null)
            {
                if (!Parts.TryGetValue((IntPtr)part, out var current) ||
                    !ReferenceEquals(current, expectedState) ||
                    current.RegionRefreshGeneration != expectedGeneration)
                    return false;
                state = current;
            }
            else
            {
                state = GetPartState(part);
            }

            if (liveHandles != null && state.StableRegions.Count > 0)
            {
                var detectedHandles = detectedRegions
                    .Select(region => region.NoteHandle)
                    .ToHashSet();
                var retainedRegions = state.StableRegions
                    .Where(region => liveHandles.Contains(region.NoteHandle) &&
                                     !detectedHandles.Contains(region.NoteHandle))
                    .ToArray();
                if (retainedRegions.Length > 0)
                {
                    detectedRegions = detectedRegions
                        .Concat(retainedRegions)
                        .OrderBy(region => region.BeginSample)
                        .ThenBy(region => region.EndSample)
                        .ToList();
                    nextRegions = detectedRegions;
                    BreathVolumeDiagnosticsLog.Write(
                        $"regions reconciled detected={regions.Count} retained={retainedRegions.Length} " +
                        $"stable={state.StableRegions.Count}");
                }
            }

            var transientScore = signature.Kind is ScoreSourceKind.RenderBlock
                or ScoreSourceKind.CombinedRendering
                or ScoreSourceKind.Holding;
            if (transientScore)
            {
                if (detectedRegions.Count > 0)
                {
                    var refreshedHandles = detectedRegions
                        .Select(region => region.NoteHandle)
                        .ToHashSet();
                    state.RenderMarkerRegions = state.RenderMarkerRegions
                        .Where(region => !refreshedHandles.Contains(region.NoteHandle))
                        .Concat(detectedRegions)
                        .Distinct()
                        .OrderBy(region => region.BeginSample)
                        .ToList();
                }
                nextRegions = state.RenderMarkerRegions.Count > 0
                    ? state.RenderMarkerRegions.ToList()
                    : state.StableRegions.ToList();
            }
            else if (signature.Kind == ScoreSourceKind.RenderedFile &&
                     detectedRegions.Count == 0 && state.RenderMarkerRegions.Count > 0)
            {
                nextRegions = state.RenderMarkerRegions.ToList();
            }
            else if (signature.Kind == ScoreSourceKind.Unavailable)
            {
                nextRegions = state.RenderMarkerRegions.Count > 0
                    ? state.RenderMarkerRegions.ToList()
                    : state.StableRegions.ToList();
            }

            changed = !state.Regions.SequenceEqual(nextRegions) || state.RegionStatus != nextStatus;
            state.ScoreCount = scoreCount;
            state.ScoreSignature = signature;
            state.Regions = nextRegions;
            state.RegionStatus = nextStatus;
            if (!transientScore &&
                signature.Kind != ScoreSourceKind.Unavailable &&
                signature.Kind != ScoreSourceKind.Faulted)
            {
                state.StableRegions = nextRegions.ToList();
                state.RenderMarkerRegions.Clear();
            }
            if (state.Generation == 0)
                state.Generation = 1;
        }

        if (changed)
            NotifyChanged(BreathVolumeChangeKind.Regions, part);
        return nextRegions.Count > 0;
    }

    private static bool MarkRegionRefreshFaulted(
        WIVSMMidiPart part,
        PartState? expectedState = null,
        int expectedGeneration = 0)
    {
        var changed = false;
        var hasRegions = false;
        lock (Sync)
        {
            PartState state;
            if (expectedState != null)
            {
                if (!Parts.TryGetValue((IntPtr)part, out var current) ||
                    !ReferenceEquals(current, expectedState) ||
                    current.RegionRefreshGeneration != expectedGeneration)
                    return false;
                state = current;
            }
            else
            {
                state = GetPartState(part);
            }
            changed = state.RegionStatus != BreathRegionStatus.Faulted;
            state.RegionStatus = BreathRegionStatus.Faulted;
            state.ScoreCount = -1;
            state.ScoreSignature = ScoreSignature.Faulted;
            hasRegions = state.Regions.Count > 0;
        }

        if (changed)
            NotifyChanged(BreathVolumeChangeKind.Regions, part);
        return hasRegions;
    }

    private static bool IsRegionRefreshCurrent(
        WIVSMMidiPart part,
        PartState? expectedState,
        int expectedGeneration)
    {
        if (expectedState == null)
            return true;
        lock (Sync)
            return Parts.TryGetValue((IntPtr)part, out var current) &&
                   ReferenceEquals(current, expectedState) &&
                   current.RegionRefreshGeneration == expectedGeneration;
    }

    private static IReadOnlyList<BreathSampleRange> ScanBreathRanges(
        IVSMScoreEnumerator score,
        long samplesPerFrame)
    {
        var ranges = new List<BreathSampleRange>();
        var names = new Dictionary<IntPtr, string>();
        long pointerFrames = 0;
        long namedFrames = 0;
        long breathFrames = 0;
        long beginFrame = -1;
        for (long frame = 0; frame < score.NumScores; frame++)
        {
            var phoneme = score.ScoreAtIndex(frame).PhnDur;
            if (phoneme.FromPhU != IntPtr.Zero || phoneme.ToPhU != IntPtr.Zero)
                pointerFrames++;
            var fromName = GetNativePhonemeName(phoneme.FromPhU, names);
            var toName = GetNativePhonemeName(phoneme.ToPhU, names);
            if (!string.IsNullOrEmpty(fromName) || !string.IsNullOrEmpty(toName))
                namedFrames++;
            var isBreath = BreathPhonemeClassifier.IsNativeBreathPhoneme(fromName) ||
                           BreathPhonemeClassifier.IsNativeBreathPhoneme(toName);
            if (isBreath)
                breathFrames++;
            if (isBreath && beginFrame < 0)
            {
                beginFrame = frame;
            }
            else if (!isBreath && beginFrame >= 0)
            {
                ranges.Add(new BreathSampleRange(
                    checked(beginFrame * samplesPerFrame),
                    checked(frame * samplesPerFrame)));
                beginFrame = -1;
            }
        }

        if (beginFrame >= 0)
            ranges.Add(new BreathSampleRange(
                checked(beginFrame * samplesPerFrame),
                checked(score.NumScores * samplesPerFrame)));
        BreathVolumeDiagnosticsLog.Write(
            $"score phonemes frames={score.NumScores} pointerFrames={pointerFrames} " +
            $"namedFrames={namedFrames} breathFrames={breathFrames}");
        return ranges;
    }

    private static string GetNativePhonemeName(IntPtr pointer, IDictionary<IntPtr, string> names)
    {
        if (pointer == IntPtr.Zero)
            return string.Empty;
        if (!names.TryGetValue(pointer, out var name))
        {
            name = NativePhonemeInspector.ReadName(pointer);
            names[pointer] = name;
        }
        return name;
    }

    private static IReadOnlyList<BreathSampleRange> ScanTraditionalBreathRanges(
        WIVSMMidiPart part)
    {
        lock (Sync)
            return Parts.TryGetValue((IntPtr)part, out var state)
                ? state.TraditionalBreathRanges.ToArray()
                : Array.Empty<BreathSampleRange>();
    }

    private static bool HasNativeBreathMarkers(WIVSMMidiPart part)
    {
        lock (Sync)
            return Parts.TryGetValue((IntPtr)part, out var state) &&
                   state.NativeBreathMarkers.Count > 0;
    }

    private static IReadOnlyList<BreathSampleRange> ScanNativeBreathRanges(
        WIVSMMidiPart part,
        long scoreCount,
        long samplesPerFrame)
    {
        NativeBreathMarker[] markers;
        lock (Sync)
            markers = Parts.TryGetValue((IntPtr)part, out var state)
                ? state.NativeBreathMarkers.ToArray()
                : Array.Empty<NativeBreathMarker>();

        var ranges = new List<BreathSampleRange>(markers.Length);
        foreach (var marker in markers)
        {
            if (NativeBreathRangeResolver.TryResolve(
                    marker.BeginFrame, marker.EndFrame, scoreCount, samplesPerFrame,
                    out var beginSample, out var endSample))
                ranges.Add(new BreathSampleRange(beginSample, endSample));
        }

        var merged = MergeBreathRanges(ranges);
        if (markers.Length > 0)
            BreathVolumeDiagnosticsLog.Write(
                $"native mixer ranges markers={markers.Length} resolved={ranges.Count} " +
                $"merged={merged.Count} scoreFrames={scoreCount} samplesPerFrame={samplesPerFrame}");
        return merged;
    }

    private static void DetectTraditionalBreathRanges(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        WIVSMAudioBufferList buffers,
        IVSMScoreEnumerator score,
        int progress)
    {
        const int maximumDetectionFrames = 2_000_000;
        var started = Stopwatch.GetTimestamp();

        try
        {
            if (!IsAutomaticBreathEnabled(part, out var breathEffect))
            {
                BreathVolumeDiagnosticsLog.Write(
                    $"traditional pcm progress={progress} skipped {breathEffect}");
                return;
            }

            var scoreCount = score.NumScores;
            var samplesPerFrame = sequence.NumSampleInFrame;
            var sampleRate = (int)sequence.GetSamplingRate();
            if (scoreCount <= 0 || scoreCount > maximumDetectionFrames ||
                samplesPerFrame <= 0 || sampleRate <= 0)
            {
                BreathVolumeDiagnosticsLog.Write(
                    $"traditional pcm progress={progress} skipped invalidShape " +
                    $"scoreFrames={scoreCount} samplesPerFrame={samplesPerFrame} sampleRate={sampleRate}");
                return;
            }

            var frameCount = checked((int)scoreCount);
            var frameRms = new float[frameCount];
            var framePeaks = new float[frameCount];
            var pitchedFrames = BuildNotePitchedFrames(
                sequence, part, frameCount, samplesPerFrame);
            var audioSamples = checked((long)buffers.NumSamples);

            for (var frame = 0; frame < frameCount; frame++)
            {
                if (pitchedFrames[frame])
                    continue;

                var beginSample = checked(frame * samplesPerFrame);
                if (beginSample >= audioSamples)
                    continue;
                var endSample = Math.Min(audioSamples, beginSample + samplesPerFrame);
                var thumb = new VSMAudioThumb();
                if (!buffers.ThumbWithRange(beginSample, endSample, ref thumb))
                    continue;

                var peak = TraditionalBreathDetector.NormalizeThumbnailPeak(thumb.Min, thumb.Max);
                framePeaks[frame] = peak;
                // The native thumbnail API exposes extrema rather than RMS, so this
                // path deliberately uses only the detector's peak threshold.
            }

            var detection = TraditionalBreathDetector.Detect(
                frameRms, framePeaks, pitchedFrames, samplesPerFrame, sampleRate);
            var detectedRanges = detection.Ranges
                .Select(range => new BreathSampleRange(range.BeginSample, range.EndSample))
                .ToArray();

            int storedRangeCount;
            lock (Sync)
            {
                var state = GetPartState(part);
                state.TraditionalBreathRanges = MergeBreathRanges(
                        state.TraditionalBreathRanges.Concat(detectedRanges))
                    .ToList();
                storedRangeCount = state.TraditionalBreathRanges.Count;
                state.ScoreCount = -1;
                state.ScoreSignature = default;
            }

            var ranges = string.Join(",", detectedRanges
                .Take(12)
                .Select(range => $"{range.BeginSample}..{range.EndSample}"));
            BreathVolumeDiagnosticsLog.Write(
                $"traditional pcm progress={progress} {breathEffect} scoreFrames={scoreCount} " +
                $"audioSamples={audioSamples} pitchedOnsets={detection.PitchedOnsets} " +
                $"evaluatedGaps={detection.EvaluatedGaps} candidates={detection.ActivityCandidates} " +
                $"rejectedShortActivity={detection.RejectedShortActivity} " +
                $"rejectedShortLead={detection.RejectedShortLead} " +
                $"rejectedPreviousTail={detection.RejectedPreviousTail} " +
                $"activeFrames={detection.ActiveFrames} " +
                $"levelSource=thumbPeak " +
                $"maxUnpitchedRms={detection.MaxUnpitchedRms:G6} " +
                $"maxUnpitchedPeak={detection.MaxUnpitchedPeak:G6} " +
                $"ranges={detectedRanges.Length} storedRanges={storedRangeCount} [{ranges}] " +
                $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            BreathVolumeDiagnosticsLog.WriteTraditionalDetection(
                "renderBlock",
                (IntPtr)part,
                scoreCount,
                audioSamples,
                samplesPerFrame,
                sampleRate,
                detection,
                storedRangeCount,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception e)
        {
            BreathVolumeDiagnosticsLog.Write(
                $"traditional pcm progress={progress} failed: {e.GetType().Name}: {e.Message} " +
                $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }
    }

    private static void DetectTraditionalBreathRangesFromWave(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        IVSMScoreEnumerator score,
        string wavePath,
        ScoreSignature signature,
        PartState? expectedState,
        int expectedGeneration)
    {
        const int maximumDetectionFrames = 2_000_000;
        var started = Stopwatch.GetTimestamp();

        try
        {
            if (!IsRegionRefreshCurrent(part, expectedState, expectedGeneration))
                return;

            var nativeMixerAvailable = UseNativeBreathMixer(part);
            if (nativeMixerAvailable || HasNativeBreathMarkers(part))
            {
                MarkTraditionalWaveDetectionAttempted(
                    part, signature, Array.Empty<BreathSampleRange>(),
                    expectedState, expectedGeneration);
                BreathVolumeDiagnosticsLog.Write(
                    $"traditional wave skipped nativeMixer={nativeMixerAvailable}");
                return;
            }

            lock (Sync)
            {
                if (Parts.TryGetValue((IntPtr)part, out var cached) &&
                    cached.TraditionalWaveDetectionAttempted &&
                    cached.TraditionalWaveSignature == signature)
                    return;
            }

            if (!IsAutomaticBreathEnabled(part, out var breathEffect))
            {
                MarkTraditionalWaveDetectionAttempted(
                    part, signature, Array.Empty<BreathSampleRange>(), expectedState, expectedGeneration);
                BreathVolumeDiagnosticsLog.Write($"traditional wave skipped {breathEffect}");
                return;
            }

            var scoreCount = score.NumScores;
            var samplesPerFrame = sequence.NumSampleInFrame;
            if (scoreCount <= 0 || scoreCount > maximumDetectionFrames || samplesPerFrame <= 0)
            {
                MarkTraditionalWaveDetectionAttempted(
                    part, signature, Array.Empty<BreathSampleRange>(), expectedState, expectedGeneration);
                BreathVolumeDiagnosticsLog.Write(
                    $"traditional wave skipped invalidShape scoreFrames={scoreCount} " +
                    $"samplesPerFrame={samplesPerFrame}");
                return;
            }
            if (string.IsNullOrEmpty(wavePath) || !File.Exists(wavePath))
                throw new IOException("The rendered wave is not available yet.");

            var wave = new WaveFile();
            var error = wave.ReadWave(wavePath);
            if (error != WaveFileError.None || wave.SampleRate <= 0 || wave.WaveData.Count == 0)
                throw new IOException($"The rendered wave is not readable yet ({error}).");

            var frameCount = checked((int)scoreCount);
            var frameRms = new float[frameCount];
            var framePeaks = new float[frameCount];
            var pitchedFrames = BuildNotePitchedFrames(
                sequence, part, frameCount, samplesPerFrame);
            var channelCount = Math.Min(wave.ChannelCount, wave.WaveData.Count);
            for (var frame = 0; frame < frameCount; frame++)
            {
                var beginSample = checked(frame * samplesPerFrame);
                var endSample = Math.Min(wave.NumSamples, beginSample + samplesPerFrame);
                double sumSquares = 0;
                long sampleCount = 0;
                float peak = 0;
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var samples = wave.WaveData[channel];
                    var channelEnd = Math.Min(endSample, samples.LongLength);
                    for (var sampleIndex = beginSample; sampleIndex < channelEnd; sampleIndex++)
                    {
                        var sample = samples[checked((int)sampleIndex)] / 32768f;
                        var absolute = Math.Abs(sample);
                        sumSquares += (double)sample * sample;
                        sampleCount++;
                        if (absolute > peak)
                            peak = absolute;
                    }
                }

                if (sampleCount > 0)
                    frameRms[frame] = (float)Math.Sqrt(sumSquares / sampleCount);
                framePeaks[frame] = peak;
            }

            var detection = TraditionalBreathDetector.Detect(
                frameRms, framePeaks, pitchedFrames, samplesPerFrame, wave.SampleRate);
            var detectedRanges = detection.Ranges
                .Select(range => new BreathSampleRange(range.BeginSample, range.EndSample))
                .ToArray();
            MarkTraditionalWaveDetectionAttempted(
                part, signature, detectedRanges, expectedState, expectedGeneration);

            var ranges = string.Join(",", detectedRanges
                .Take(12)
                .Select(range => $"{range.BeginSample}..{range.EndSample}"));
            BreathVolumeDiagnosticsLog.Write(
                $"traditional wave {breathEffect} scoreFrames={scoreCount} waveSamples={wave.NumSamples} " +
                $"pitchedOnsets={detection.PitchedOnsets} evaluatedGaps={detection.EvaluatedGaps} " +
                $"candidates={detection.ActivityCandidates} " +
                $"rejectedShortActivity={detection.RejectedShortActivity} " +
                $"rejectedShortLead={detection.RejectedShortLead} " +
                $"rejectedPreviousTail={detection.RejectedPreviousTail} " +
                $"activeFrames={detection.ActiveFrames} " +
                $"maxUnpitchedRms={detection.MaxUnpitchedRms:G6} " +
                $"maxUnpitchedPeak={detection.MaxUnpitchedPeak:G6} " +
                $"ranges={detectedRanges.Length} [{ranges}] " +
                $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            BreathVolumeDiagnosticsLog.WriteTraditionalDetection(
                "renderedWave",
                (IntPtr)part,
                scoreCount,
                wave.NumSamples,
                samplesPerFrame,
                wave.SampleRate,
                detection,
                detectedRanges.Length,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception e)
        {
            BreathVolumeDiagnosticsLog.Write(
                $"traditional wave failed: {e.GetType().Name}: {e.Message} " +
                $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            throw;
        }
    }

    private static void MarkTraditionalWaveDetectionAttempted(
        WIVSMMidiPart part,
        ScoreSignature signature,
        IReadOnlyList<BreathSampleRange> ranges,
        PartState? expectedState,
        int expectedGeneration)
    {
        lock (Sync)
        {
            PartState state;
            if (expectedState != null)
            {
                if (!Parts.TryGetValue((IntPtr)part, out var current) ||
                    !ReferenceEquals(current, expectedState) ||
                    current.RegionRefreshGeneration != expectedGeneration)
                    return;
                state = current;
            }
            else
            {
                state = GetPartState(part);
            }

            state.TraditionalBreathRanges = ranges.ToList();
            state.TraditionalWaveSignature = signature;
            state.TraditionalWaveDetectionAttempted = true;
            state.ScoreCount = -1;
            state.ScoreSignature = default;
        }
    }

    private static bool IsAutomaticBreathEnabled(
        WIVSMMidiPart part,
        out string description)
    {
        try
        {
            var effect = part.BreathEffect;
            if (effect == null)
            {
                description = $"isAi={part.IsAi} effect=null";
                return false;
            }

            description =
                $"isAi={part.IsAi} bypassed={effect.IsBypassed} mode={effect.BreathMode} " +
                $"type={effect.BreathType} exhalation={effect.Exhalation}";
            return !effect.IsBypassed;
        }
        catch (Exception e)
        {
            description = $"effectReadFailed={e.GetType().Name}:{e.Message}";
            return false;
        }
    }

    private static bool IsAiPart(WIVSMMidiPart part)
    {
        try { return part.IsAi; }
        catch { return true; }
    }

    private static bool UseNativeBreathMixer(WIVSMMidiPart part)
        => !IsAiPart(part) && NativeBreathCapture.TryInitialize();

    private static IReadOnlyList<BreathSampleRange> MergeBreathRanges(
        IEnumerable<BreathSampleRange> ranges)
    {
        var ordered = ranges
            .Where(range => range.EndSample > range.BeginSample)
            .OrderBy(range => range.BeginSample)
            .ThenBy(range => range.EndSample)
            .ToArray();
        if (ordered.Length < 2)
            return ordered;

        var result = new List<BreathSampleRange> { ordered[0] };
        foreach (var range in ordered.Skip(1))
        {
            var previous = result[^1];
            if (range.BeginSample > previous.EndSample)
            {
                result.Add(range);
                continue;
            }
            result[^1] = new BreathSampleRange(
                previous.BeginSample, Math.Max(previous.EndSample, range.EndSample));
        }
        return result;
    }

    private static long GetNoteBeginSample(WIVSMSequence sequence, WIVSMMidiPart part, WIVSMNote note)
    {
        var seconds = sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsPosTick);
        return Math.Max(0L, (long)Math.Round(seconds * (double)sequence.GetSamplingRate()));
    }

    private static long GetNoteEndSample(WIVSMSequence sequence, WIVSMMidiPart part, WIVSMNote note)
    {
        var seconds = sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsEndTick);
        return Math.Max(0L, (long)Math.Round(seconds * (double)sequence.GetSamplingRate()));
    }

    private static bool[] BuildNotePitchedFrames(
        WIVSMSequence sequence,
        WIVSMMidiPart part,
        int frameCount,
        long samplesPerFrame)
        => TraditionalBreathDetector.BuildPitchedFrames(
            frameCount,
            samplesPerFrame,
            EnumerateNotes(part).Select(note => new TraditionalBreathRange(
                GetNoteBeginSample(sequence, part, note),
                GetNoteEndSample(sequence, part, note))));

    private static IEnumerable<WIVSMNote> EnumerateNotes(WIVSMMidiPart part)
    {
        for (ulong index = 0; index < part.NumNotes; index++)
        {
            var note = part.GetNote(index);
            if (note != null)
                yield return note;
        }
    }

    private static void CollectSequenceHandles(
        WIVSMSequence sequence,
        ISet<IntPtr> noteHandles,
        ISet<IntPtr> partHandles)
    {
        try
        {
            for (ulong trackIndex = 0; trackIndex < sequence.NumTrack; trackIndex++)
            {
                if (sequence.GetTrack(trackIndex) is not WIVSMMidiTrack track)
                    continue;
                for (ulong partIndex = 0; partIndex < track.NumParts; partIndex++)
                {
                    if (track.GetPart(partIndex) is not WIVSMMidiPart part)
                        continue;
                    partHandles.Add((IntPtr)part);
                    foreach (var note in EnumerateNotes(part))
                        noteHandles.Add(note.CppObjPtr);
                }
            }
        }
        catch
        {
            // The native sequence may already be partly closed; collected handles are still safe to release.
        }
    }

    private static List<BreathGainRegion> BuildGainRegions(WIVSMMidiPart part)
    {
        lock (Sync)
        {
            if (!Parts.TryGetValue((IntPtr)part, out var state))
                return new List<BreathGainRegion>();
            return state.Regions
                .Select(region => new BreathGainRegion(region.BeginSample, region.EndSample, GetValue(region.NoteHandle)))
                .Where(region => region.Value < DefaultValue)
                .ToList();
        }
    }

    private static void RebuildNow(WIVSMSequence sequence, WIVSMMidiPart part, int requestedGeneration = -1)
    {
        var state = GetPartState(part);
        lock (state.RebuildLock)
        {
            var sourcePath = ReadOriginalWavePath(part);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                RemoveCache(part);
                return;
            }

            var gains = BuildGainRegions(part);
            if (gains.Count == 0)
            {
                RemoveCache(part);
                RefreshAudioPlacement(sequence, part);
                return;
            }

            if (requestedGeneration >= 0 && state.RebuildGeneration != requestedGeneration)
                return;

            var sourceLength = SafeLength(sourcePath);
            var sourceWriteTime = SafeWriteTime(sourcePath);
            var identity = string.Join(";", gains.Select(region => $"{region.BeginSample}-{region.EndSample}-{region.Value}"));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{sourcePath}|{sourceLength}|{sourceWriteTime.Ticks}|{(IntPtr)part}|{identity}")))[..24];
            var destinationPath = Path.Combine(CacheDirectory, hash + ".wav");

            lock (Sync)
            {
                if (state.Cache is { } current &&
                    current.DerivedPath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(destinationPath))
                {
                    RefreshAudioPlacement(sequence, part);
                    return;
                }
                RemoveCacheCore(state);
            }

            BreathWaveProcessor.CreateAdjustedWave(sourcePath, destinationPath, gains);
            if (!Settings.IndividualBreathVolume ||
                requestedGeneration >= 0 && state.RebuildGeneration != requestedGeneration)
            {
                if (!SafeDelete(destinationPath))
                    lock (Sync)
                        CreatedCacheFiles.Add(destinationPath);
                return;
            }

            lock (Sync)
            {
                state.Sequence = sequence;
                state.Part = part;
                state.Cache = new CacheInfo(sourcePath, sourceLength, sourceWriteTime, destinationPath);
                CreatedCacheFiles.Add(destinationPath);
            }

            RefreshAudioPlacement(sequence, part);
        }
    }

    private static void RemoveCache(WIVSMMidiPart part)
    {
        lock (Sync)
        {
            if (Parts.TryGetValue((IntPtr)part, out var state))
                RemoveCacheCore(state);
        }
    }

    private static void RemoveCacheCore(PartState state)
    {
        if (state.Cache == null)
            return;
        if (SafeDelete(state.Cache.DerivedPath))
            CreatedCacheFiles.Remove(state.Cache.DerivedPath);
        state.Cache = null;
    }

    private static void RefreshAudioPlacement(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        void Refresh()
        {
            try
            {
                var player = App.AudioPlayer;
                RegisterFilePlacementMethod?.Invoke(player, new object[] { sequence, part });
                if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
                {
                    var vm = mainVm.MusicalEditorVM;
                    if (vm != null)
                        RenderedWaveCachePatch.InvalidatePart(vm, part);
                }
                ShowOtherTracksNotesPatch.RequestRefreshPianoroll();
                NotifyChanged(BreathVolumeChangeKind.Display, part);
            }
            catch (Exception e)
            {
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_PlacementFailed", e.Message));
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Refresh();
        else
            dispatcher.BeginInvoke((Action)Refresh);
    }

    private static void RebuildAllCached(WIVSMSequence sequence)
    {
        List<WIVSMMidiPart> parts;
        lock (Sync)
            parts = Parts.Values.Where(state => state.Sequence?.Equals(sequence) == true && state.Part != null)
                .Select(state => state.Part!).ToList();
        foreach (var part in parts)
            RequestRebuild(sequence, part);
    }

    private static WIVSMNote? FindPersistedNote(WIVSMSequence sequence, BreathProjectEntry entry)
    {
        if (entry.Track < 0 || (ulong)entry.Track >= sequence.NumTrack ||
            sequence.GetTrack((ulong)entry.Track) is not WIVSMMidiTrack track ||
            entry.Part < 0 || (ulong)entry.Part >= track.NumParts ||
            track.GetPart((ulong)entry.Part) is not WIVSMMidiPart part)
            return null;

        if (entry.Note >= 0 && (ulong)entry.Note < part.NumNotes)
        {
            var indexed = part.GetNote((ulong)entry.Note);
            if (indexed != null && indexed.RelPosTick.Value == entry.RelPosTick && indexed.NoteNumber == entry.NoteNumber)
                return indexed;
        }

        return EnumerateNotes(part)
            .Where(note => note.RelPosTick.Value == entry.RelPosTick && note.NoteNumber == entry.NoteNumber)
            .Skip(Math.Max(0, entry.Occurrence))
            .FirstOrDefault();
    }

    private static PartState GetPartState(WIVSMMidiPart part)
    {
        lock (Sync)
        {
            var key = (IntPtr)part;
            if (!Parts.TryGetValue(key, out var state))
            {
                state = new PartState { Part = part };
                Parts[key] = state;
            }
            return state;
        }
    }

    private static SequenceHistory GetHistory(WIVSMSequence sequence)
    {
        var key = (IntPtr)sequence;
        if (!Histories.TryGetValue(key, out var history))
        {
            history = new SequenceHistory();
            Histories[key] = history;
        }
        return history;
    }

    private static void RegisterNote(WIVSMNote note, WIVSMSequence? sequence = null)
    {
        if (note.CppObjPtr == IntPtr.Zero)
            return;
        IntPtr sequenceHandle;
        try
        {
            var ownerSequence = sequence ?? note.Sequence;
            sequenceHandle = ownerSequence == null ? IntPtr.Zero : (IntPtr)ownerSequence;
        }
        catch
        {
            sequenceHandle = IntPtr.Zero;
        }

        lock (Sync)
            RegisterNoteCore(note.CppObjPtr, sequenceHandle, clipboard: false);
    }

    private static void RegisterCopiedNote(WIVSMNote note, bool clipboard)
    {
        var handle = note.CppObjPtr;
        if (handle == IntPtr.Zero)
            return;

        IntPtr sequenceHandle = IntPtr.Zero;
        if (!clipboard)
        {
            try
            {
                var ownerSequence = note.Sequence;
                sequenceHandle = ownerSequence == null ? IntPtr.Zero : (IntPtr)ownerSequence;
            }
            catch { }
        }

        lock (Sync)
        {
            ReleaseHandlesCore(new[] { handle });
            RegisterNoteCore(handle, sequenceHandle, clipboard);
        }
    }

    private static NoteKey RegisterNoteCore(IntPtr handle, IntPtr sequenceHandle, bool clipboard)
    {
        if (ActiveNoteKeys.TryGetValue(handle, out var existing) &&
            NoteOwners.TryGetValue(existing, out var previousOwner) &&
            previousOwner != IntPtr.Zero && sequenceHandle != IntPtr.Zero && previousOwner != sequenceHandle)
        {
            ReleaseHandlesCore(new[] { handle });
        }

        var key = GetOrCreateNoteKeyCore(handle);
        if (sequenceHandle != IntPtr.Zero)
        {
            NoteOwners[key] = sequenceHandle;
            ClipboardNotes.Remove(key);
        }
        if (clipboard)
            ClipboardNotes.Add(key);
        return key;
    }

    private static NoteKey GetOrCreateNoteKeyCore(IntPtr handle)
    {
        if (ActiveNoteKeys.TryGetValue(handle, out var key))
            return key;

        var generation = ++_nextNoteGeneration;
        if (generation == 0)
            generation = ++_nextNoteGeneration;
        key = new NoteKey(handle, generation);
        ActiveNoteKeys[handle] = key;
        return key;
    }

    private static bool TryGetNoteKeyCore(IntPtr handle, out NoteKey key)
    {
        if (handle != IntPtr.Zero && ActiveNoteKeys.TryGetValue(handle, out key))
            return true;
        key = default;
        return false;
    }

    private static bool IsActiveNoteKeyCore(NoteKey key)
        => ActiveNoteKeys.TryGetValue(key.Handle, out var active) && active == key;

    private static void SetValueCore(IntPtr handle, byte value)
    {
        if (handle == IntPtr.Zero)
            return;
        lock (Sync)
        {
            var key = GetOrCreateNoteKeyCore(handle);
            if (value == DefaultValue)
                Values.Remove(key);
            else
                Values[key] = value;
        }
    }

    private static void SetValueCore(NoteKey key, byte value)
    {
        if (!IsActiveNoteKeyCore(key))
            return;
        if (value == DefaultValue)
            Values.Remove(key);
        else
            Values[key] = value;
    }

    private static void ReleaseHandlesCore(IEnumerable<IntPtr> handles)
    {
        var releasedHandles = handles.Where(handle => handle != IntPtr.Zero).Distinct().ToHashSet();
        if (releasedHandles.Count == 0)
            return;

        var releasedKeys = new HashSet<NoteKey>();
        foreach (var handle in releasedHandles)
        {
            if (!ActiveNoteKeys.Remove(handle, out var key))
                continue;
            releasedKeys.Add(key);
            Values.Remove(key);
            Selection.Remove(key);
            NoteOwners.Remove(key);
            ClipboardNotes.Remove(key);
        }

        if (releasedKeys.Count == 0)
            return;

        foreach (var state in Parts.Values)
        {
            state.Regions.RemoveAll(region => releasedHandles.Contains(region.NoteHandle));
            state.StableRegions.RemoveAll(region => releasedHandles.Contains(region.NoteHandle));
            state.RenderMarkerRegions.RemoveAll(region => releasedHandles.Contains(region.NoteHandle));
        }
        foreach (var history in Histories.Values)
        {
            PurgeHistoryKeysCore(history.Undo, releasedKeys);
            PurgeHistoryKeysCore(history.Redo, releasedKeys);
        }
    }

    private static void PurgeHistoryKeysCore(List<TimelineEntry> timeline, ISet<NoteKey> releasedKeys)
    {
        for (var index = 0; index < timeline.Count; index++)
        {
            var edit = timeline[index].Edit;
            if (edit == null)
                continue;
            var before = edit.Before.Where(pair => !releasedKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var after = edit.After.Where(pair => !releasedKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            timeline[index] = new TimelineEntry(new ValueEdit(before, after), null);
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static void CleanupCreatedFiles()
    {
        lock (Sync)
        {
            foreach (var path in CreatedCacheFiles.ToArray())
                if (SafeDelete(path))
                    CreatedCacheFiles.Remove(path);
        }
    }

    private static bool SafeDelete(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return true;
            var fullPath = Path.GetFullPath(path);
            var cacheRoot = Path.GetFullPath(CacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            return !File.Exists(fullPath);
        }
        catch
        {
            // A placement may still hold the file briefly. It will be reclaimed by the OS temp cleanup.
            return false;
        }
    }

    private readonly record struct NotePosition(WIVSMNote? Note, long BeginSample);
    private readonly record struct NoteKey(IntPtr Handle, long Generation);

    private readonly record struct ScoreSignature(
        ScoreSourceKind Kind,
        long PrimaryCount,
        long SecondaryCount,
        string? Path,
        long FileLength,
        long FileWriteTimeTicks,
        string? AudioPath = null,
        long AudioFileLength = 0,
        long AudioFileWriteTimeTicks = 0)
    {
        public static readonly ScoreSignature Unavailable = new(ScoreSourceKind.Unavailable, 0, 0, null, 0, 0);
        public static readonly ScoreSignature Faulted = new(ScoreSourceKind.Faulted, 0, 0, null, 0, 0);
    }

    private enum ScoreSourceKind
    {
        Unavailable,
        Faulted,
        RenderedFile,
        CombinedRendering,
        Holding,
        RenderBlock,
        External
    }

    private sealed record CacheInfo(string SourcePath, long SourceLength, DateTime SourceWriteTimeUtc, string DerivedPath);

    private sealed class PartState
    {
        public WIVSMSequence? Sequence;
        public WIVSMMidiPart? Part;
        public List<BreathRegion> Regions = new();
        public List<BreathRegion> StableRegions = new();
        public List<BreathRegion> RenderMarkerRegions = new();
        public List<NativeBreathMarker> NativeBreathMarkers = new();
        public List<BreathSampleRange> TraditionalBreathRanges = new();
        public ScoreSignature TraditionalWaveSignature;
        public bool TraditionalWaveDetectionAttempted;
        public readonly HashSet<ulong> NativeBreathSequences = new();
        public readonly object RebuildLock = new();
        public CacheInfo? Cache;
        public long ScoreCount = -1;
        public ScoreSignature ScoreSignature;
        public BreathRegionStatus RegionStatus;
        public bool RegionRefreshPending;
        public bool RebuildAfterRegionRefresh;
        public int RegionRefreshGeneration;
        public int Generation;
        public int LastProgress = -1;
        public int RebuildGeneration;
    }

    private sealed class SequenceHistory
    {
        public readonly List<TimelineEntry> Undo = new();
        public readonly List<TimelineEntry> Redo = new();
        public int Revision;
        public int SavedRevision;
    }

    private sealed record ValueEdit(
        IReadOnlyDictionary<NoteKey, byte> Before,
        IReadOnlyDictionary<NoteKey, byte> After);

    private readonly record struct TimelineEntry(
        ValueEdit? Edit,
        ICustomParameterHistoryEdit? External = null)
    {
        public bool HasEdit => Edit != null || External != null;
        public static TimelineEntry Native => new(null, null);
    }
}
