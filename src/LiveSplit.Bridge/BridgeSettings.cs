using System;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.Bridge;

internal sealed class BridgeSettings
{
    public const int CurrentVersion = 1;
    public const int DefaultWebSocketPort = 54000;

    public int WebSocketPort { get; set; } = DefaultWebSocketPort;

    public void WriteTo(XmlElement settings)
    {
        AppendValue(settings, "Version", CurrentVersion);
        AppendValue(settings, "WebSocketPort", WebSocketPort);
    }

    public void ReadFrom(XmlNode settings)
    {
        WebSocketPort = DefaultWebSocketPort;

        var versionText = settings.SelectSingleNode("Version")?.InnerText;
        if (!int.TryParse(versionText, out var version) || version != CurrentVersion)
        {
            return;
        }

        WebSocketPort = ReadPort(settings, "WebSocketPort", DefaultWebSocketPort);
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
    private readonly NumericUpDown webSocketPort = CreatePortInput();
    private readonly Label status = new() { AutoSize = true };
    private readonly Label validation = new() { AutoSize = true, ForeColor = System.Drawing.Color.DarkRed };

    public event EventHandler PortChanged;

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

        layout.Controls.Add(status, 0, 0); layout.SetColumnSpan(status, 2);
        layout.Controls.Add(validation, 0, 1); layout.SetColumnSpan(validation, 2);
        layout.Controls.Add(new Label { Text = "WebSocket port:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(webSocketPort, 1, 2);
        var applyButton = new Button { Text = "Apply", AutoSize = true };
        applyButton.Click += (_, _) => PortChanged?.Invoke(this, EventArgs.Empty);
        layout.Controls.Add(applyButton, 1, 3);
        Controls.Add(layout);

        SetValues(settings);
    }

    public int WebSocketPort => Decimal.ToInt32(webSocketPort.Value);

    public void SetRuntimeStatus(string text, string? error = null) { status.Text = $"Status: {text}"; validation.Text = error ?? string.Empty; }

    public void SetValues(BridgeSettings settings)
    {
        webSocketPort.Value = settings.WebSocketPort;
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
