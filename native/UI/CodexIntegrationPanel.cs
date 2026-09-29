namespace VectorAnimationEngine;

internal sealed class CodexIntegrationPanel : UserControl
{
    private readonly ModernToggleSwitch _enabled = new() { Text = "Enable MCP integration" };
    private readonly ModernToggleSwitch _allowChanges = new() { Text = "Allow editor changes" };
    private readonly ModernNumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = 43521 };
    private readonly TextBox _token = new() { UseSystemPasswordChar = true, MaxLength = 256 };
    private readonly TextBox _endpoint = new() { ReadOnly = true };
    private readonly TextBox _configuration = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Horizontal, WordWrap = false };
    private readonly Label _status = new();

    public CodexIntegrationPanel()
    {
        AccessibleName = "Codex / MCP";
        BackColor = Theme.Panel;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 11,
            BackColor = Theme.Panel, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 40, 32, 32, 42, 42, 44, 46, 36, 28, 84 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        layout.Controls.Add(TextLabel("Connect Codex or another MCP client to this running editor."), 0, 0);
        layout.Controls.Add(_enabled, 0, 1);
        layout.Controls.Add(_allowChanges, 0, 2);
        layout.Controls.Add(Field("Local port", _port), 0, 3);
        layout.Controls.Add(Field("Bearer token (optional)", _token), 0, 4);
        layout.Controls.Add(TextLabel("Without write access, clients can only read state and settings. Save applies connection changes; Cancel discards them."), 0, 5);
        layout.Controls.Add(Field("Server address", _endpoint), 0, 6);
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Theme.Muted;
        _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_status, 0, 7);
        layout.Controls.Add(TextLabel("Codex configuration"), 0, 8);
        _configuration.Dock = DockStyle.Fill;
        layout.Controls.Add(_configuration, 0, 9);
        layout.Controls.Add(TextLabel("Add this to Codex config.toml, or add a Streamable HTTP server using the address above. Keep the editor running. If a token is set, define VECTOR2D_MCP_TOKEN in the Codex process environment."), 0, 10);
        foreach (var box in new[] { _token, _endpoint, _configuration }) Theme.StyleTextBox(box);
        _enabled.AccessibleName = _enabled.Text;
        _allowChanges.AccessibleName = _allowChanges.Text;
        _configuration.AccessibleName = "Codex configuration";
        _enabled.CheckedChanged += (_, _) => RefreshFields();
        _port.ValueChanged += (_, _) => RefreshFields();
        _token.TextChanged += (_, _) => RefreshFields();
        SetSettings(new ApplicationSettings(), "Stopped");
        UiLocalization.Watch(this);
    }

    internal void SetSettings(ApplicationSettings settings, string status)
    {
        _enabled.Checked = settings.CodexIntegrationEnabled;
        _allowChanges.Checked = settings.CodexIntegrationAllowChanges;
        _port.Value = settings.CodexIntegrationPort;
        _token.Text = settings.CodexIntegrationAuthToken;
        _status.Text = $"{UiLocalization.T("Current server status")}: {UiLocalization.T(status)}";
        RefreshFields();
    }

    internal ApplicationSettings ApplyTo(ApplicationSettings settings) => settings with
    {
        CodexIntegrationEnabled = _enabled.Checked,
        CodexIntegrationAllowChanges = _allowChanges.Checked,
        CodexIntegrationPort = (int)_port.Value,
        CodexIntegrationAuthToken = _token.Text.Trim()
    };

    private void RefreshFields()
    {
        _allowChanges.Enabled = _enabled.Checked;
        _port.Enabled = _enabled.Checked;
        _token.Enabled = _enabled.Checked;
        _endpoint.Text = CodexBridgeServer.EndpointFor((int)_port.Value);
        _configuration.Text = $"[mcp_servers.vector2d]\r\nurl = \"{_endpoint.Text}\""
            + (string.IsNullOrWhiteSpace(_token.Text) ? "" : "\r\nbearer_token_env_var = \"VECTOR2D_MCP_TOKEN\"");
    }

    private static Label TextLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, ForeColor = Theme.Text, BackColor = Theme.Panel,
        Font = Theme.UiFont(), TextAlign = ContentAlignment.MiddleLeft
    };

    private static Control Field(string label, Control control)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.Controls.Add(TextLabel(label), 0, 0);
        control.AccessibleName = label;
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 7, 0, 7);
        row.Controls.Add(control, 1, 0);
        return row;
    }
}
