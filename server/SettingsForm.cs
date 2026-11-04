namespace WceRoomServer;

internal sealed class SettingsForm : Form
{
    private readonly ServerConfig _cfg;

    private readonly TextBox _serverName = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly TextBox _bind = new();
    private readonly CheckBox _relayEnabled = new();
    private readonly TextBox _relayHost = new();
    private readonly NumericUpDown _relayPortStart = new() { Minimum = 1024, Maximum = 65535 };
    private readonly NumericUpDown _relayPortCount = new() { Minimum = 1, Maximum = 5000 };
    private readonly NumericUpDown _roomTtl = new() { Minimum = 5, Maximum = 3600 };
    private readonly NumericUpDown _maxPlayers = new() { Minimum = 1, Maximum = 64 };
    private readonly NumericUpDown _maxRooms = new() { Minimum = 1, Maximum = 5000 };

    public SettingsForm(ServerConfig cfg)
    {
        _cfg = cfg;
        Text = "服务器设置";
        ClientSize = new Size(520, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 252);

        int y = 16;
        AddRow("服务器名称", _serverName, ref y, 240);
        AddRow("API 端口（需重启生效）", _port, ref y, 90);
        AddRow("监听地址", _bind, ref y, 140);
        AddRow("房间心跳超时（秒）", _roomTtl, ref y, 90);
        AddRow("单房间最大人数", _maxPlayers, ref y, 90);
        AddRow("最大房间数", _maxRooms, ref y, 90);

        y += 6;
        _relayEnabled.Text = "启用 UDP 中继（免去房主端口映射）";
        _relayEnabled.AutoSize = true;
        _relayEnabled.Location = new Point(150, y);
        Controls.Add(_relayEnabled);
        y += 34;

        AddRow("中继对外地址", _relayHost, ref y, 220);
        AddRow("中继端口起始", _relayPortStart, ref y, 90);
        AddRow("中继端口数量", _relayPortCount, ref y, 90);

        Label tip = new()
        {
            Text = "中继对外地址：公网部署填公网 IP 或域名；局域网留空自动取内网地址。",
            ForeColor = Color.FromArgb(110, 125, 145),
            AutoSize = false,
            Location = new Point(20, y + 4),
            Size = new Size(480, 36)
        };
        Controls.Add(tip);
        y += 44;

        Button save = new()
        {
            Text = "保存",
            DialogResult = DialogResult.None,
            Location = new Point(310, ClientSize.Height - 46),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(28, 40, 60),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        save.Click += OnSave;
        Controls.Add(save);

        Button cancel = new()
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(410, ClientSize.Height - 46),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(220, 224, 232),
            ForeColor = Color.FromArgb(40, 50, 65),
            Cursor = Cursors.Hand
        };
        Controls.Add(cancel);
        CancelButton = cancel;
        AcceptButton = save;

        LoadValues();
    }

    private void LoadValues()
    {
        _serverName.Text = _cfg.ServerName;
        _port.Value = Math.Clamp(_cfg.Port, 1, 65535);
        _bind.Text = _cfg.Bind;
        _roomTtl.Value = Math.Clamp(_cfg.RoomTtlSeconds, 5, 3600);
        _maxPlayers.Value = Math.Clamp(_cfg.MaxPlayersPerRoom, 1, 64);
        _maxRooms.Value = Math.Clamp(_cfg.MaxRooms, 1, 5000);
        _relayEnabled.Checked = _cfg.RelayEnabled;
        _relayHost.Text = _cfg.RelayHost;
        _relayPortStart.Value = Math.Clamp(_cfg.RelayPortStart, 1024, 65535);
        _relayPortCount.Value = Math.Clamp(_cfg.RelayPortCount, 1, 5000);
    }

    private void AddRow(string caption, Control input, ref int y, int width)
    {
        Label label = new()
        {
            Text = caption,
            AutoSize = false,
            Location = new Point(20, y + 4),
            Size = new Size(130, 22),
            ForeColor = Color.FromArgb(60, 72, 90)
        };
        Controls.Add(label);

        input.Location = new Point(150, y);
        input.Width = width;
        Controls.Add(input);
        y += 34;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        int relayStart = (int)_relayPortStart.Value;
        int relayCount = (int)_relayPortCount.Value;
        if (relayStart + relayCount - 1 > 65535)
        {
            MessageBox.Show(this, "中继端口范围超出 65535，请调小起始端口或数量。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool portChanged = _cfg.Port != (int)_port.Value;
        _cfg.ServerName = _serverName.Text.Trim();
        _cfg.Port = (int)_port.Value;
        _cfg.Bind = _bind.Text.Trim().Length > 0 ? _bind.Text.Trim() : "0.0.0.0";
        _cfg.RoomTtlSeconds = (int)_roomTtl.Value;
        _cfg.MaxPlayersPerRoom = (int)_maxPlayers.Value;
        _cfg.MaxRooms = (int)_maxRooms.Value;
        _cfg.RelayEnabled = _relayEnabled.Checked;
        _cfg.RelayHost = _relayHost.Text.Trim();
        _cfg.RelayPortStart = relayStart;
        _cfg.RelayPortCount = relayCount;
        _cfg.Save();
        Relay.Init(_cfg);

        if (portChanged)
        {
            MessageBox.Show(this, "API 端口已修改，重启服务器后生效。中继设置已立即生效（新房间使用）。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
