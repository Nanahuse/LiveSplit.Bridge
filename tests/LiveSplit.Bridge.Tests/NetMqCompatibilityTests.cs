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

    [Fact]
    public void BridgeRuntimeDoesNotHoldNetMqTypes()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var runtimeTypes = typeof(BridgeRuntime).GetFields(flags).Select(field => field.FieldType)
            .Concat(typeof(BridgeRuntime).GetProperties(flags).Select(property => property.PropertyType));

        Assert.DoesNotContain(runtimeTypes, IsNetMqType);
    }

    private static bool IsNetMqType(Type type)
    {
        return type.Namespace is string @namespace
            && @namespace.StartsWith("NetMQ", StringComparison.Ordinal);
    }
}
