using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HarmonyLib;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Utils;

/// <summary>
/// The single owner of opt-in reverse-engineering observations. Domain patches may
/// submit structured events, but must not open files, allocate cycle IDs, or log
/// raw native identities themselves.
/// </summary>
internal static class RuntimeObservationLog
{
    private const long MaximumLogBytes = 16 * 1024 * 1024;
    private const int MaximumQueuedLines = 16384;
    private const int MaximumBatchLines = 256;
    private const int MaximumScoreSamples = 4096;
    private const int MaximumPhonemePositions = 64;
    private static readonly long CommitContextLifetimeTicks = Stopwatch.Frequency * 10L;

    private static readonly object SyncRoot = new();
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly ConcurrentQueue<string> PendingLines = new();
    private static readonly ConcurrentDictionary<IntPtr, long> LastCommitBySequence = new();
    private static readonly ConcurrentDictionary<IntPtr, RenderCycle> RenderByPart = new();
    private static readonly ConcurrentDictionary<IntPtr, RenderCycle> RenderByAudioBuffer = new();
    private static readonly byte[] IdentityKey = RandomNumberGenerator.GetBytes(32);
    private static long _nextCommitCycleId;
    private static long _nextRenderCycleId;
    private static int _queuedLines;
    private static int _writerScheduled;

    [ThreadStatic]
    private static CommitContext _threadCommit;

    private static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VOCALOIDPatcher");

    public static string EnabledPath { get; } = Path.Combine(RootDirectory, "observations.enabled");

    public static string LogPath { get; } = Path.Combine(
        RootDirectory,
        "logs",
        "runtime-observations.jsonl");

    public static bool IsEnabled
    {
        get
        {
            try { return File.Exists(EnabledPath); }
            catch { return false; }
        }
    }

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public static long EnsureCommitCycle()
    {
        if (!IsEnabled)
            return 0;

        long now = Timestamp();
        if (_threadCommit.Id == 0 || now - _threadCommit.LastSeen > CommitContextLifetimeTicks)
            _threadCommit = new CommitContext(Interlocked.Increment(ref _nextCommitCycleId), now);
        else
            _threadCommit = _threadCommit with { LastSeen = now };
        return _threadCommit.Id;
    }

    public static long CurrentCommitCycle()
    {
        if (_threadCommit.Id == 0 || Timestamp() - _threadCommit.LastSeen > CommitContextLifetimeTicks)
            return 0;
        return _threadCommit.Id;
    }

    public static void CompleteCommitCycle(WIVSMSequence? sequence, bool succeeded)
    {
        long cycle = CurrentCommitCycle();
        if (succeeded && cycle != 0 && sequence != null)
            LastCommitBySequence[(IntPtr)sequence] = cycle;
    }

    public static void ClearCommitCycle() => _threadCommit = default;

