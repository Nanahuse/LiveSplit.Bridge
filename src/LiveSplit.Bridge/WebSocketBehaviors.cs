using System;
using System.Diagnostics;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V2;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace LiveSplit.Bridge;

internal static class WebSocketOriginValidator
{
    public static bool IsAllowed(string origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (string.Equals(origin, "null", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
        {
            host = host.Substring(1, host.Length - 2);
        }

        return host == "localhost"
            || host == "127.0.0.1"
            || host == "::1";
    }
}

internal sealed class WebSocketRpcBehavior : WebSocketBehavior
{
    private readonly Func<Request, Response> requestHandler;
    private readonly object rpcLock;

    public WebSocketRpcBehavior(Func<Request, Response> requestHandler, object rpcLock)
    {
        this.requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        this.rpcLock = rpcLock ?? throw new ArgumentNullException(nameof(rpcLock));
        OriginValidator = WebSocketOriginValidator.IsAllowed;
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        if (!e.IsBinary)
        {
            return;
        }

        Request request;
        try
        {
            request = Request.Parser.ParseFrom(e.RawData);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Failed to parse RPC request: {exception.Message}");
            return;
        }

        Response response;
        lock (rpcLock)
        {
            response = requestHandler(request);
        }

        try
        {
            Send(response.ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Failed to send RPC response: {exception.Message}");
        }
    }
}

internal sealed class WebSocketEventBehavior : WebSocketBehavior
{
    public WebSocketEventBehavior()
    {
        OriginValidator = WebSocketOriginValidator.IsAllowed;
    }
}
