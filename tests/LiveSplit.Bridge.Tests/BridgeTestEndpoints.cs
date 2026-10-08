using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LiveSplit.Bridge.Tests;

internal static class BridgeTestEndpoints
{
    public static int[] GetFreePorts(int count)
    {
        var ports = new HashSet<int>();
        while (ports.Count < count)
        {
            ports.Add(GetFreePort());
        }

        return ports.ToArray();
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

    public static string Rpc(int port) => $"ws://127.0.0.1:{port}/bridge/v3/rpc";

    public static string Events(int port) => $"ws://127.0.0.1:{port}/bridge/v2/events";

    public static string WebSocket(int port) => $"ws://127.0.0.1:{port}";

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

