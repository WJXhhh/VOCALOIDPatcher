using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VOCALOIDPatcher.McpBridge;

public static class BridgeProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 8 * 1024 * 1024;
    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 1000;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };
}

public sealed record BridgeClientInfo(
    string Id,
    string Name,
    string? Version,
    string Transport);

public sealed record BridgeRequest(
    int ProtocolVersion,
    string RequestId,
    string Method,
    JsonElement? Arguments,
    BridgeClientInfo Client,
    string HandshakeToken);

public sealed record BridgeError(
    string Code,
    string Message,
    bool Retryable = false,
    JsonElement? Details = null);

public sealed record BridgeResponse(
    int ProtocolVersion,
    string RequestId,
    bool Ok,
    JsonElement? Result = null,
    BridgeError? Error = null)
{
    public static BridgeResponse Success(string requestId, object? result)
        => new(BridgeProtocol.Version, requestId, true, JsonSerializer.SerializeToElement(result, BridgeProtocol.JsonOptions));

    public static BridgeResponse Failure(string requestId, string code, string message, bool retryable = false, object? details = null)
        => new(
            BridgeProtocol.Version,
            requestId,
            false,
            Error: new BridgeError(
                code,
                message,
                retryable,
                details == null ? null : JsonSerializer.SerializeToElement(details, BridgeProtocol.JsonOptions)));
}

public sealed record InstanceRegistration(
    int ProtocolVersion,
    string InstanceId,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string PipeName,
    string HandshakeToken,
    string EditorVersion,
    string? WindowTitle,
    string? ProjectName,
    DateTimeOffset RegisteredAtUtc);

public sealed record ProjectContext(string InstanceId, string ProjectId, long Revision);

public sealed record EntityRef(
    string ProjectId,
    long Revision,
    string Kind,
    int TrackIndex = -1,
    int PartIndex = -1,
    int ItemIndex = -1,
    string? EntityId = null,
    string? ClientTag = null);

public sealed record CapabilityStatus(
    string Id,
    bool Implemented,
    bool HostVerified,
    string? MinimumEditorVersion = null,
    string? UnavailableReason = null,
    string Availability = "available");

public sealed record OperationResult(
    int OperationIndex,
    string OperationId,
    string Status,
    EntityRef? Reference = null,
    string? ClientTag = null,
    string? TempId = null,
    object? Summary = null);

public sealed record OperationFailure(
    int OperationIndex,
    string OperationId,
    string? Field,
    string Code,
    string Message,
    bool RolledBack,
    bool Retryable);

public sealed record MusicalPosition(
    long AbsoluteTick,
    int? Bar = null,
    int? Beat = null,
    int? Tick = null,
    double? Seconds = null,
    long? PartRelativeTick = null);

public sealed record CapabilityManifest(
    bool ReadProject,
    bool EditStructure,
    bool EditNotes,
    bool G2pa,
    bool EditParameters,
    bool Selection,
    bool Transport,
    bool History,
    bool ProjectFiles,
    bool Conversion,
    bool Mixdown,
    IReadOnlyList<string>? UnsupportedReasons = null);

public enum BridgeJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    CompletedAfterCancel,
}

public sealed record JobInfo(
    string JobId,
    string Kind,
    BridgeJobStatus Status,
    double Progress,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    JsonElement? Result = null,
    BridgeError? Error = null,
    bool CancellationRequested = false);

public static class PipeMessageFraming
{
    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.JsonOptions);
        if (payload.Length > BridgeProtocol.MaxMessageBytes)
            throw new InvalidDataException($"Message exceeds {BridgeProtocol.MaxMessageBytes} bytes.");

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > BridgeProtocol.MaxMessageBytes)
            throw new InvalidDataException("Invalid bridge message length.");

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, BridgeProtocol.JsonOptions)
               ?? throw new InvalidDataException($"Could not deserialize {typeof(T).Name}.");
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }
}
