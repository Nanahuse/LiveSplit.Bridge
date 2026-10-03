using System.Net.WebSockets;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;

namespace LiveSplit.Bridge.Tests;

internal sealed class WebSocketTestClient : IDisposable
{
    private readonly ClientWebSocket socket;

    private WebSocketTestClient(ClientWebSocket socket)
    {
        this.socket = socket;
    }

    public static async Task<WebSocketTestClient> ConnectAsync(string url, string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (origin != null)
        {
            socket.Options.SetRequestHeader("Origin", origin);
        }

        await socket.ConnectAsync(new Uri(url), CancellationToken.None);
        return new WebSocketTestClient(socket);
    }

    public Task SendBinaryAsync(byte[] data)
    {
        return socket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None);
    }

    public Task SendTextAsync(string data)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    public async Task<byte[]> ReceiveBinaryAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The WebSocket was closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return stream.ToArray();
            }
        }
    }

    public async Task<Response> SendRequestAsync(Request request, TimeSpan timeout)
    {
        await SendBinaryAsync(request.ToByteArray());
        var data = await ReceiveBinaryAsync(timeout);
        return Response.Parser.ParseFrom(data);
    }

    public void Dispose()
    {
        socket.Dispose();
    }
}
