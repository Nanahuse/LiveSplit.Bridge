using System;

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
