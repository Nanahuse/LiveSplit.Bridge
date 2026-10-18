#nullable enable
using System;
using LiveSplit.Model;

namespace LiveSplit.Bridge;

/// <summary>Owns the JSON runtime and its v4 WebSocket transport as one component lifetime.</summary>
internal sealed class JsonBridgeHost : IDisposable
{
    private readonly JsonWebSocketTransport transport;
    private readonly JsonBridgeRuntime runtime;
    private bool disposed;

    internal JsonBridgeHost(LiveSplitState state, int port)
    {
        transport = new JsonWebSocketTransport(port);
        JsonBridgeRuntime? createdRuntime = null;
        try
        {
            createdRuntime = new JsonBridgeRuntime(state ?? throw new ArgumentNullException(nameof(state)), transport);
            runtime = createdRuntime;
            transport.Start(createdRuntime);
        }
        catch
        {
            createdRuntime?.Dispose();
            transport.Dispose();
            throw;
        }
    }

    internal string Endpoint => transport.Endpoint;
    internal bool IsListening => transport.IsListening;

    internal void Update()
    {
        if (!disposed) runtime.Update();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        runtime.Dispose();
        transport.Dispose();
    }
}
