using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace VoiceAgent.Realtime;

/// <summary>A text-message view over a WebSocket, used for both the Twilio leg and the model leg.</summary>
public interface IMessageChannel : IAsyncDisposable
{
    bool IsOpen { get; }
    Task SendAsync(string json, CancellationToken ct);
    IAsyncEnumerable<string> ReadAsync(CancellationToken ct);
    Task CloseAsync(string reason, CancellationToken ct);
}

/// <summary>
/// Reassembles fragmented frames into whole messages and serializes writes.
/// System.Net.WebSockets allows one concurrent send; the call loop is the only writer in
/// practice, but the lock makes that a guarantee instead of a convention.
/// </summary>
public sealed class WebSocketMessageChannel(WebSocket socket) : IMessageChannel
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendAsync(string json, CancellationToken ct)
    {
        if (!IsOpen) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (IsOpen)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // Peer went away mid-send; the read loop reports the close.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new ArrayBufferWriter<byte>();
        while (!ct.IsCancellationRequested && IsOpen)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close) yield break;
            message.Write(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text)
                yield return Encoding.UTF8.GetString(message.WrittenSpan);
            message.Clear();
        }
    }

    public async Task CloseAsync(string reason, CancellationToken ct)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason.Length > 120 ? reason[..120] : reason, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing a socket the peer already closed is not an error.
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
