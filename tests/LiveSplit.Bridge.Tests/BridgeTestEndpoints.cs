using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LiveSplit.Bridge.Tests;

internal static class BridgeTestEndpoints
{
    public static (int Rpc, int Event) GetFreePorts()
    {
        var rpc = GetFreePort();
        int @event;
        do
        {
            @event = GetFreePort();
        }
        while (@event == rpc);

        return (rpc, @event);
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static string Rpc(int port) => $"tcp://127.0.0.1:{port}";

    public static string Event(int port) => $"tcp://127.0.0.1:{port}";

    public static bool IsLoopbackListening(int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address));
    }

    public static void WaitForListener(int port, bool expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (IsLoopbackListening(port) == expected)
            {
                return;
            }

            Thread.Sleep(50);
        }

        Assert.Equal(expected, IsLoopbackListening(port));
    }
}
