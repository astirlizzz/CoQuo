namespace CoQuo;

internal sealed class TrayApplicationContext : ApplicationContext
{
    // The usage endpoint has no published polling budget; stay well above the 1-minute option.
    private const int ClaudeMinIntervalSeconds = 120;

    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _refresh;
    private readonly ToolStripMenuItem _autostart;
    private readonly Dictionary<int, ToolStripMenuItem> _intervals = new();
    private readonly Dictionary<int, ToolStripMenuItem> _iconSizes = new();
    private readonly ToolStripMenuItem _iconSizeStatus = new() { Enabled = false, Visible = false };
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _dispatcher;
    private readonly Settings _settings;
    private readonly CodexAppServerClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly QuotaIndicator _codex;
    private readonly QuotaIndicator _claude;
    private bool _exiting;

    public TrayApplicationContext()
    {
        _settings = Settings.Load();
        _dispatcher = new Control();
        _ = _dispatcher.Handle; // invoke target even though the app has no visible window

        _menu = new ContextMenuStrip();
        _codex = new QuotaIndicator("Codex", WindowOf(_settings.CodexDisplay, QuotaWindow.Weekly), IconProvider.Codex,
            ReadCodexAsync,
            // An RPC or broken pipe may poison this subprocess; restart on next poll.
            onFailure: () => _exiting ? Task.CompletedTask : _client.StopAsync(),
            minIntervalSeconds: 0, () => _settings.RefreshSeconds, _menu, _lifetime.Token);
        _claude = new QuotaIndicator("Claude", WindowOf(_settings.ClaudeDisplay, QuotaWindow.FiveHour),
            IconProvider.Claude, ReadClaudeAsync,
            onFailure: null, ClaudeMinIntervalSeconds, () => _settings.RefreshSeconds, _menu, _lifetime.Token);

        _refresh = new ToolStripMenuItem("Refresh now", null, (_, _) =>
        {
            _codex.RequestManualRefresh();
            _claude.RequestManualRefresh();
        });
        var intervals = new ToolStripMenuItem("Refresh interval");
        foreach (int seconds in new[] { 60, 120, 300, 600 })
        {
            var option = new ToolStripMenuItem(seconds < 60 ? $"{seconds} sec" : $"{seconds / 60} min")
            {
                Checked = seconds == _settings.RefreshSeconds
            };
            var value = seconds;
            option.Click += (_, _) => ChangeInterval(value);
            _intervals.Add(value, option);
            intervals.DropDownItems.Add(option);
        }
        var iconSizes = new ToolStripMenuItem("Icon size") { Enabled = TrayIconSizeClient.IsAvailable };
        foreach (int size in new[] { 16, 20, 24, 28, 32 })
        {
            var option = new ToolStripMenuItem(size == TrayIconSizeClient.StandardSize ? "Standard" : $"{size} px")
            {
                Checked = size == _settings.IconSize
            };
            var value = size;
            option.Click += (_, _) => ChangeIconSize(value);
            _iconSizes.Add(value, option);
            iconSizes.DropDownItems.Add(option);
        }
        iconSizes.DropDownItems.Add(_iconSizeStatus);
        var codexDisplay = DisplayMenu("Codex", _codex, codex: true);
        var claudeDisplay = DisplayMenu("Claude", _claude, codex: false);
        _autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Settings.IsAutoStartEnabled()
        };
        _autostart.Click += (_, _) => ToggleAutoStart();

