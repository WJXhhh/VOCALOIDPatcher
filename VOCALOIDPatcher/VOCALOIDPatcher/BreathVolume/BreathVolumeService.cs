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
using VOCALOIDPatcher.Translation;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.BreathVolume;

internal readonly record struct BreathRegion(
    long BeginSample,
    long EndSample,
    long BeginTick,
    long EndTick,
    IntPtr NoteHandle);

internal sealed record BreathNativeObjectHandles(IntPtr[] NoteHandles, IntPtr[] PartHandles);

internal enum BreathVolumeChangeKind
{
    Display,
    Values,
    Regions,
    Selection
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

    static BreathVolumeService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupCreatedFiles();
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

        Changed?.Invoke(BreathVolumeChangeKind.Selection, null);
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

        Changed?.Invoke(BreathVolumeChangeKind.Selection, null);
    }

    public static void ClearSelection()
    {
        lock (Sync)
            Selection.Clear();
        Changed?.Invoke(BreathVolumeChangeKind.Selection, null);
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

        Changed?.Invoke(BreathVolumeChangeKind.Display, null);
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

        Changed?.Invoke(BreathVolumeChangeKind.Values, part);
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
    }

    public static void CopyClipboardPartValues(WIVSMMidiPart? source, WIVSMMidiPart? target)
    {
        if (source == null || target == null)
            return;

        var count = Math.Min(source.NumNotes, target.NumNotes);
        for (ulong index = 0; index < count; index++)
            CopyClipboardNoteValue(source.GetNote(index), target.GetNote(index));
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
    {
        if (!Settings.IndividualBreathVolume || sequence == null || part == null)
            return false;

        VSMScoreList? list = null;
        try
        {
            list = part.RenderingScoreList;
            if (list != null && !list.IsEmpty)
                return RefreshRegions(sequence, part, list);
            list?.Dispose();
            list = part.HoldingScoreList;
            if (list != null && !list.IsEmpty)
                return RefreshRegions(sequence, part, list);
            list?.Dispose();
            list = null;

            var path = part.ScoreFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return StoreRegions(part, Array.Empty<BreathRegion>(), 0);

            using var file = new VSMScoreFile(path);
            return RefreshRegions(sequence, part, file);
        }
        catch (Exception e)
        {
            Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_ScoreFailed", e.Message));
            return StoreRegions(part, Array.Empty<BreathRegion>(), 0);
        }
        finally
        {
            list?.Dispose();
        }
    }

    public static bool RefreshRegions(WIVSMSequence sequence, WIVSMMidiPart part, IVSMScoreEnumerator score)
    {
        if (!Settings.IndividualBreathVolume || score == null || score.NumScores <= 0 || sequence.NumSampleInFrame <= 0)
            return StoreRegions(part, Array.Empty<BreathRegion>(), score?.NumScores ?? 0);

        lock (Sync)
        {
            if (Parts.TryGetValue((IntPtr)part, out var cached) && cached.ScoreCount == score.NumScores && cached.Generation != 0)
                return cached.Regions.Count > 0;
        }

        var ranges = new List<(long Begin, long End)>();
        long breathBegin = -1;
        for (long frame = 0; frame < score.NumScores; frame++)
        {
            var phoneme = score.ScoreAtIndex(frame).PhnDur;
            var isBreath = IsBreathPhoneme(phoneme.FromPhU) || IsBreathPhoneme(phoneme.ToPhU);
            if (isBreath && breathBegin < 0)
                breathBegin = frame;
            else if (!isBreath && breathBegin >= 0)
            {
                ranges.Add((breathBegin * sequence.NumSampleInFrame, frame * sequence.NumSampleInFrame));
                breathBegin = -1;
            }
        }

        if (breathBegin >= 0)
            ranges.Add((breathBegin * sequence.NumSampleInFrame, score.NumScores * sequence.NumSampleInFrame));

        var notes = EnumerateNotes(part)
            .Select(note =>
            {
                RegisterNote(note, sequence);
                return new NotePosition(note, GetNoteBeginSample(sequence, part, note));
            })
            .OrderBy(item => item.BeginSample)
            .ToArray();
        var sampleRate = (double)sequence.GetSamplingRate();
        var waveBeginTime = sequence.GetTimeFromTick(part.AbsBeginTick) - sequence.PresendTimeSec;
        var regions = new List<BreathRegion>();

        foreach (var range in ranges)
        {
            var next = notes.FirstOrDefault(note => note.BeginSample >= range.End);
            if (next.Note == null)
                next = notes.FirstOrDefault(note => note.BeginSample >= range.Begin);
            if (next.Note == null)
                continue;

            var beginTime = waveBeginTime + range.Begin / sampleRate;
            var endTime = waveBeginTime + range.End / sampleRate;
            var beginTick = Math.Max(0, sequence.GetTickFromTime(beginTime).Value);
            var endTick = Math.Max(beginTick, sequence.GetTickFromTime(endTime).Value);
            regions.Add(new BreathRegion(
                range.Begin,
                range.End,
                beginTick,
                endTick,
                next.Note.CppObjPtr));
        }

        return StoreRegions(part, regions, score.NumScores);
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
            state.ProcessedBuffers.Clear();
            state.LastProgress = progress;
            RemoveCacheCore(state);
        }
    }

    public static void StartRender(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        lock (Sync)
        {
            var state = GetPartState(part);
            state.Sequence = sequence;
            state.Part = part;
            state.Generation++;
            state.ScoreCount = -1;
            state.ProcessedBuffers.Clear();
            state.LastProgress = 0;
            Interlocked.Increment(ref state.RebuildGeneration);
            RemoveCacheCore(state);
        }
        Changed?.Invoke(BreathVolumeChangeKind.Regions, part);
    }

    public static void CancelRender(WIVSMMidiPart part)
    {
        lock (Sync)
        {
            var state = GetPartState(part);
            state.ScoreCount = -1;
            state.ProcessedBuffers.Clear();
            state.LastProgress = -1;
        }
        Changed?.Invoke(BreathVolumeChangeKind.Regions, part);
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

        BeginRenderedBlock(part, progress);
        RefreshRegions(sequence, part, score);
        var gains = BuildGainRegions(part);
        if (gains.Count == 0)
            return;

        long bufferBegin = 0;
        for (var index = 0; index < buffers.NumAudioBuffers; index++)
        {
            using var buffer = buffers.AudioBuffer(index);
            if (buffer == null || buffer.Samples == IntPtr.Zero || buffer.NumSamples == 0)
                continue;

            long alreadyProcessed;
            lock (Sync)
            {
                var state = GetPartState(part);
                state.ProcessedBuffers.TryGetValue(buffer.CppObjPtr, out alreadyProcessed);
                if (alreadyProcessed > checked((long)buffer.NumSamples))
                    alreadyProcessed = 0;
                state.ProcessedBuffers[buffer.CppObjPtr] = checked((long)buffer.NumSamples);
            }

            BreathWaveProcessor.ApplyFloatBuffer(
                buffer.Samples,
                checked((long)buffer.NumSamples),
                bufferBegin,
                bufferBegin + alreadyProcessed,
                gains);
            bufferBegin += checked((long)buffer.NumSamples);
        }
    }

    public static void CompleteRender(WIVSMSequence sequence, WIVSMMidiPart part)
    {
        if (!Settings.IndividualBreathVolume)
            return;

        try
        {
            lock (Sync)
            {
                var state = GetPartState(part);
                state.LastProgress = -1;
                state.ScoreCount = -1;
                RemoveCacheCore(state);
            }

            RefreshRegions(sequence, part);
            RebuildNow(sequence, part);
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
        _ = Task.Run(() =>
        {
            try
            {
                RebuildNow(sequence, part, generation);
            }
            catch (Exception e)
            {
                Debug.Print(TranslationManager.Tr("VOCALOIDPatcher_Debug_BreathVolume_CacheFailed", e.Message));
                RemoveCache(part);
                RefreshAudioPlacement(sequence, part);
            }
        });
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
        Changed?.Invoke(BreathVolumeChangeKind.Display, null);
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
        if (data == null || data.Version != 1 || data.Entries == null)
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

        lock (Sync)
        {
            var history = GetHistory(sequence);
            history.Revision = 0;
            history.SavedRevision = 0;
        }
        Changed?.Invoke(BreathVolumeChangeKind.Values, null);
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
            RefreshRegions(sequence, part);
            RequestRebuild(sequence, part);
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
        => data.Entries is { Count: > 0 } || IsProjectDirty(sequence);

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

    public static bool CanUndo(WIVSMSequence sequence, bool nativeResult)
    {
        lock (Sync)
            return nativeResult || GetHistory(sequence).Undo.LastOrDefault().Edit != null;
    }

    public static bool CanRedo(WIVSMSequence sequence, bool nativeResult)
    {
        lock (Sync)
            return nativeResult || GetHistory(sequence).Redo.LastOrDefault().Edit != null;
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
        ValueEdit? edit;
        lock (Sync)
        {
            var history = GetHistory(sequence);
            var source = undo ? history.Undo : history.Redo;
            var destination = undo ? history.Redo : history.Undo;
            if (source.Count == 0)
                return false;

            var entry = source[^1];
            source.RemoveAt(source.Count - 1);
            destination.Add(entry);
            if (entry.Edit == null)
                return false;

            edit = entry.Edit;
            history.Revision += undo ? -1 : 1;
            foreach (var pair in undo ? edit.Before : edit.After)
                if (IsActiveNoteKeyCore(pair.Key))
                    SetValueCore(pair.Key, pair.Value);
        }

        Changed?.Invoke(BreathVolumeChangeKind.Values, null);
        RebuildAllCached(sequence);
        return true;
    }

    private static void MarkSaved(WIVSMSequence sequence)
    {
        lock (Sync)
        {
            var history = GetHistory(sequence);
            history.SavedRevision = history.Revision;
        }
    }

    private static bool StoreRegions(WIVSMMidiPart part, IReadOnlyList<BreathRegion> regions, long scoreCount)
    {
        lock (Sync)
        {
            var state = GetPartState(part);
            state.ScoreCount = scoreCount;
            state.Regions = regions.ToList();
            if (state.Generation == 0)
                state.Generation = 1;
        }

        Changed?.Invoke(BreathVolumeChangeKind.Regions, part);
        return regions.Count > 0;
    }

    private static bool IsBreathPhoneme(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return false;
        var text = Marshal.PtrToStringAnsi(pointer);
        return text != null && (text.Equals("br", StringComparison.OrdinalIgnoreCase) ||
                                text.Equals("SilBreath", StringComparison.OrdinalIgnoreCase));
    }

    private static long GetNoteBeginSample(WIVSMSequence sequence, WIVSMMidiPart part, WIVSMNote note)
    {
        var seconds = sequence.PresendTimeSec + sequence.GetTimeFromTick(part.AbsBeginTick, note.AbsPosTick);
        return Math.Max(0L, (long)Math.Round(seconds * (double)sequence.GetSamplingRate()));
    }

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
                Changed?.Invoke(BreathVolumeChangeKind.Display, part);
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
            state.Regions.RemoveAll(region => releasedHandles.Contains(region.NoteHandle));
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
            timeline[index] = new TimelineEntry(new ValueEdit(before, after));
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
    private sealed record CacheInfo(string SourcePath, long SourceLength, DateTime SourceWriteTimeUtc, string DerivedPath);

    private sealed class PartState
    {
        public WIVSMSequence? Sequence;
        public WIVSMMidiPart? Part;
        public List<BreathRegion> Regions = new();
        public readonly Dictionary<IntPtr, long> ProcessedBuffers = new();
        public readonly object RebuildLock = new();
        public CacheInfo? Cache;
        public long ScoreCount = -1;
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

    private readonly record struct TimelineEntry(ValueEdit? Edit)
    {
        public static TimelineEntry Native => new(null);
    }
}
