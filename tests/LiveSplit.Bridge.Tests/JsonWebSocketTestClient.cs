using System.Net.WebSockets;
using System.Text;
using System.Xml;
using LiveSplit.Model;

namespace LiveSplit.Bridge.Tests;

internal sealed class JsonWebSocketTestClient : IDisposable
{
    private readonly ClientWebSocket socket;

    private JsonWebSocketTestClient(ClientWebSocket socket) => this.socket = socket;

    internal WebSocketState State => socket.State;
    internal WebSocketCloseStatus? CloseStatus => socket.CloseStatus;

    internal static async Task<JsonWebSocketTestClient> ConnectAsync(string url, string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (origin != null) socket.Options.SetRequestHeader("Origin", origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
        return new JsonWebSocketTestClient(socket);
    }

    internal static Component StartComponent(LiveSplitState state, int port)
    {
        var component = new Component(state);
        var document = new XmlDocument();
        document.LoadXml($"<Settings><Version>1</Version><WebSocketPort>{port}</WebSocketPort></Settings>");
        component.SetSettings(document.DocumentElement!);
        component.Update(null!, state, 0, 0, LiveSplit.UI.LayoutMode.Vertical);
        BridgeTestEndpoints.WaitForListener(port, expected: true, timeout: TimeSpan.FromSeconds(5));
        return component;
    }

    internal Task SendTextAsync(string json) => SendBytesAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    internal Task SendBinaryAsync(byte[] bytes) => SendBytesAsync(bytes, WebSocketMessageType.Binary);

    internal async Task SendFragmentedTextAsync(string json, int fragmentLength)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var offset = 0; offset < bytes.Length; offset += fragmentLength)
        {
            var length = Math.Min(fragmentLength, bytes.Length - offset);
            await socket.SendAsync(new ArraySegment<byte>(bytes, offset, length), WebSocketMessageType.Text,
                endOfMessage: offset + length == bytes.Length, CancellationToken.None);
        }
    }

    internal async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await socket.CloseOutputAsync(socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            socket.CloseStatusDescription ?? string.Empty, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        // The server may dispose immediately after receiving the close reply.
                    }
                }
                return (WebSocketMessageType.Close, Array.Empty<byte>());
            }
            data.Write(buffer, 0, received.Count);
            if (received.EndOfMessage) return (received.MessageType, data.ToArray());
        }
    }

    private Task SendBytesAsync(byte[] bytes, WebSocketMessageType type) =>
        socket.SendAsync(new ArraySegment<byte>(bytes), type, true, CancellationToken.None);

    public void Dispose()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None)
                    .GetAwaiter().GetResult();
        }
        catch (Exception) { }
        socket.Dispose();
    }
}
