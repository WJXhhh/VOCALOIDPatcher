using System.IO.Pipes;
using System.Text.Json;

namespace VOCALOIDPatcher.McpBridge;

public sealed class BridgePipeClient
{
    public async Task<BridgeResponse> InvokeAsync(
        InstanceRegistration instance,
        string method,
        object? arguments,
        BridgeClientInfo client,
        CancellationToken cancellationToken = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        JsonElement? element = arguments == null
            ? null
            : JsonSerializer.SerializeToElement(arguments, BridgeProtocol.JsonOptions);

        using var pipe = new NamedPipeClientStream(
            ".",
            instance.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

        var request = new BridgeRequest(
            BridgeProtocol.Version,
            requestId,
            method,
            element,
            client,
            instance.HandshakeToken);

        await PipeMessageFraming.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        BridgeResponse response = await PipeMessageFraming.ReadAsync<BridgeResponse>(pipe, timeout.Token).ConfigureAwait(false);
        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            throw new InvalidDataException("Mismatched bridge response ID.");
        return response;
    }
}
