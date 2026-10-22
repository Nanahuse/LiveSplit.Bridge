using System;

namespace LiveSplit.Bridge;

internal sealed class BridgeTransportStartException : Exception
{
    public BridgeTransportStartException(string endpoint, Exception inner)
        : base($"Failed to bind WebSocket endpoint {endpoint}.", inner)
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }
}
