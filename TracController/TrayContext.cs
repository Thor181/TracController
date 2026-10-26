using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TracController;

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new();
    private readonly Icon _iconConnected = MakeIcon(Color.FromArgb(46, 160, 67));
    private readonly Icon _iconDisconnected = MakeIcon(Color.FromArgb(140, 140, 140));
    private readonly Icon _iconBusy = MakeIcon(Color.FromArgb(219, 160, 30));
    private readonly Icon _iconError = MakeIcon(Color.FromArgb(200, 50, 50));

    private TracClient? _client;
    private IReadOnlyList<VpnSite> _sites = [];
    private string? _lastError;
    private string? _busyText;
    private bool _refreshing;

    public TrayContext()
    {
        _tray = new NotifyIcon
        {
            Icon = _iconDisconnected,
            Text = "TracController",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseUp += (_, e) =>
        {
            // Левый клик тоже открывает меню — так быстрее переключаться
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_tray, null);
        };
        _menu.Opening += (_, _) =>
        {
            BuildMenu();
            _ = RefreshAsync();
        };

        _pollTimer.Interval = (int)PollInterval.TotalMilliseconds;
        _pollTimer.Tick += (_, _) => _ = RefreshAsync();

        InitClient();
        _pollTimer.Start();
        _ = RefreshAsync();
    }

    private void InitClient()
    {
        var path = TracLocator.Find(_settings.TracPath);
        _client = path != null ? new TracClient(path) : null;
        if (_client == null)
            _lastError = "trac.exe не найден. Укажите путь вручную в меню.";
    }

    private async Task RefreshAsync()
    {
        if (_client == null || _refreshing || _busyText != null)
        {
            UpdateTrayState();
            return;
        }
        _refreshing = true;
        try
        {
            _sites = await _client.GetSitesAsync();
            _lastError = _sites.Count == 0 ? "trac info не вернул ни одного шлюза" : null;
        }
        catch (Exception ex)
        {
            _lastError = ex is TracException te && te.Output.Length > 0 ? $"{ex.Message}: {te.Output}" : ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
        UpdateTrayState();
        if (_menu.Visible)
            BuildMenu();
    }

    private async Task SwitchToAsync(VpnSite target)
    {
        if (_client == null || _busyText != null)
            return;

        try
        {
            if (_sites.Any(s => s.IsConnected || s.IsConnecting))
            {
                SetBusy("Отключение…");
                var disconnect = await _client.DisconnectAsync();
                if (!disconnect.Success)
                {
                    ShowError("Не удалось отключиться", disconnect);
                    return;
                }
            }

            SetBusy($"Подключение к {target.Name}…");
            var connect = await _client.ConnectAsync(target.Name, _settings.UseConnectGui);
            if (connect.Success)
                _tray.ShowBalloonTip(3000, "VPN подключён", target.Name, ToolTipIcon.Info);
            else
                ShowError($"Не удалось подключиться к {target.Name}", connect);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "Ошибка", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            SetBusy(null);
            await RefreshAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        if (_client == null || _busyText != null)
            return;
        try
        {
            SetBusy("Отключение…");
            var result = await _client.DisconnectAsync();
            if (result.Success)
                _tray.ShowBalloonTip(2000, "VPN отключён", " ", ToolTipIcon.Info);
            else
                ShowError("Не удалось отключиться", result);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "Ошибка", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            SetBusy(null);
            await RefreshAsync();
        }
    }

    private void ShowError(string title, TracResult result)
    {
        var details = result.TimedOut ? "trac.exe не ответил вовремя" : result.Output;
        if (string.IsNullOrWhiteSpace(details))
            details = $"Код выхода {result.ExitCode}";
        _tray.ShowBalloonTip(8000, title, Truncate(details, 250), ToolTipIcon.Error);
    }

    private void SetBusy(string? text)
    {
        _busyText = text;
        UpdateTrayState();
        if (_menu.Visible)
            BuildMenu();
    }

    private void UpdateTrayState()
    {
        var connected = _sites.FirstOrDefault(s => s.IsConnected);
        string text;
        if (_busyText != null)
        {
            _tray.Icon = _iconBusy;
            text = _busyText;
        }
        else if (_client == null || (_lastError != null && _sites.Count == 0))
        {
            _tray.Icon = _iconError;
            text = _lastError ?? "Ошибка";
        }
        else if (connected != null)
        {
            _tray.Icon = _iconConnected;
            text = $"VPN: {connected.Name}";
        }
        else
        {
            _tray.Icon = _iconDisconnected;
            text = "VPN: не подключён";
        }
        // Ограничение Windows на подсказку значка — 127 символов
        _tray.Text = Truncate(text, 127);
    }

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        _menu.Items.Clear();

        if (_busyText != null)
            _menu.Items.Add(new ToolStripMenuItem(_busyText) { Enabled = false });

        if (_client == null)
        {
            _menu.Items.Add(new ToolStripMenuItem("trac.exe не найден") { Enabled = false });
        }
        else if (_sites.Count == 0)
        {
            var text = _lastError ?? (_refreshing ? "Загрузка списка шлюзов…" : "Шлюзы не найдены");
            _menu.Items.Add(new ToolStripMenuItem(Truncate(text, 80)) { Enabled = false });
        }
        else
        {
            _menu.Items.Add(new ToolStripMenuItem("Шлюзы") { Enabled = false, Font = new Font(_menu.Font, FontStyle.Bold) });
            foreach (var site in _sites)
            {
                var label = site.Name;
                if (!string.IsNullOrEmpty(site.Gateway) && !string.Equals(site.Gateway, site.Name, StringComparison.OrdinalIgnoreCase))
                    label += $"   ({site.Gateway})";
                if (site.IsConnecting)
                    label += "   — подключение…";

                var item = new ToolStripMenuItem(label)
                {
                    Checked = site.IsConnected,
                    Enabled = _busyText == null && !site.IsConnected,
                    ToolTipText = site.Status != null ? $"Статус: {site.Status}" : null,
                };
                var target = site;
                item.Click += (_, _) => _ = SwitchToAsync(target);
                _menu.Items.Add(item);
            }

            var disconnect = new ToolStripMenuItem("Отключиться")
            {
                Enabled = _busyText == null && _sites.Any(s => s.IsConnected || s.IsConnecting),
            };
            disconnect.Click += (_, _) => _ = DisconnectAsync();
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(disconnect);
        }

        _menu.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("Обновить") { Enabled = _client != null && _busyText == null };
        refresh.Click += (_, _) => _ = RefreshAsync();
        _menu.Items.Add(refresh);

        var settings = new ToolStripMenuItem("Настройки");
        var gui = new ToolStripMenuItem("Подключаться через окно ввода пароля (connectgui)")
        {
            Checked = _settings.UseConnectGui,
            ToolTipText = "Включите, если шлюз требует ввода пароля, OTP или MFA при каждом подключении",
        };
        gui.Click += (_, _) =>
        {
            _settings.UseConnectGui = !_settings.UseConnectGui;
            _settings.Save();
        };
        settings.DropDownItems.Add(gui);
        var pickPath = new ToolStripMenuItem("Указать путь к trac.exe…");
        pickPath.Click += (_, _) => PickTracPath();
        settings.DropDownItems.Add(pickPath);
        settings.DropDownItems.Add(new ToolStripMenuItem(_client?.TracPath ?? "trac.exe не найден") { Enabled = false });
        _menu.Items.Add(settings);

        var exit = new ToolStripMenuItem("Выход");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);

        _menu.ResumeLayout();
    }

    private void PickTracPath()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Укажите trac.exe",
            Filter = "trac.exe|trac.exe|Программы (*.exe)|*.exe",
            InitialDirectory = _client != null
                ? Path.GetDirectoryName(_client.TracPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;
        _settings.TracPath = dialog.FileName;
        _settings.Save();
        _sites = [];
        InitClient();
        _ = RefreshAsync();
    }

    protected override void ExitThreadCore()
    {
        _pollTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Рисует значок-щит нужного цвета, чтобы не таскать с собой .ico файлы.</summary>
    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var shield = new GraphicsPath();
            shield.AddLines(new PointF[]
            {
                new(16, 1), new(29, 6), new(29, 15),
            });
            shield.AddBezier(29, 15, 29, 23, 23, 28, 16, 31);
            shield.AddBezier(16, 31, 9, 28, 3, 23, 3, 15);
            shield.AddLines(new PointF[] { new(3, 15), new(3, 6) });
            shield.CloseFigure();
            using var fill = new SolidBrush(color);
            g.FillPath(fill, shield);
            using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            // замочек: дужка + корпус
            g.DrawArc(pen, 11, 7, 10, 10, 180, 180);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 9.5f, 13, 13, 10);
        }
        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
