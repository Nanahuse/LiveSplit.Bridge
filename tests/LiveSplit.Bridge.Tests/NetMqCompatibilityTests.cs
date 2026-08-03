using System.Reflection;

namespace LiveSplit.Bridge.Tests;

public class NetMqCompatibilityTests
{
    [Fact]
    public void NetMqSystemMemoryReferenceIsCoveredByLiveSplitRedirect()
    {
        var netMqPath = Path.Combine(AppContext.BaseDirectory, "NetMQ.dll");
        var netMq = Assembly.ReflectionOnlyLoadFrom(netMqPath);

        var systemMemory = Assert.Single(
            netMq.GetReferencedAssemblies(),
            reference => reference.Name == "System.Memory");

        Assert.True(systemMemory.Version <= new Version(4, 0, 1, 2));
    }
}
