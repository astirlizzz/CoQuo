namespace CoQuo;

internal sealed class TrayApplicationContext : ApplicationContext
{
    // The usage endpoint has no published polling budget; stay well above the 1-minute option.
    private const int ClaudeMinIntervalSeconds = 120;

    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _refresh;
    private readonly ToolStripMenuItem _showCodex;
    private readonly ToolStripMenuItem _showClaude;
    private readonly ToolStripMenuItem _autostart;
    private readonly Dictionary<int, ToolStripMenuItem> _intervals = new();
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
        _codex = new QuotaIndicator("Codex weekly", "Codex weekly", IconProvider.Codex, ReadCodexAsync,
            // An RPC or broken pipe may poison this subprocess; restart on next poll.
            onFailure: () => _exiting ? Task.CompletedTask : _client.StopAsync(),
            minIntervalSeconds: 0, () => _settings.RefreshSeconds, _menu, _lifetime.Token);
        _claude = new QuotaIndicator("Claude 5-hour", "Claude 5h", IconProvider.Claude, ReadClaudeAsync,
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
        _showCodex = new ToolStripMenuItem("Show Codex icon", null, (_, _) => ToggleIndicator(codex: true));
        _showClaude = new ToolStripMenuItem("Show Claude icon", null, (_, _) => ToggleIndicator(codex: false));
        _autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Settings.IsAutoStartEnabled()
        };
        _autostart.Click += (_, _) => ToggleAutoStart();

        _menu.Items.AddRange(_codex.MenuItems);
        _menu.Items.AddRange(_claude.MenuItems);
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _refresh, intervals, _showCodex, _showClaude, _autostart, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit CoQuo", null, (_, _) => ExitThread())
        });
        _menu.Opening += (_, _) => UpdateUi();

        _codex.Enabled = _settings.ShowCodex;
        _claude.Enabled = _settings.ShowClaude;
        UpdateToggles();

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

    private async Task<QuotaUsage> ReadCodexAsync(CancellationToken ct)
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
            if (!_settings.ShowCodex && !_exiting) await _client.StopAsync();
        }
        if (!UsageParser.TryParse(response, out var usage) || usage is null)
            throw new FormatException("Weekly (7-day) quota missing in the Codex response");
        return usage;
    }

    private static async Task<QuotaUsage> ReadClaudeAsync(CancellationToken ct)
    {
        var response = await ClaudeUsageClient.ReadUsageAsync(ct);
        if (!ClaudeUsageParser.TryParse(response, out var usage) || usage is null)
            throw new FormatException("5-hour quota missing in the Claude response");
        return usage;
    }

    private void OnLimitsUpdated(System.Text.Json.JsonElement parameters)
    {
        if (!UsageParser.TryParse(parameters, out var parsed) || parsed is null) return;
        try
        {
            if (_dispatcher.IsHandleCreated && !_dispatcher.IsDisposed)
                _dispatcher.BeginInvoke((MethodInvoker)(() => { if (!_exiting) _codex.AcceptUsage(parsed); }));
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

    private void ToggleIndicator(bool codex)
    {
        if (codex) _settings.ShowCodex = !_settings.ShowCodex;
        else _settings.ShowClaude = !_settings.ShowClaude;
        _codex.Enabled = _settings.ShowCodex;
        _claude.Enabled = _settings.ShowClaude;
        // Do not keep a codex app-server running when nobody looks at its numbers.
        if (!_settings.ShowCodex && !_codex.IsBusy) _ = _client.StopAsync();
        UpdateToggles();
        SaveSettings();
    }

    private void UpdateToggles()
    {
        _showCodex.Checked = _settings.ShowCodex;
        _showClaude.Checked = _settings.ShowClaude;
        // The last visible icon carries the menu, so it cannot be hidden.
        _showCodex.Enabled = !_settings.ShowCodex || _settings.ShowClaude;
        _showClaude.Enabled = !_settings.ShowClaude || _settings.ShowCodex;
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
