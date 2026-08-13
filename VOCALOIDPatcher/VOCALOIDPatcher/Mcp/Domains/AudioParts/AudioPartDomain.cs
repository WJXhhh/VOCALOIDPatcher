using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VSM;
using Yamaha.VOCALOID.WOR;

namespace VOCALOIDPatcher.Mcp.Domains.AudioParts;

internal sealed class AudioPartDomainException : Exception
{
    public string Code { get; }

    public AudioPartDomainException(string code, string message) : base(message)
    {
        Code = code;
    }
}

internal static class AudioPartDomain
{
    private static OperationContract[] Operations => AudioPartContracts.Operations;

    internal static void Register()
        => McpDomainRegistry.Register(new McpDomainAdapter(
            new DomainContract("audio_parts", new[] { "audio_parts" }, Operations.Select(item => item.Id).ToArray(), new[] { "part" }, "operation.audio_parts"),
            Operations,
            Capabilities,
            (_, sequence, projectId, revision, _) => Query(sequence, projectId, revision),
            ApplyRegistered,
            verb => (verb == "create", verb == "delete")));

    private static void ApplyRegistered(WIVSMSequence sequence, JsonElement operation, bool execute)
    {
        try
        {
            Apply(sequence, operation, execute);
        }
        catch (AudioPartDomainException exception) when (exception.Code == McpErrorCodes.Unsupported)
        {
            throw new NotSupportedException(exception.Message, exception);
        }
        catch (AudioPartDomainException exception) when (exception.Code is McpErrorCodes.InvalidRequest or McpErrorCodes.InvalidReference)
        {
            throw new ArgumentException(exception.Message, exception);
        }
        catch (AudioPartDomainException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    private static IReadOnlyList<CapabilityStatus> Capabilities() => new[]
    {
        Status("query.audio_parts", true, "Awaiting repeatable V6 host validation."),
        Status("operation.audio_parts.core", true, "Awaiting repeatable V6 host validation."),
        Status("operation.audio_parts.normalize", true, "Uses V6 OfflineProcessor synchronously inside the caller's native Transaction; Audio Parts with AI Voice Changer are rejected."),
        Status("operation.audio_parts.fade_gain", false, "V6 exposes only a fixed playback anti-click fade; per-Part gain is an effect-chain Gain, not an Audio Part property."),
        Status("operation.audio_parts.time_stretch", true, "Uses V6 OfflineProcessor synchronously inside the caller's native Transaction; Audio Parts with AI Voice Changer are rejected."),
    };

    private static CapabilityStatus Status(string id, bool implemented, string reason)
        => new(id, implemented, false, "6.13.0", reason, implemented ? "host_validation_required" : "unsupported");

    internal static object[] Query(WIVSMSequence sequence, string projectId, long revision)
    {
        var result = new List<object>();
        for (int trackIndex = 0; trackIndex < sequence.Tracks.Count; trackIndex++)
        {
            WIVSMTrack track = sequence.Tracks[trackIndex];
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
            {
                if (track.Parts[partIndex] is not WIVSMAudioPart part)
                    continue;
                string sourcePath = part.GetOriginalWaveFilePath();
                VSMAudioPartRegion region = part.Region;
                string resolved = string.Empty;
                bool allowed = !string.IsNullOrWhiteSpace(sourcePath)
                               && McpAccessController.TryResolvePath(sourcePath, out resolved, out _);
                WaveMetadata? metadata = null;
                string? diagnostic = null;
                if (string.IsNullOrWhiteSpace(sourcePath))
                    diagnostic = "missing_media_reference";
                else if (!allowed)
                    diagnostic = "media_outside_allowlist";
                else if (!File.Exists(resolved))
                    diagnostic = "missing_media";
                else if (!TryReadWaveMetadata(resolved, out metadata, out diagnostic))
                    diagnostic ??= "unsupported_media";

                result.Add(new
                {
                    reference = McpEntityRegistry.Reference(projectId, revision, "part", part, trackIndex, partIndex),
                    name = part.Name,
                    position_tick = part.AbsPosTick.Value,
                    duration_tick = region.DurationTick.Value,
                    region = new { tick_begin = region.TickBegin, tick_end = region.TickEnd },
                    source = new
                    {
                        id = SafeSourceId(sourcePath),
                        name = string.IsNullOrWhiteSpace(sourcePath) ? null : part.GetOriginalWaveFileName(),
                        exists = allowed && File.Exists(resolved),
                        sample_rate = metadata?.SampleRate,
                        channels = metadata?.Channels,
                        duration_seconds = metadata?.DurationSeconds,
                        diagnostic,
                    },
                    offline_processing = new
                    {
                        normalize_supported = string.IsNullOrEmpty(part.AiVoiceBankID),
                        time_stretch_supported = string.IsNullOrEmpty(part.AiVoiceBankID),
                        unavailable_reason = string.IsNullOrEmpty(part.AiVoiceBankID)
                            ? null
                            : "ai_voice_changer_requires_async_rebuild",
                    },
                    gain = (double?)null,
                });
            }
        }
        return result.ToArray();
    }

    internal static void Apply(WIVSMSequence sequence, JsonElement operation, bool execute)
    {
        string op = ReadString(operation, "op") ?? throw Invalid("op is required.");
        if (!op.StartsWith("audio_", StringComparison.Ordinal))
            op = "audio_" + op;
        switch (op)
        {
            case "audio_create":
                Create(sequence, operation, execute);
                return;
            case "audio_replace_source":
            {
                WIVSMAudioPart part = ResolvePart(sequence, operation);
                string source = ValidateSource(operation, out _);
                if (execute && !part.SetOriginalWaveFile(source, Path.GetFileName(source)))
                    throw Failed("VOCALOID rejected the replacement audio source.");
                return;
            }
            case "audio_move":
            {
                int sourceTrackIndex = ReadInt(operation, "track_index", -1);
                WIVSMAudioPart part = ResolvePart(sequence, operation);
                int targetTrackIndex = ReadInt(operation, "to_track_index", sourceTrackIndex);
                WIVSMAudioTrack source = ResolveAudioTrack(sequence, sourceTrackIndex);
                WIVSMAudioTrack target = ResolveAudioTrack(sequence, targetTrackIndex);
                long position = ReadLong(operation, "absolute_tick") ?? throw Invalid("absolute_tick is required.");
                if (position < 0)
                    throw Invalid("absolute_tick cannot be negative.");
                if (execute && !source.MovePart(new VSMAbsTick(position), target, part))
                    throw Failed("VOCALOID could not move the audio part.");
                return;
            }
            case "audio_trim_region":
            {
                WIVSMAudioPart part = ResolvePart(sequence, operation);
                int begin = CheckedTick(operation, "region_tick_begin");
                int end = CheckedTick(operation, "region_tick_end");
                ValidateRegion(begin, end);
                if (execute && !part.SetRegion(new VSMAudioPartRegion(begin, end)))
                    throw Failed("VOCALOID rejected the audio region.");
                return;
            }
            case "audio_set_length":
            {
                WIVSMAudioPart part = ResolvePart(sequence, operation);
                long duration = ReadLong(operation, "duration_tick") ?? throw Invalid("duration_tick is required.");
                if (duration <= 0 || duration > int.MaxValue)
                    throw Invalid("duration_tick must be between 1 and 2147483647.");
                VSMAudioPartRegion region = part.Region;
                int end = checked(region.TickBegin + (int)duration);
                if (execute && !part.SetRegion(new VSMAudioPartRegion(region.TickBegin, end)))
                    throw Failed("VOCALOID rejected the audio part length.");
                return;
            }
            case "audio_delete":
            {
                int trackIndex = ReadInt(operation, "track_index", -1);
                WIVSMAudioTrack track = ResolveAudioTrack(sequence, trackIndex);
                WIVSMAudioPart part = ResolvePart(sequence, operation);
                if (execute && !track.RemovePart(part))
                    throw Failed("VOCALOID could not delete the audio part.");
                return;
            }
            case "audio_normalize":
            {
                ValidateOfflineOperation(operation);
                WIVSMAudioPart part = ResolvePart(sequence, operation);
                ValidateRenderableMedia(part);
                RejectAiVoiceChanger(part);
                if (execute)
                {
                    OfflineProcessor.Result result = OfflineProcessor.ApplyNormalize(part);
                    if (!result.IsSuccess || !OfflineProcessor.ApplyPitchShift(part).IsSuccess)
                        throw Failed("VOCALOID could not normalize and rebuild the Audio Part media.");
                }
                return;
            }
            case "audio_time_stretch":
            {
                ValidateOfflineOperation(operation);
                WIVSMAudioPart part = ResolvePart(sequence, operation);
                ValidateRenderableMedia(part);
                RejectAiVoiceChanger(part);
                long duration = ReadLong(operation, "duration_tick")!.Value;
                double currentSeconds = part.DurationSec;
                if (currentSeconds <= 0)
                    throw Invalid("The Audio Part has no positive playable duration.");
                double targetSeconds = sequence.GetTimeFromTick(part.AbsBeginTick, part.AbsBeginTick + duration);
                double magnification = targetSeconds / currentSeconds;
                double minimum = TimeStretchRenderer.MinBpmMag;
                double maximum = TimeStretchRenderer.MaxBpmMag;
                if (!double.IsFinite(magnification) || magnification < minimum || magnification > maximum)
                    throw Invalid($"duration_tick produces a time-stretch magnification outside the native range {minimum:R}..{maximum:R}.");
                if (execute)
                {
                    OfflineProcessor.Result result = OfflineProcessor.ApplyTimeStretch(sequence, part, magnification);
                    if (!result.IsSuccess || !OfflineProcessor.ApplyPitchShift(part).IsSuccess)
                        throw Failed("VOCALOID could not time-stretch and rebuild the Audio Part media.");
                }
                return;
            }
            case "audio_fade":
            case "audio_gain":
                throw new AudioPartDomainException(McpErrorCodes.Unsupported,
                    "V6 6.13 has no editable Audio Part fade/gain property. Fade is a fixed playback anti-click behavior and gain belongs to the Part effect chain.");
            default:
                throw new AudioPartDomainException(McpErrorCodes.Unsupported, $"Unsupported Audio Part operation '{op}'.");
        }
    }

    private static void Create(WIVSMSequence sequence, JsonElement operation, bool execute)
    {
        int trackIndex = ReadInt(operation, "track_index", -1);
        WIVSMAudioTrack track = ResolveAudioTrack(sequence, trackIndex);
        string source = ValidateSource(operation, out WaveMetadata metadata);
        long position = ReadLong(operation, "absolute_tick") ?? 0;
        if (position < 0)
            throw Invalid("absolute_tick cannot be negative.");
        int begin = ReadLong(operation, "region_tick_begin") is { } beginValue ? checked((int)beginValue) : 0;
        int end;
        if (ReadLong(operation, "region_tick_end") is { } endValue)
            end = checked((int)endValue);
        else if (ReadLong(operation, "duration_tick") is { } duration)
            end = checked(begin + checked((int)duration));
        else
            end = checked((int)(sequence.GetTickFromTime(new VSMAbsTick(position), metadata.DurationSeconds).Value - position));
        ValidateRegion(begin, end);
        if (!execute)
            return;
        WIVSMAudioPart part = track.InsertPart(new VSMAbsTick(position), ReadString(operation, "name") ?? Path.GetFileNameWithoutExtension(source))
                               ?? throw Failed("VOCALOID could not create the audio part.");
        if (!part.SetRegion(new VSMAudioPartRegion(begin, end))
            || !part.SetOriginalWaveFile(source, Path.GetFileName(source)))
            throw Failed("VOCALOID could not attach the audio source.");
    }

    private static string ValidateSource(JsonElement operation, out WaveMetadata metadata)
    {
        string supplied = ReadString(operation, "source_path") ?? ReadString(operation, "audio_path")
            ?? throw Invalid("source_path is required.");
        if (!McpAccessController.TryResolvePath(supplied, out string fullPath, out BridgeError? error))
            throw new AudioPartDomainException(error?.Code ?? "path_not_allowed", error?.Message ?? "The path is not allowed.");
        if (!File.Exists(fullPath))
            throw new AudioPartDomainException(McpErrorCodes.InvalidReference, "The audio source does not exist.");
        if (!TryReadWaveMetadata(fullPath, out WaveMetadata? parsed, out string? diagnostic) || parsed == null)
            throw new AudioPartDomainException(McpErrorCodes.Unsupported, diagnostic ?? "The audio source is not a supported PCM/float WAVE file.");
        metadata = parsed;
        return fullPath;
    }

    private static void ValidateOfflineOperation(JsonElement operation)
    {
        IReadOnlyList<string> errors = AudioPartContracts.ValidateOfflineOperation(operation);
        if (errors.Count > 0)
            throw Invalid(string.Join(" ", errors));
    }

    private static void ValidateRenderableMedia(WIVSMAudioPart part)
    {
        string source = part.GetOriginalWaveFilePath();
        if (string.IsNullOrWhiteSpace(source))
            throw new AudioPartDomainException(McpErrorCodes.InvalidReference, "The Audio Part has no original media reference.");
        if (!McpAccessController.TryResolvePath(source, out string resolved, out BridgeError? error))
            throw new AudioPartDomainException(error?.Code ?? "path_not_allowed", error?.Message ?? "The Audio Part media path is not allowed.");
        if (!File.Exists(resolved))
            throw new AudioPartDomainException(McpErrorCodes.InvalidReference, "The Audio Part media is missing.");
        if (!TryReadWaveMetadata(resolved, out _, out string? diagnostic))
            throw new AudioPartDomainException(McpErrorCodes.Unsupported, diagnostic ?? "The Audio Part media is not a supported PCM/float WAVE file.");
    }

    private static void RejectAiVoiceChanger(WIVSMAudioPart part)
    {
        if (!string.IsNullOrEmpty(part.AiVoiceBankID))
            throw new AudioPartDomainException(McpErrorCodes.Unsupported,
                "This Audio Part uses AI Voice Changer. V6 rebuilds that derived media asynchronously, so it cannot participate in this synchronous transaction.");
    }

    private static bool TryReadWaveMetadata(string path, out WaveMetadata? metadata, out string? diagnostic)
    {
        metadata = null;
        diagnostic = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            {
                diagnostic = "unsupported_media_format";
                return false;
            }
            ushort format = 0, channels = 0;
            uint sampleRate = 0, byteRate = 0;
            long dataLength = -1;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            while (stream.Position + 8 <= stream.Length)
            {
                if (stream.Read(chunk) != 8)
                    break;
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                long next = checked(stream.Position + size + (size & 1));
                if (next > stream.Length)
                {
                    diagnostic = "truncated_media";
                    return false;
                }
                if (chunk[..4].SequenceEqual("fmt "u8) && size >= 16)
                {
                    if (stream.Read(fmt) != 16)
                        return false;
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                    byteRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]);
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                    dataLength = size;
                stream.Position = next;
            }
            if (format is not (1 or 3) || channels is 0 or > 32 || sampleRate is < 8000 or > 384000 || byteRate == 0 || dataLength < 0)
            {
                diagnostic = "unsupported_media_format";
                return false;
            }
            metadata = new WaveMetadata((int)sampleRate, channels, dataLength / (double)byteRate);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            diagnostic = exception is UnauthorizedAccessException ? "media_not_accessible" : "media_read_failed";
            return false;
        }
    }

