using System.Net.Sockets;

namespace WceRoomServer;

internal sealed class MainForm : Form
{
    private readonly ServerConfig _cfg;
    private readonly Server _server;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };

    private readonly DataGridView _roomGrid = new();
    private readonly DataGridView _playerGrid = new();
    private readonly RichTextBox _logBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _roomCountLabel = new();
    private readonly Label _onlineLabel = new();
    private readonly Label _accountLabel = new();
    private readonly TextBox _apiBox = new();

    private string _roomSignature = "";
    private string _playerSignature = "";
    private string _selectedRoomId = "";
    private bool _serverRunning;

    public MainForm(ServerConfig cfg)
    {
        _cfg = cfg;
        _server = new Server(cfg);
        BuildUi();
        Log.OnLog += AppendLog;
        Load += OnFormLoad;
        FormClosing += OnFormClosing;
        _refreshTimer.Tick += (_, _) => RefreshData();
        _refreshTimer.Start();
    }

    private void BuildUi()
    {
        Text = "我带过的最差一届 · 自定义房间服务器";
        Width = 1040;
        Height = 720;
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 252);

        Panel header = new()
        {
            Dock = DockStyle.Top,
            Height = 96,
            BackColor = Color.FromArgb(28, 40, 60),
            Padding = new Padding(18, 10, 18, 10)
        };

        Label title = new()
        {
            Text = "自定义房间服务器",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 12)
        };
        header.Controls.Add(title);

        _statusLabel.Text = "● 正在启动...";
        _statusLabel.ForeColor = Color.FromArgb(255, 199, 94);
        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _statusLabel.Location = new Point(20, 54);
        header.Controls.Add(_statusLabel);

        Panel apiPanel = new()
        {
            Dock = DockStyle.Right,
            Width = 470,
            BackColor = Color.Transparent
        };

        Label apiCaption = new()
        {
            Text = "服务端地址",
            ForeColor = Color.FromArgb(170, 186, 205),
            AutoSize = true,
            Location = new Point(0, 2)
        };
        apiPanel.Controls.Add(apiCaption);

        _apiBox.ReadOnly = true;
        _apiBox.BackColor = Color.FromArgb(40, 55, 78);
        _apiBox.ForeColor = Color.FromArgb(210, 225, 245);
        _apiBox.BorderStyle = BorderStyle.FixedSingle;
        _apiBox.Location = new Point(0, 22);
        _apiBox.Width = 246;
        _apiBox.Font = new Font("Consolas", 10F);
        apiPanel.Controls.Add(_apiBox);

        Button copyApiBtn = MakeButton("复制", Color.FromArgb(58, 76, 105));
        copyApiBtn.Size = new Size(64, 26);
        copyApiBtn.Location = new Point(252, 22);
        copyApiBtn.Click += (_, _) => CopyText(_apiBox.Text);
        apiPanel.Controls.Add(copyApiBtn);

        Button settingsBtn = MakeButton("设置", Color.FromArgb(58, 76, 105));
        settingsBtn.Size = new Size(64, 26);
        settingsBtn.Location = new Point(322, 22);
        settingsBtn.Click += (_, _) => OpenSettings();
        apiPanel.Controls.Add(settingsBtn);

        Button aboutBtn = MakeButton("关于", Color.FromArgb(58, 76, 105));
        aboutBtn.Size = new Size(64, 26);
        aboutBtn.Location = new Point(392, 22);
        aboutBtn.Click += (_, _) => OpenAbout();
        apiPanel.Controls.Add(aboutBtn);

        Label hint = new()
        {
            Text = "房间超时自动下线 · 账号仅保存在内存，重启后需重新注册",
            ForeColor = Color.FromArgb(140, 158, 180),
            AutoSize = false,
            Location = new Point(0, 54),
            Size = new Size(460, 18)
        };
        apiPanel.Controls.Add(hint);

        header.Controls.Add(apiPanel);

        Panel stats = new()
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.White,
            Padding = new Padding(18, 8, 18, 8)
        };
        stats.Controls.Add(MakeStatCard("在线房间", _roomCountLabel, 0));
        stats.Controls.Add(MakeStatCard("在线玩家", _onlineLabel, 190));
        stats.Controls.Add(MakeStatCard("注册账号", _accountLabel, 380));

        TabControl tabs = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Point(16, 6)
        };

        TabPage roomPage = new("房间列表");
        roomPage.BackColor = Color.White;
        ConfigureRoomGrid();
        Panel roomTools = new()
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(8, 6, 8, 6)
        };
        Button refreshBtn = MakeButton("刷新", Color.FromArgb(43, 92, 230));
        refreshBtn.Location = new Point(8, 7);
        refreshBtn.Click += (_, _) => RefreshData(true);
        roomTools.Controls.Add(refreshBtn);

        Button copyCodeBtn = MakeButton("复制加入码", Color.FromArgb(90, 104, 125));
        copyCodeBtn.Location = new Point(96, 7);
        copyCodeBtn.Click += (_, _) => CopySelectedJoinCode();
        roomTools.Controls.Add(copyCodeBtn);

        Button closeRoomBtn = MakeButton("关闭选中房间", Color.FromArgb(196, 66, 66));
        closeRoomBtn.Location = new Point(200, 7);
        closeRoomBtn.Click += (_, _) => CloseSelectedRoom();
        roomTools.Controls.Add(closeRoomBtn);

        roomPage.Controls.Add(roomGridHost());
        roomPage.Controls.Add(roomTools);
        tabs.TabPages.Add(roomPage);

        TabPage playerPage = new("玩家账号");
        playerPage.BackColor = Color.White;
        ConfigurePlayerGrid();
        Label playerHint = new()
        {
            Text = "账号由游戏启动时输入的名字自动注册，PlayerId 与游戏本机的 SavesDir/PlayerIdentity.bytes 对应。",
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(10, 8, 0, 0),
            ForeColor = Color.FromArgb(100, 112, 128)
        };
        playerPage.Controls.Add(playerPanel());
        playerPage.Controls.Add(playerHint);
        tabs.TabPages.Add(playerPage);

        TabPage logPage = new("运行日志");
        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.BackColor = Color.FromArgb(24, 28, 36);
        _logBox.ForeColor = Color.FromArgb(190, 205, 225);
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Font = new Font("Consolas", 10F);
        _logBox.WordWrap = false;
        Panel logTools = new()
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6)
        };
        Button clearLogBtn = MakeButton("清空日志", Color.FromArgb(90, 104, 125));
        clearLogBtn.Location = new Point(8, 6);
        clearLogBtn.Click += (_, _) => _logBox.Clear();
        logTools.Controls.Add(clearLogBtn);
        logPage.Controls.Add(_logBox);
        logPage.Controls.Add(logTools);
        tabs.TabPages.Add(logPage);

        StatusStrip strip = new();
        _lastRefreshStatusLabel = new ToolStripStatusLabel("等待刷新")
        {
            ForeColor = Color.FromArgb(90, 104, 125)
        };
        strip.Items.Add(_lastRefreshStatusLabel);
        Controls.Add(tabs);
        Controls.Add(stats);
        Controls.Add(header);
        Controls.Add(strip);
    }

    private ToolStripStatusLabel _lastRefreshStatusLabel = null!;

    private Control roomGridHost()
    {
        Panel panel = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
        panel.Controls.Add(_roomGrid);
        return panel;
    }

    private Control playerPanel()
    {
        Panel panel = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
        panel.Controls.Add(_playerGrid);
        return panel;
    }

    private static Control MakeStatCard(string caption, Label valueLabel, int x)
    {
        Panel card = new()
        {
            Location = new Point(x, 8),
            Size = new Size(172, 48),
            BackColor = Color.FromArgb(240, 245, 255),
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        Label cap = new()
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 138),
            Location = new Point(10, 3)
        };
        valueLabel.Text = "0";
        valueLabel.AutoSize = true;
        valueLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        valueLabel.ForeColor = Color.FromArgb(43, 92, 230);
        valueLabel.Location = new Point(10, 21);
        card.Controls.Add(cap);
        card.Controls.Add(valueLabel);
        return card;
    }

    private static Button MakeButton(string text, Color back)
    {
        return new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(84, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
    }

    private void ConfigureRoomGrid()
    {
        _roomGrid.Dock = DockStyle.Fill;
        _roomGrid.BackgroundColor = Color.White;
        _roomGrid.BorderStyle = BorderStyle.None;
        _roomGrid.AllowUserToAddRows = false;
        _roomGrid.AllowUserToDeleteRows = false;
        _roomGrid.AllowUserToResizeRows = false;
        _roomGrid.ReadOnly = true;
        _roomGrid.RowHeadersVisible = false;
        _roomGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _roomGrid.MultiSelect = false;
        _roomGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _roomGrid.ColumnHeadersHeight = 34;
        _roomGrid.RowTemplate.Height = 32;
        _roomGrid.GridColor = Color.FromArgb(232, 236, 243);
        _roomGrid.EnableHeadersVisualStyles = false;
        _roomGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 247, 252);
        _roomGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(70, 84, 102);
        _roomGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _roomGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 254);

        _roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "房间名", FillWeight = 22 });
        _roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "host", HeaderText = "主机", FillWeight = 14 });
        _roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "code", HeaderText = "加入码", FillWeight = 24 });
        _roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "players", HeaderText = "人数", FillWeight = 7 });
        _roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "状态", FillWeight = 9 });
        _roomGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "updated", HeaderText = "最后心跳", FillWeight = 13 });
        _roomGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "close",
            HeaderText = "操作",
            Text = "关闭",
            UseColumnTextForButtonValue = true,
            FlatStyle = FlatStyle.Flat,
            FillWeight = 8
        });

        _roomGrid.CellContentClick += OnRoomCellClick;
        _roomGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                CopySelectedJoinCode();
            }
        };
    }

    private void ConfigurePlayerGrid()
    {
        _playerGrid.Dock = DockStyle.Fill;
        _playerGrid.BackgroundColor = Color.White;
        _playerGrid.BorderStyle = BorderStyle.None;
        _playerGrid.AllowUserToAddRows = false;
        _playerGrid.AllowUserToDeleteRows = false;
        _playerGrid.AllowUserToResizeRows = false;
        _playerGrid.ReadOnly = true;
        _playerGrid.RowHeadersVisible = false;
        _playerGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _playerGrid.MultiSelect = false;
        _playerGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _playerGrid.ColumnHeadersHeight = 34;
        _playerGrid.RowTemplate.Height = 32;
        _playerGrid.GridColor = Color.FromArgb(232, 236, 243);
        _playerGrid.EnableHeadersVisualStyles = false;
        _playerGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 247, 252);
        _playerGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(70, 84, 102);
        _playerGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _playerGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 254);

        _playerGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "名字", FillWeight = 18 });
        _playerGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "pid", HeaderText = "PlayerId", FillWeight = 30 });
        _playerGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "created", HeaderText = "注册时间", FillWeight = 18 });
        _playerGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "seen", HeaderText = "最后登录", FillWeight = 18 });
        _playerGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "visits", HeaderText = "登录次数", FillWeight = 10 });
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        string apiHost = Server.GetLocalIPv4().First();
        _apiBox.Text = $"http://{apiHost}:{_cfg.Port}";
        try
        {
            _server.Start();
            _serverRunning = true;
            _statusLabel.Text = "● 服务运行中";
            _statusLabel.ForeColor = Color.FromArgb(105, 220, 140);
            Log.Info("服务器已启动");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "● 启动失败：" + ex.Message;
            _statusLabel.ForeColor = Color.FromArgb(255, 120, 110);
            Log.Error("服务器启动失败：" + ex.Message);
            MessageBox.Show(this, "服务器启动失败：\n" + ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshData(true);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        if (_serverRunning)
        {
            _server.Stop();
            Log.Info("服务器已停止");
        }
    }

    private void RefreshData(bool force = false)
    {
        List<Room> rooms = Store.Rooms.OrderByDescending(r => r.UpdatedAt).ToList();
        List<Player> players = Store.Players.OrderByDescending(p => p.LastSeenAt).ToList();

        string roomSig = string.Join("|", rooms.Select(r => $"{r.Id},{r.PlayerCount},{r.Status},{r.UpdatedAt}"));
        string playerSig = string.Join("|", players.Select(p => $"{p.PlayerId},{p.Name},{p.LastSeenAt}"));

        if (force || roomSig != _roomSignature)
        {
            _roomSignature = roomSig;
            UpdateRoomRows(rooms);
        }
        if (force || playerSig != _playerSignature)
        {
            _playerSignature = playerSig;
            UpdatePlayerRows(players);
        }

        int online = rooms.Sum(r => r.PlayerCount);
        _roomCountLabel.Text = rooms.Count.ToString();
        _onlineLabel.Text = online.ToString();
        _accountLabel.Text = players.Count.ToString();
        _lastRefreshStatusLabel.Text = "最后刷新 " + DateTime.Now.ToString("HH:mm:ss") + $" · 端口 {_cfg.Port} · 超时 {_cfg.RoomTtlSeconds}s";
    }

    private void UpdateRoomRows(List<Room> rooms)
    {
        string keepId = _selectedRoomId;
        _roomGrid.SuspendLayout();
        _roomGrid.Rows.Clear();
        foreach (Room room in rooms)
        {
            int idx = _roomGrid.Rows.Add(
                room.Name,
                room.HostName,
                room.JoinCode,
                $"{room.PlayerCount}/{room.MaxPlayers}",
                room.Status == "Running" ? "已开局" : "可加入",
                FormatTime(room.UpdatedAt));
            DataGridViewRow row = _roomGrid.Rows[idx];
            row.Tag = room.Id;
            if (room.Status == "Running")
            {
                row.Cells["status"].Style.ForeColor = Color.FromArgb(196, 132, 40);
            }
            else
            {
                row.Cells["status"].Style.ForeColor = Color.FromArgb(40, 150, 90);
            }
            if (!string.IsNullOrEmpty(keepId) && room.Id == keepId)
            {
                row.Selected = true;
            }
        }
        _roomGrid.ResumeLayout();
        if (_roomGrid.SelectedRows.Count > 0 && _roomGrid.SelectedRows[0].Tag is string id)
        {
            _selectedRoomId = id;
        }
        else
        {
            _selectedRoomId = "";
        }
    }

    private void UpdatePlayerRows(List<Player> players)
    {
        _playerGrid.SuspendLayout();
        _playerGrid.Rows.Clear();
        foreach (Player p in players)
        {
            _playerGrid.Rows.Add(p.Name, p.PlayerId, FormatTime(p.CreatedAt), FormatTime(p.LastSeenAt), p.VisitCount);
        }
        _playerGrid.ResumeLayout();
    }

    private void OnRoomCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _roomGrid.Columns[e.ColumnIndex].Name != "close")
        {
            return;
        }
        DataGridViewRow row = _roomGrid.Rows[e.RowIndex];
        string roomId = row.Tag as string ?? "";
        string roomName = row.Cells["name"].Value?.ToString() ?? "";
        if (string.IsNullOrEmpty(roomId))
        {
            return;
        }
        if (MessageBox.Show(this, $"确定关闭房间「{roomName}」？\n玩家将无法再通过房间列表加入。", "关闭房间",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        Store.RemoveRoom(roomId);
        Log.Info($"管理面板关闭房间：{roomName} ({roomId})，房间内玩家将在 5 秒内被踢下线");
        _selectedRoomId = "";
        RefreshData(true);
    }

    private void CloseSelectedRoom()
    {
        if (_roomGrid.SelectedRows.Count == 0)
        {
            return;
        }
        DataGridViewRow row = _roomGrid.SelectedRows[0];
        string roomName = row.Cells["name"].Value?.ToString() ?? "";
        string roomId = row.Tag as string ?? "";
        if (string.IsNullOrEmpty(roomId))
        {
            return;
        }
        if (MessageBox.Show(this, $"确定关闭房间「{roomName}」？", "关闭房间",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        Store.RemoveRoom(roomId);
        Log.Info($"管理面板关闭房间：{roomName} ({roomId})，房间内玩家将在 5 秒内被踢下线");
        _selectedRoomId = "";
        RefreshData(true);
    }

    private void CopySelectedJoinCode()
    {
        if (_roomGrid.SelectedRows.Count == 0)
        {
            return;
        }
        string code = _roomGrid.SelectedRows[0].Cells["code"].Value?.ToString() ?? "";
        CopyText(code);
    }

    private void OpenAbout()
    {
        using AboutForm form = new();
        form.ShowDialog(this);
    }

    private void OpenSettings()
    {
        using SettingsForm form = new(_cfg);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            AppendLog("服务器配置已保存（中继：" +
                      (_cfg.RelayEnabled ? (string.IsNullOrEmpty(_cfg.RelayHost) ? "自动地址" : _cfg.RelayHost) : "已关闭") + "）");
        }
    }

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
            Log.Info("已复制：" + text);
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败：" + ex.Message);
        }
    }

    private void AppendLog(string line)
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLog), line);
            return;
        }
        if (_logBox.Lines.Length > 3000)
        {
            _logBox.Clear();
        }
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private static string FormatTime(long ms)
    {
        return ms <= 0 ? "-" : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm:ss");
    }
}
