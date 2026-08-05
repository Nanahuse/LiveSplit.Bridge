using System.Xml;

namespace LiveSplit.Bridge.Tests;

public class BridgeSettingsTests
{
    [Fact]
    public void ReadFrom_LoadsSavedPorts()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><RpcPort>55000</RpcPort><EventPort>55001</EventPort></Settings>");
        var settings = new BridgeSettings();

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(55000, settings.RpcPort);
        Assert.Equal(55001, settings.EventPort);
    }

    [Fact]
    public void ReadFrom_UsesDefaultsForMissingOrInvalidPorts()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><RpcPort>0</RpcPort><EventPort>not-a-port</EventPort></Settings>");
        var settings = new BridgeSettings { RpcPort = 60000, EventPort = 60001 };

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(BridgeSettings.DefaultRpcPort, settings.RpcPort);
        Assert.Equal(BridgeSettings.DefaultEventPort, settings.EventPort);
    }

    [Fact]
    public void WriteTo_SavesPorts()
    {
        var document = new XmlDocument();
        var root = document.CreateElement("Settings");
        document.AppendChild(root);
        var settings = new BridgeSettings { RpcPort = 55000, EventPort = 55001 };

        settings.WriteTo(root);

        Assert.Equal("55000", root.SelectSingleNode("RpcPort")?.InnerText);
        Assert.Equal("55001", root.SelectSingleNode("EventPort")?.InnerText);
    }

    [Fact]
    public void ReadFrom_UsesDifferentPortWhenSavedPortsMatch()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><RpcPort>55000</RpcPort><EventPort>55000</EventPort></Settings>");
        var settings = new BridgeSettings();

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(55000, settings.RpcPort);
        Assert.Equal(BridgeSettings.DefaultEventPort, settings.EventPort);
    }
}
