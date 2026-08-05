using System;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.Bridge;

internal sealed class BridgeSettings
{
    public const int DefaultRpcPort = 54000;
    public const int DefaultEventPort = 54001;

    public int RpcPort { get; set; } = DefaultRpcPort;
    public int EventPort { get; set; } = DefaultEventPort;

    public void WriteTo(XmlElement settings)
    {
        AppendValue(settings, "RpcPort", RpcPort);
        AppendValue(settings, "EventPort", EventPort);
    }

    public void ReadFrom(XmlNode settings)
    {
        RpcPort = ReadPort(settings, "RpcPort", DefaultRpcPort);
        EventPort = ReadPort(settings, "EventPort", DefaultEventPort);
        if (EventPort == RpcPort)
        {
            EventPort = RpcPort == DefaultEventPort ? DefaultRpcPort : DefaultEventPort;
        }
    }

    private static int ReadPort(XmlNode settings, string name, int defaultValue)
    {
        var text = settings.SelectSingleNode(name)?.InnerText;
        return int.TryParse(text, out var port) && port >= 1 && port <= 65535
            ? port
            : defaultValue;
    }

    private static void AppendValue(XmlElement settings, string name, int value)
    {
        var element = settings.OwnerDocument!.CreateElement(name);
        element.InnerText = value.ToString();
        settings.AppendChild(element);
    }
}

internal sealed class BridgeSettingsControl : UserControl
{
    private readonly NumericUpDown rpcPort = CreatePortInput();
    private readonly NumericUpDown eventPort = CreatePortInput();

    public event EventHandler PortsChanged;

    public BridgeSettingsControl(BridgeSettings settings)
    {
        AutoSize = true;
        Dock = DockStyle.Fill;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(7)
        };

        layout.Controls.Add(new Label { Text = "RPC port:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(rpcPort, 1, 0);
        layout.Controls.Add(new Label { Text = "Event port:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(eventPort, 1, 1);
        var applyButton = new Button { Text = "Apply", AutoSize = true };
        applyButton.Click += (_, _) => PortsChanged?.Invoke(this, EventArgs.Empty);
        layout.Controls.Add(applyButton, 1, 2);
        Controls.Add(layout);

        SetValues(settings);
    }

    public int RpcPort => Decimal.ToInt32(rpcPort.Value);
    public int EventPort => Decimal.ToInt32(eventPort.Value);

    public void SetValues(BridgeSettings settings)
    {
        rpcPort.Value = settings.RpcPort;
        eventPort.Value = settings.EventPort;
    }

    private static NumericUpDown CreatePortInput()
    {
        return new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Width = 90,
            ThousandsSeparator = false
        };
    }
}