    private static WIVSMAudioPart ResolvePart(WIVSMSequence sequence, JsonElement operation)
    {
        int trackIndex = ReadInt(operation, "track_index", -1);
        WIVSMAudioTrack track = ResolveAudioTrack(sequence, trackIndex);
        int partIndex = ReadInt(operation, "part_index", -1);
        if (partIndex < 0 || partIndex >= track.Parts.Count || track.Parts[partIndex] is not WIVSMAudioPart part)
            throw new AudioPartDomainException(McpErrorCodes.InvalidReference, "part_index does not identify an Audio Part.");
        return part;
    }

    private static WIVSMAudioTrack ResolveAudioTrack(WIVSMSequence sequence, int index)
    {
        if (index < 0 || index >= sequence.Tracks.Count || sequence.Tracks[index] is not WIVSMAudioTrack track)
            throw new AudioPartDomainException(McpErrorCodes.InvalidReference, "track_index does not identify an audio track.");
        return track;
    }

    private static int CheckedTick(JsonElement operation, string name)
    {
        long value = ReadLong(operation, name) ?? throw Invalid($"{name} is required.");
        if (value < 0 || value > int.MaxValue)
            throw Invalid($"{name} must be between 0 and 2147483647.");
        return (int)value;
    }

    private static void ValidateRegion(int begin, int end)
    {
        if (begin < 0 || end <= begin)
            throw Invalid("The audio Region must satisfy 0 <= region_tick_begin < region_tick_end.");
    }

    private static string SafeSourceId(string path)
        => string.IsNullOrWhiteSpace(path) ? "missing" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..24];

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : null;

    private static int ReadInt(JsonElement element, string name, int fallback)
        => ReadLong(element, name) is { } value ? checked((int)value) : fallback;

    private static AudioPartDomainException Invalid(string message) => new(McpErrorCodes.InvalidRequest, message);
    private static AudioPartDomainException Failed(string message) => new(McpErrorCodes.OperationFailed, message);

    private sealed record WaveMetadata(int SampleRate, int Channels, double DurationSeconds);
}