        _menu.Items.AddRange(_codex.MenuItems);
        _menu.Items.AddRange(_claude.MenuItems);
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _refresh, intervals, codexDisplay, claudeDisplay, iconSizes, _autostart, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit CoQuo", null, (_, _) => ExitThread())
        });
        _menu.Opening += (_, _) => UpdateUi();

        _codex.IconSize = _claude.IconSize = _settings.IconSize;
        _codex.Enabled = _settings.CodexDisplay != TrayDisplay.Hidden;
        _claude.Enabled = _settings.ClaudeDisplay != TrayDisplay.Hidden;

        _client.RateLimitsUpdated += OnLimitsUpdated;
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            if (_exiting) return;
            _codex.Tick();
            _claude.Tick();
            UpdateUi();
        };
        _timer.Start(); // First poll starts on the message-loop thread, with WinForms SynchronizationContext.
    }

    private static QuotaWindow WindowOf(TrayDisplay display, QuotaWindow fallback) => display switch
    {
        TrayDisplay.FiveHour => QuotaWindow.FiveHour,
        TrayDisplay.Weekly => QuotaWindow.Weekly,
        _ => fallback
    };

    // "Codex ▸ 5-hour limit / Weekly limit / Hide"; a window the last response lacked is grayed.
    private ToolStripMenuItem DisplayMenu(string name, QuotaIndicator indicator, bool codex)
    {
        var menu = new ToolStripMenuItem(name);
        var fiveHour = new ToolStripMenuItem("5-hour limit", null, (_, _) => ChangeDisplay(codex, TrayDisplay.FiveHour));
        var weekly = new ToolStripMenuItem("Weekly limit", null, (_, _) => ChangeDisplay(codex, TrayDisplay.Weekly));
        var hide = new ToolStripMenuItem("Hide", null, (_, _) => ChangeDisplay(codex, TrayDisplay.Hidden));
        menu.DropDownItems.AddRange(new ToolStripItem[] { fiveHour, weekly, new ToolStripSeparator(), hide });
        menu.DropDownOpening += (_, _) =>
        {
            var current = codex ? _settings.CodexDisplay : _settings.ClaudeDisplay;
            var other = codex ? _settings.ClaudeDisplay : _settings.CodexDisplay;
            fiveHour.Checked = current == TrayDisplay.FiveHour;
            weekly.Checked = current == TrayDisplay.Weekly;
            hide.Checked = current == TrayDisplay.Hidden;
            // Unknown before the first reading, so only a confirmed absence grays an option.
            fiveHour.Enabled = indicator.Reports(QuotaWindow.FiveHour) != false;
            weekly.Enabled = indicator.Reports(QuotaWindow.Weekly) != false;
            // The last visible icon carries the menu, so it cannot be hidden.
            hide.Enabled = other != TrayDisplay.Hidden;
        };
        return menu;
    }

    private void ChangeDisplay(bool codex, TrayDisplay display)
    {
        var indicator = codex ? _codex : _claude;
        if (codex) _settings.CodexDisplay = display;
        else _settings.ClaudeDisplay = display;
        indicator.Window = WindowOf(display, indicator.Window);
        indicator.Enabled = display != TrayDisplay.Hidden;
        // Do not keep a codex app-server running when nobody looks at its numbers.
        if (codex && display == TrayDisplay.Hidden && !_codex.IsBusy) _ = _client.StopAsync();
        SaveSettings();
    }

    private async Task<QuotaReading> ReadCodexAsync(CancellationToken ct)
    {
        System.Text.Json.JsonElement response;
        try
        {
            if (!_client.IsRunning) await _client.StartAsync(ct);
            response = await _client.ReadRateLimitsAsync(ct);
        }
        finally
        {
            // The icon may have been hidden while this read was in flight.
            if (_settings.CodexDisplay == TrayDisplay.Hidden && !_exiting) await _client.StopAsync();
        }
        if (!UsageParser.TryParse(response, out var reading) || reading is null)
            throw new FormatException("No 5-hour or weekly quota in the Codex response");
        return reading;
    }

    private static async Task<QuotaReading> ReadClaudeAsync(CancellationToken ct)
    {
        var response = await ClaudeUsageClient.ReadUsageAsync(ct);
        if (!ClaudeUsageParser.TryParse(response, out var reading) || reading is null)
            throw new FormatException("No 5-hour or weekly quota in the Claude response");
        return reading;
    }

    private void OnLimitsUpdated(System.Text.Json.JsonElement parameters)
    {
        if (!UsageParser.TryParse(parameters, out var parsed) || parsed is null) return;
        try
        {
            if (_dispatcher.IsHandleCreated && !_dispatcher.IsDisposed)
                _dispatcher.BeginInvoke((MethodInvoker)(() =>
                {
                    if (!_exiting) _codex.AcceptReading(parsed, partial: true);
                }));
        }
        catch (ObjectDisposedException) { /* shutdown */ }
        catch (InvalidOperationException) { /* shutdown */ }
    }

    private void ChangeInterval(int seconds)
    {
        _settings.RefreshSeconds = seconds;
        foreach (var item in _intervals) item.Value.Checked = item.Key == seconds;
        SaveSettings();
        _codex.OnIntervalChanged();
        _claude.OnIntervalChanged();
    }

    private void ChangeIconSize(int size)
    {
        _settings.IconSize = size;
        foreach (var item in _iconSizes) item.Value.Checked = item.Key == size;
        _codex.IconSize = _claude.IconSize = size;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "CoQuo — settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleAutoStart()
    {
        try
        {
            Settings.SetAutoStart(!_autostart.Checked);
            _autostart.Checked = Settings.IsAutoStartEnabled();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "CoQuo — startup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateUi()
    {
        if (_exiting) return;
        _codex.UpdateUi();
        _claude.UpdateUi();
        _refresh.Enabled = (_codex.Enabled && !_codex.IsBusy) || (_claude.Enabled && !_claude.IsBusy);
        var sizeError = _codex.IconSizeError ?? _claude.IconSizeError;
        _iconSizeStatus.Visible = sizeError is not null;
        _iconSizeStatus.Text = sizeError is null ? "" : $"Error: {sizeError}";
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _timer.Stop();
        _lifetime.Cancel();
        _codex.Dispose();
        _claude.Dispose();
        _menu.Dispose();
        _timer.Dispose();
        _dispatcher.Dispose();
        // Must not block the WinForms message pump while awaiting the stdio reader.
        _ = _client.DisposeAsync().AsTask();
        _lifetime.Dispose();
        base.ExitThreadCore();
    }
}