    /// <summary>
    /// 把原生 Part 句柄包装为托管 WIVSMMidiPart（构造函数为 internal，
    /// 通过反射创建并 AddRef；调用方负责 Dispose 释放引用）。
    /// </summary>
    public static WIVSMMidiPart? PartFromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var constructor = AccessTools.Constructor(
                typeof(WIVSMMidiPart), new[] { typeof(IntPtr) });
            return constructor?.Invoke(new object[] { handle }) as WIVSMMidiPart;
        }
        catch
        {
            return null;
        }
    }

    public static RenderCycle BeginRenderCycle(IntPtr partHandle, WIVSMMidiPart? part)
    {
        if (!IsEnabled || partHandle == IntPtr.Zero)
            return default;

        long commitCycle = 0;
        try
        {
            WIVSMSequence? sequence = part?.Sequence;
            if (sequence != null)
                LastCommitBySequence.TryGetValue((IntPtr)sequence, out commitCycle);
        }
        catch
        {
        }

        if (RenderByPart.TryGetValue(partHandle, out RenderCycle existing))
        {
            if (existing.CommitCycleId == 0 && commitCycle != 0)
            {
                existing = existing with { CommitCycleId = commitCycle };
                RenderByPart[partHandle] = existing;
            }
            return existing;
        }

        var cycle = new RenderCycle(
            Interlocked.Increment(ref _nextRenderCycleId),
            commitCycle,
            Timestamp());
        RenderByPart[partHandle] = cycle;
        return cycle;
    }

    public static RenderCycle RenderCycleForPart(IntPtr partHandle)
        => partHandle != IntPtr.Zero && RenderByPart.TryGetValue(partHandle, out RenderCycle cycle)
            ? cycle
            : default;

    public static RenderCycle RenderCycleForAudioBuffer(IntPtr bufferHandle)
        => bufferHandle != IntPtr.Zero && RenderByAudioBuffer.TryGetValue(bufferHandle, out RenderCycle cycle)
            ? cycle
            : default;

    public static void LinkAudioBuffer(IntPtr partHandle, IntPtr bufferHandle)
    {
        RenderCycle cycle = RenderCycleForPart(partHandle);
        if (cycle.Id != 0 && bufferHandle != IntPtr.Zero)
            RenderByAudioBuffer[bufferHandle] = cycle;

        if (RenderByAudioBuffer.Count > 4096)
        {
            long cutoff = Timestamp() - Stopwatch.Frequency * 600L;
            foreach ((IntPtr buffer, RenderCycle owner) in RenderByAudioBuffer.ToArray())
            {
                if (owner.StartedTicks < cutoff)
                    RenderByAudioBuffer.TryRemove(buffer, out _);
            }
        }
    }

    public static void EndRenderCycle(IntPtr partHandle)
    {
        if (partHandle != IntPtr.Zero)
            RenderByPart.TryRemove(partHandle, out _);
    }

    public static void AddCycleData(
        IDictionary<string, object?> data,
        long commitCycleId = 0,
        RenderCycle renderCycle = default)
    {
        long commit = commitCycleId != 0
            ? commitCycleId
            : renderCycle.CommitCycleId != 0
                ? renderCycle.CommitCycleId
                : CurrentCommitCycle();
        if (commit != 0)
            data["commitCycleId"] = commit;
        if (renderCycle.Id != 0)
            data["renderCycleId"] = renderCycle.Id;
    }

    public static void Write(string eventName, string stage, IReadOnlyDictionary<string, object?>? data = null)
    {
        if (!IsEnabled)
            return;

        try
        {
            var record = new Dictionary<string, object?>
            {
                ["schema"] = "v6patch.observe/2",
                ["tsUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["monoTicks"] = Timestamp(),
                ["event"] = eventName,
                ["stage"] = stage,
                ["thread"] = ThreadSnapshot(),
            };

            long commitCycle = CurrentCommitCycle();
            if (commitCycle != 0)
                record["commitCycleId"] = commitCycle;

            if (data != null)
            {
                foreach ((string key, object? value) in data)
                    record[key] = value;
            }

            Enqueue(JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        catch
        {
            // Observation must never affect the editor or synthesis fallback.
        }
    }

    public static Dictionary<string, object?> ProgressSnapshot(VSMRendererProgress progress) => new()
    {
        ["blockEnabled"] = progress.BlockRenderingEnabled,
        ["paused"] = progress.IsPaused,
        ["firstEnd"] = progress.FirstEnd,
        ["secondBegin"] = progress.SecondBegin,
        ["secondEnd"] = progress.SecondEnd,
    };

    public static Dictionary<string, object?> PartSnapshot(WIVSMMidiPart? part)
    {
        if (part == null)
            return new Dictionary<string, object?> { ["available"] = false };

        try
        {
            return new Dictionary<string, object?>
            {
                ["available"] = true,
                ["id"] = ObjectId("part", (IntPtr)part),
                ["engine"] = part.IsAi ? "ai" : "traditional",
                ["validWave"] = Safe(() => part.HasValidRenderedWave),
                ["validScore"] = Safe(() => part.HasValidRenderedScore),
                ["rendererProgress"] = Safe(() => ProgressSnapshot(part.RendererProgress)),
                ["breathEffect"] = BreathEffectSnapshot(Safe(() => part.BreathEffect)),
                ["waveFile"] = Safe(() => FileSnapshot(part.WaveFilePath)),
                ["scoreFile"] = Safe(() => FileSnapshot(part.ScoreFilePath)),
            };
        }
        catch (Exception exception)
        {
            return new Dictionary<string, object?>
            {
                ["available"] = false,
                ["error"] = exception.GetType().Name,
            };
        }
    }

    public static Dictionary<string, object?> BreathEffectSnapshot(WIVSMBreathEffect? effect)
    {
        if (effect == null)
            return new Dictionary<string, object?> { ["available"] = false };

        try
        {
            return new Dictionary<string, object?>
            {
                ["available"] = true,
                ["id"] = ObjectId("effect", NativeHandle(effect, "BreathEffectHandle", "MidiEffectHandle", "EffectHandle")),
                ["bypassed"] = effect.IsBypassed,
                ["mode"] = effect.BreathMode.ToString(),
                ["type"] = effect.BreathType.ToString(),
                ["exhalation"] = effect.Exhalation,
                ["lastError"] = effect.LastError.ToString(),
            };
        }
        catch (Exception exception)
        {
            return new Dictionary<string, object?>
            {
                ["available"] = false,
                ["error"] = exception.GetType().Name,
            };
        }
    }

    public static Dictionary<string, object?> NoteWindowSnapshot(WIVSMNote? note)
    {
        if (note == null)
            return new Dictionary<string, object?> { ["available"] = false };

        return new Dictionary<string, object?>
        {
            ["available"] = true,
            ["prev"] = NoteSnapshot(Safe(() => note.Prev)),
            ["current"] = NoteSnapshot(note),
            ["next"] = NoteSnapshot(Safe(() => note.Next)),
        };
    }

    public static Dictionary<string, object?> BlockSnapshot(
        WIVSMAudioBufferList? buffers,
        VSMScoreList? scores,
        IntPtr partHandle = default)
    {
        var result = new Dictionary<string, object?>();

        try
        {
            int bufferCount = buffers?.NumAudioBuffers ?? 0;
            ulong sampleCount = buffers?.NumSamples ?? 0;
            var pcm = new Dictionary<string, object?>
            {
                ["buffers"] = bufferCount,
                ["samples"] = sampleCount,
            };

            if (buffers != null && sampleCount > 0 && sampleCount <= long.MaxValue)
            {
                var thumb = new VSMAudioThumb();
                if (buffers.ThumbWithRange(0, (long)sampleCount, ref thumb))
                {
                    pcm["peakRaw"] = Math.Max(Math.Abs((int)thumb.Min), Math.Abs((int)thumb.Max));
                    pcm["peak"] = Math.Max(Math.Abs((int)thumb.Min), Math.Abs((int)thumb.Max)) / 32768.0;
                }
            }

            var bufferIds = new List<string>();
            for (int index = 0; index < bufferCount; index++)
            {
                using WIVSMAudioBuffer? buffer = buffers?.AudioBuffer(index);
                if (buffer == null)
                    continue;
                bufferIds.Add(ObjectId("pcm", buffer.CppObjPtr));
                LinkAudioBuffer(partHandle, buffer.CppObjPtr);
            }
            pcm["bufferIds"] = bufferIds;
            result["pcm"] = pcm;
        }
        catch (Exception exception)
        {
            result["pcm"] = new Dictionary<string, object?> { ["error"] = exception.GetType().Name };
        }

        result["score"] = ScoreSnapshot(scores);
        return result;
    }

    public static Dictionary<string, object?> ScoreSnapshot(VSMScoreList? scores)
    {
        try
        {
            long count = scores?.NumScores ?? 0;
            if (scores == null || count <= 0)
                return new Dictionary<string, object?> { ["frames"] = Math.Max(0, count) };

            long stride = Math.Max(1, (count + MaximumScoreSamples - 1) / MaximumScoreSamples);
            long sampled = 0;
            long nonZero = 0;
            int minLeft = int.MaxValue;
            int maxLeft = int.MinValue;
            int minRight = int.MaxValue;
            int maxRight = int.MinValue;
            int transitions = 0;
            string? previous = null;
            Dictionary<string, object?>? first = null;
            Dictionary<string, object?>? last = null;

            for (long index = 0; index < count; index += stride)
            {
                VSMPhoneme phnDur = scores.ScoreAtIndex(index).PhnDur;
                var item = PhnDurSnapshot(phnDur);
                first ??= item;
                last = item;
                sampled++;
                if (!phnDur.IsZero)
                    nonZero++;
                minLeft = Math.Min(minLeft, phnDur.LeftDur);
                maxLeft = Math.Max(maxLeft, phnDur.LeftDur);
                minRight = Math.Min(minRight, phnDur.RightDur);
                maxRight = Math.Max(maxRight, phnDur.RightDur);
                string key = $"{phnDur.FwIdx}:{phnDur.BwIdx}:{phnDur.LeftDur}:{phnDur.RightDur}:" +
                             $"{ObjectId("phu", phnDur.FromPhU)}:{ObjectId("phu", phnDur.ToPhU)}";
                if (previous != null && !string.Equals(previous, key, StringComparison.Ordinal))
                    transitions++;
                previous = key;
            }

            return new Dictionary<string, object?>
            {
                ["frames"] = count,
                ["sampledFrames"] = sampled,
                ["sampleStride"] = stride,
                ["sampledNonZeroPhnDur"] = nonZero,
                ["sampledTransitions"] = transitions,
                ["leftDurRange"] = new[] { minLeft, maxLeft },
                ["rightDurRange"] = new[] { minRight, maxRight },
                ["firstPhnDur"] = first,
                ["lastPhnDur"] = last,
            };
        }
        catch (Exception exception)
        {
            return new Dictionary<string, object?> { ["error"] = exception.GetType().Name };
        }
    }

    public static int DictionaryCount(object? owner, string fieldName)
    {
        try
        {
            object? value = AccessTools.Field(owner?.GetType(), fieldName)?.GetValue(owner);
            return value switch
            {
                IDictionary dictionary => dictionary.Count,
                ICollection collection => collection.Count,
                _ => -1,
            };
        }
        catch { return -1; }
    }

    /// <summary>
    /// 渲染完成后的音符/score/波形快照。用于 VEL 实验：把每个音符的 velocity、
    /// ConsonantOffset、音素位置，以及全量 score 的音素时长和波形峰值一起记录，
    /// 以观察“VEL → 渲染辅音时长”的关系与饱和点。
    /// </summary>
    public static Dictionary<string, object?> PostRenderSnapshot(WIVSMMidiPart part)
    {
        var result = new Dictionary<string, object?>();

        var notes = new List<Dictionary<string, object?>>();
        try
        {
            foreach (WIVSMNote note in part.Notes)
            {
                notes.Add(new Dictionary<string, object?>
                {
                    ["relPos"] = note.RelPosTick.Value,
                    ["duration"] = note.DurationTick.Value,
                    ["lyricHash"] = HashText(note.Lyric),
                    ["phonemeHash"] = HashText(note.Phonemes),
                    ["velocity"] = note.NoteVelocity,
                    ["consonantOffset"] = note.ConsonantOffset,
                    ["positions"] = note.GetPhonemePositions().Take(MaximumPhonemePositions).ToArray(),
                });
            }
        }
        catch (Exception exception)
        {
            notes.Add(new Dictionary<string, object?> { ["error"] = exception.GetType().Name });
        }
        result["notes"] = notes;

        // 全量 score：优先取 Part 保留的渲染结果 score list（覆盖整个 Part，
        // 而不是 block 事件的第一个分块）。属性名随版本差异（6.13.0.1 为
        // RetainedHolding/RetainedRendering，6.13.1.1 为 Holding/Rendering）。
        VSMScoreList? retained = Safe(() => part.HoldingScoreList)
                                ?? Safe(() => part.RenderingScoreList);
        result["score"] = retained == null
            ? new Dictionary<string, object?> { ["available"] = false }
            : ScoreSnapshot(retained);

        result["wave"] = WavePeakSnapshot(Safe(() => part.WaveFilePath));

        return result;
    }

    private static Dictionary<string, object?> WavePeakSnapshot(string? path)
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["pathId"] = HashText(path),
            ["readable"] = false,
        };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return snapshot;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.SequentialScan);
            long length = stream.Length;
            snapshot["readable"] = true;
            snapshot["length"] = length;
            if (length < 44)
                return snapshot;

            // 跳过 RIFF 头；按 16-bit PCM 扫描峰值与非零样本数。
            const int bytesPerSample = 2;
            long dataBytes = length - 44;
            long maxScan = Math.Min(dataBytes, 16L * 1024 * 1024);
            long samples = maxScan / bytesPerSample;
            if (samples <= 0)
                return snapshot;

            var buffer = new byte[1 << 16];
            int peak = 0;
            long nonZero = 0;
            long remaining = maxScan;
            long offset = 44;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                stream.Position = offset;
                int read = stream.Read(buffer, 0, toRead);
                if (read <= 0)
                    break;
                for (int i = 0; i + 1 < read; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    int abs = Math.Abs((int)sample);
                    if (abs > peak)
                        peak = abs;
                    if (sample != 0)
                        nonZero++;
                }
                offset += read;
                remaining -= read;
            }

            snapshot["peakRaw"] = peak;
            snapshot["peak"] = peak / 32768.0;
            snapshot["scannedSamples"] = samples;
            snapshot["nonZeroSamples"] = nonZero;
            snapshot["nonZeroRatio"] = samples == 0 ? 0.0 : nonZero / (double)samples;
        }
        catch (Exception exception)
        {
            snapshot["openError"] = exception.GetType().Name;
        }
        return snapshot;
    }

    // Compatibility name for existing probes. Values are per-process keyed IDs, never raw pointers.
    public static string Handle(IntPtr handle) => ObjectId("native", handle);

    public static string ObjectId(string kind, IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return "none";
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(bytes, handle.ToInt64());
        byte[] hash = HMACSHA256.HashData(IdentityKey, bytes);
        return $"{kind}_{Convert.ToHexString(hash.AsSpan(0, 6))}";
    }

    public static string HashText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "empty";
        byte[] hash = HMACSHA256.HashData(IdentityKey, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public static string ExperimentInputClass(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "empty";
        string normalized = string.Join(' ', value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
        return normalized switch
        {
            "f un" => "pinyin.f_un",
            "x un" => "pinyin.x_un",
            "f a" => "pinyin.f_a",
            "m" => "pinyin.m",
            "n" => "pinyin.n",
            _ => "pinyin.other",
        };
    }

    private static Dictionary<string, object?> ThreadSnapshot()
    {
        bool? uiAccess = null;
        try { uiAccess = Application.Current?.Dispatcher?.CheckAccess(); }
        catch { }

        return new Dictionary<string, object?>
        {
            ["managedId"] = Environment.CurrentManagedThreadId,
            ["isThreadPool"] = Thread.CurrentThread.IsThreadPoolThread,
            ["uiAccess"] = uiAccess,
        };
    }

    private static Dictionary<string, object?> NoteSnapshot(WIVSMNote? note)
    {
        if (note == null)
            return new Dictionary<string, object?> { ["available"] = false };

        try
        {
            string phonemes = note.Phonemes ?? string.Empty;
            List<int> positions = note.GetPhonemePositions();
            return new Dictionary<string, object?>
            {
                ["available"] = true,
                ["id"] = ObjectId("note", (IntPtr)note),
                ["engine"] = note.IsAi ? "ai" : "traditional",
                ["langId"] = note.LangID,
                ["protected"] = note.IsProtected,
                ["validPhonemes"] = note.IsValidPhonemes,
                ["velocity"] = note.NoteVelocity,
                ["consonantOffset"] = note.ConsonantOffset,
                ["phonemeLength"] = phonemes.Length,
                ["phonemeHash"] = HashText(phonemes),
                ["phonemePositionCount"] = positions.Count,
                ["phonemePositions"] = positions.Take(MaximumPhonemePositions).ToArray(),
                ["phonemePositionsTruncated"] = positions.Count > MaximumPhonemePositions,
            };
        }
        catch (Exception exception)
        {
            return new Dictionary<string, object?>
            {
                ["available"] = false,
                ["error"] = exception.GetType().Name,
            };
        }
    }

    private static Dictionary<string, object?> PhnDurSnapshot(VSMPhoneme value) => new()
    {
        ["fwIdx"] = value.FwIdx,
        ["bwIdx"] = value.BwIdx,
        ["leftDur"] = value.LeftDur,
        ["rightDur"] = value.RightDur,
        ["fromId"] = ObjectId("phu", value.FromPhU),
        ["toId"] = ObjectId("phu", value.ToPhU),
        ["zero"] = value.IsZero,
    };

    private static Dictionary<string, object?> FileSnapshot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new Dictionary<string, object?> { ["pathId"] = "empty", ["exists"] = false };

        var snapshot = new Dictionary<string, object?>
        {
            ["pathId"] = HashText(path),
            ["exists"] = false,
            ["readableShared"] = false,
        };

        try
        {
            var info = new FileInfo(path);
            snapshot["exists"] = info.Exists;
            if (!info.Exists)
                return snapshot;
            snapshot["length"] = info.Length;
            snapshot["lastWriteUtc"] = info.LastWriteTimeUtc.ToString("O");
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.SequentialScan);
            snapshot["readableShared"] = true;
        }
        catch (Exception exception)
        {
            snapshot["openError"] = exception.GetType().Name;
        }
        return snapshot;
    }

    private static IntPtr NativeHandle(object owner, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            try
            {
                object? value = AccessTools.Property(owner.GetType(), name)?.GetValue(owner);
                if (value is IntPtr handle && handle != IntPtr.Zero)
                    return handle;
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private static T? Safe<T>(Func<T> action)
    {
        try { return action(); }
        catch { return default; }
    }

    private static void Enqueue(string line)
    {
        if (Interlocked.Increment(ref _queuedLines) > MaximumQueuedLines)
        {
            Interlocked.Decrement(ref _queuedLines);
            return;
        }

        PendingLines.Enqueue(line);
        if (Interlocked.CompareExchange(ref _writerScheduled, 1, 0) == 0)
            _ = Task.Run(DrainPendingLines);
    }

    private static void DrainPendingLines()
    {
        try
        {
            while (true)
            {
                var batch = new StringBuilder();
                int count = 0;
                while (count < MaximumBatchLines && PendingLines.TryDequeue(out string? line))
                {
                    Interlocked.Decrement(ref _queuedLines);
                    batch.Append(line);
                    count++;
                }
                if (batch.Length > 0)
                    AppendBatch(batch.ToString());
                if (!PendingLines.IsEmpty)
                    continue;
                Interlocked.Exchange(ref _writerScheduled, 0);
                if (PendingLines.IsEmpty || Interlocked.CompareExchange(ref _writerScheduled, 1, 0) != 0)
                    return;
            }
        }
        catch
        {
            Interlocked.Exchange(ref _writerScheduled, 0);
        }
    }

    private static void AppendBatch(string text)
    {
        try
        {
            lock (SyncRoot)
            {
                string? directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogBytes)
                    File.Move(LogPath, LogPath + ".previous", true);
                File.AppendAllText(LogPath, text, Utf8WithoutBom);
            }
        }
        catch
        {
        }
    }

    private readonly record struct CommitContext(long Id, long LastSeen);
}

internal readonly record struct RenderCycle(long Id, long CommitCycleId, long StartedTicks);
