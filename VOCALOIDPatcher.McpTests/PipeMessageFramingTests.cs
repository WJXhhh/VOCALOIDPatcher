using System.Buffers.Binary;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.McpTests;

public sealed class PipeMessageFramingTests
{
    [Fact]
    public async Task RoundTripsBridgeResponse()
    {
        await using var stream = new MemoryStream();
        BridgeResponse expected = BridgeResponse.Success("request-1", new { revision = 42, title = "demo" });

        await PipeMessageFraming.WriteAsync(stream, expected);
        stream.Position = 0;
        BridgeResponse actual = await PipeMessageFraming.ReadAsync<BridgeResponse>(stream);

        Assert.True(actual.Ok);
        Assert.Equal("request-1", actual.RequestId);
        Assert.Equal(42, actual.Result!.Value.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task RejectsOversizedFrameBeforeAllocation()
    {
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, BridgeProtocol.MaxMessageBytes + 1);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await PipeMessageFraming.ReadAsync<BridgeRequest>(stream));
    }

    [Fact]
    public async Task RejectsTruncatedFrame()
    {
        byte[] frame = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 20);
        await using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await PipeMessageFraming.ReadAsync<BridgeRequest>(stream));
    }
}
