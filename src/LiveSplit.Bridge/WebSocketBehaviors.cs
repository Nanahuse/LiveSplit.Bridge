using System;
using System.Diagnostics;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V3;
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

    public WebSocketRpcBehavior(Func<Request, Response> requestHandler)
    {
        this.requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
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

        var response = requestHandler(request);

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
    public WebSocketEventBehavior() => OriginValidator = WebSocketOriginValidator.IsAllowed;
}

