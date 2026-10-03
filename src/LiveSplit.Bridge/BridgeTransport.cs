using System;
using LiveSplit.Bridge.Protocol.V1;

namespace LiveSplit.Bridge;

internal enum BridgeEndpointKind { Rpc, Event, WebSocket, Other }

internal sealed class BridgeTransportStartException : Exception
{
    public BridgeTransportStartException(BridgeEndpointKind kind, string endpoint, Exception inner)
        : base($"Failed to bind {kind} endpoint {endpoint}.", inner)
    {
        EndpointKind = kind;
        Endpoint = endpoint;
    }

    public BridgeEndpointKind EndpointKind { get; }
    public string Endpoint { get; }
}

internal interface IBridgeTransport : IDisposable
{
    void Start();

    void Publish(BridgeEvent bridgeEvent);
}
