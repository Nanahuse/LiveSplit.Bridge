using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Google.Protobuf;
using LiveSplit.Bridge.Protocol.V1;
using NetMQ;
using NetMQ.Sockets;

namespace LiveSplit.Bridge;

internal enum BridgeEndpointKind { Rpc, Event, Other }

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

internal sealed class ZeroMqTransport : IBridgeTransport
{
    private readonly string rpcEndpoint;
    private readonly string eventEndpoint;
    private readonly TimeSpan heartbeatInterval;
    private readonly Func<Request, Response> requestHandler;
    private readonly Func<BridgeEvent> heartbeatFactory;
    private readonly Action<ulong> eventSettled;
    private readonly CancellationTokenSource cancellation = new();
    private readonly BlockingCollection<BridgeEvent> publishQueue = new();
    private readonly ManualResetEventSlim publisherReady = new(false);
    private readonly Thread publisherThread;
    private readonly Thread requestThread;
    private ResponseSocket responder;
    private Exception publisherStartException;
    private int disposed;

    public ZeroMqTransport(
        string rpcEndpoint,
        string eventEndpoint,
        TimeSpan heartbeatInterval,
        Func<Request, Response> requestHandler,
        Func<BridgeEvent> heartbeatFactory,
        Action<ulong> eventSettled)
    {
        this.rpcEndpoint = rpcEndpoint ?? throw new ArgumentNullException(nameof(rpcEndpoint));
        this.eventEndpoint = eventEndpoint ?? throw new ArgumentNullException(nameof(eventEndpoint));
        this.heartbeatInterval = heartbeatInterval;
        this.requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        this.heartbeatFactory = heartbeatFactory ?? throw new ArgumentNullException(nameof(heartbeatFactory));
        this.eventSettled = eventSettled ?? throw new ArgumentNullException(nameof(eventSettled));

        publisherThread = new Thread(PublisherLoop)
        {
            IsBackground = true,
            Name = "LiveSplit.Bridge.PublisherLoop"
        };
        requestThread = new Thread(RequestLoop)
        {
            IsBackground = true,
            Name = "LiveSplit.Bridge.RequestLoop"
        };
    }

    public void Start()
    {
        publisherThread.Start();
        if (!publisherReady.Wait(TimeSpan.FromSeconds(5)))
        {
            StopPublisherAfterStartFailure();
            throw new BridgeTransportStartException(BridgeEndpointKind.Event, eventEndpoint, new TimeoutException("Timed out while binding the event endpoint."));
        }

        if (publisherStartException != null)
        {
            StopPublisherAfterStartFailure();
            throw new BridgeTransportStartException(BridgeEndpointKind.Event, eventEndpoint, publisherStartException);
        }

        try
        {
            responder = new ResponseSocket();
            responder.Bind(rpcEndpoint);
            Debug.WriteLine($"[LiveSplit.Bridge] RPC endpoint bound to {rpcEndpoint}");
        }
        catch (Exception exception)
        {
            responder?.Close();
            responder?.Dispose();
            responder = null;
            StopPublisherAfterStartFailure();
            throw new BridgeTransportStartException(BridgeEndpointKind.Rpc, rpcEndpoint, exception);
        }

        requestThread.Start();
    }

    public void Publish(BridgeEvent bridgeEvent)
    {
        if (bridgeEvent == null)
        {
            throw new ArgumentNullException(nameof(bridgeEvent));
        }

        if (cancellation.IsCancellationRequested || publishQueue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            publishQueue.Add(bridgeEvent, cancellation.Token);
        }
        catch (InvalidOperationException)
        {
            // The queue was completed during shutdown.
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        cancellation.Cancel();
        publishQueue.CompleteAdding();

        JoinThread(requestThread);
        JoinThread(publisherThread);

        responder?.Close();
        responder?.Dispose();
        publisherReady.Dispose();
        publishQueue.Dispose();
        cancellation.Dispose();
        NetMQConfig.Cleanup(true);
    }

    private void StopPublisherAfterStartFailure()
    {
        cancellation.Cancel();
        publishQueue.CompleteAdding();
        JoinThread(publisherThread);
    }

    private static void JoinThread(Thread thread)
    {
        try
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        catch (ThreadStateException)
        {
            // The thread was never started because startup failed.
        }
    }

    private void RequestLoop()
    {
        if (responder == null)
        {
            return;
        }

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (!responder.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(100), out var requestData))
                {
                    continue;
                }

                var request = Request.Parser.ParseFrom(requestData);
                var response = requestHandler(request);
                responder.SendFrame(response.ToByteArray());
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[LiveSplit.Bridge] Request loop error: {exception.Message}");
            }
        }
    }

    private void PublisherLoop()
    {
        PublisherSocket publisher = null;

        try
        {
            publisher = new PublisherSocket();
            publisher.Bind(eventEndpoint);
            Debug.WriteLine($"[LiveSplit.Bridge] Event endpoint bound to {eventEndpoint}");
            publisherReady.Set();

            var clock = Stopwatch.StartNew();
            var nextHeartbeat = heartbeatInterval;

            while (!cancellation.IsCancellationRequested)
            {
                var remaining = nextHeartbeat - clock.Elapsed;
                var waitMilliseconds = remaining <= TimeSpan.Zero
                    ? 0
                    : (int)Math.Min(Math.Ceiling(remaining.TotalMilliseconds), int.MaxValue);

                if (publishQueue.TryTake(
                    out var bridgeEvent,
                    waitMilliseconds,
                    cancellation.Token))
                {
                    PublishSequencedEvent(publisher, bridgeEvent);
                }

                if (clock.Elapsed >= nextHeartbeat)
                {
                    PublishHeartbeat(publisher);
                    do
                    {
                        nextHeartbeat += heartbeatInterval;
                    }
                    while (nextHeartbeat <= clock.Elapsed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            if (!publisherReady.IsSet)
            {
                publisherStartException = exception;
            }
            else
            {
                Debug.WriteLine($"[LiveSplit.Bridge] Publisher loop error: {exception.Message}");
            }
        }
        finally
        {
            publisherReady.Set();
            publisher?.Close();
            publisher?.Dispose();
        }
    }

    private void PublishSequencedEvent(PublisherSocket publisher, BridgeEvent bridgeEvent)
    {
        var sequence = bridgeEvent.EventSequence;

        try
        {
            publisher.SendFrame(bridgeEvent.ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Event publish failed: {exception.Message}");
        }
        finally
        {
            eventSettled(sequence);
        }
    }

    private void PublishHeartbeat(PublisherSocket publisher)
    {
        var heartbeat = heartbeatFactory();

        try
        {
            publisher.SendFrame(heartbeat.ToByteArray());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[LiveSplit.Bridge] Heartbeat publish failed: {exception.Message}");
        }
    }
}
