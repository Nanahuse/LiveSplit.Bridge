using System.Xml;

namespace LiveSplit.Bridge.Tests;

public class BridgeSettingsTests
{
    [Fact]
    public void ReadFrom_LoadsVersion1Port()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><Version>1</Version><WebSocketPort>55000</WebSocketPort></Settings>");
        var settings = new BridgeSettings();

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(55000, settings.WebSocketPort);
    }

    [Fact]
    public void ReadFrom_MissingVersionUsesDefault()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><WebSocketPort>55000</WebSocketPort></Settings>");
        var settings = new BridgeSettings();

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(BridgeSettings.DefaultWebSocketPort, settings.WebSocketPort);
    }

    [Fact]
    public void ReadFrom_UnparseableVersionUsesDefault()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><Version>not-a-version</Version><WebSocketPort>55000</WebSocketPort></Settings>");
        var settings = new BridgeSettings();

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(BridgeSettings.DefaultWebSocketPort, settings.WebSocketPort);
    }

    [Fact]
    public void ReadFrom_DifferentVersionUsesDefault()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><Version>2</Version><WebSocketPort>55000</WebSocketPort></Settings>");
        var settings = new BridgeSettings();

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(BridgeSettings.DefaultWebSocketPort, settings.WebSocketPort);
    }

    [Fact]
    public void ReadFrom_InvalidVersion1PortUsesDefault()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><Version>1</Version><WebSocketPort>0</WebSocketPort></Settings>");
        var settings = new BridgeSettings { WebSocketPort = 60000 };

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(BridgeSettings.DefaultWebSocketPort, settings.WebSocketPort);
    }

    [Fact]
    public void ReadFrom_LegacySettingsWithoutVersionAreIgnored()
    {
        var document = new XmlDocument();
        document.LoadXml("<Settings><RpcPort>55000</RpcPort><EventPort>55001</EventPort></Settings>");
        var settings = new BridgeSettings { WebSocketPort = 60000 };

        settings.ReadFrom(document.DocumentElement!);

        Assert.Equal(BridgeSettings.DefaultWebSocketPort, settings.WebSocketPort);
    }

    [Fact]
    public void WriteTo_SavesVersionAndPort()
    {
        var document = new XmlDocument();
        var root = document.CreateElement("Settings");
        document.AppendChild(root);
        var settings = new BridgeSettings { WebSocketPort = 55000 };

        settings.WriteTo(root);

        Assert.Equal("1", root.SelectSingleNode("Version")?.InnerText);
        Assert.Equal("55000", root.SelectSingleNode("WebSocketPort")?.InnerText);
    }
}
