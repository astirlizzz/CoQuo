using System.Globalization;

namespace CoQuo;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _week;
    private readonly ToolStripMenuItem _reset;
    private readonly ToolStripMenuItem _updated;
    private readonly ToolStripMenuItem _refresh;
    private readonly ToolStripMenuItem _autostart;
    private readonly Dictionary<int, ToolStripMenuItem> _intervals = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _dispatcher;
    private readonly Settings _settings;
    private readonly CodexAppServerClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private WeeklyUsage? _lastUsage;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset _nextPollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private int _failures;
    private DateTimeOffset _rateLimitedUntilUtc = DateTimeOffset.MinValue;
    private bool _busy;
    private bool _exiting;
    private string? _lastError;

    public TrayApplicationContext()
    {
        _settings = Settings.Load();
        _dispatcher = new Control();
        _ = _dispatcher.Handle; // invoke target even though the app has no visible window

        _menu = new ContextMenuStrip();
        _week = new ToolStripMenuItem("Weekly: —") { Enabled = false };
        _reset = new ToolStripMenuItem("Resets: —") { Enabled = false };
        _updated = new ToolStripMenuItem("Updated: —") { Enabled = false };
        _refresh = new ToolStripMenuItem("Refresh now", null, (_, _) => RequestManualRefresh());
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
        _autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Settings.IsAutoStartEnabled()
        };
        _autostart.Click += (_, _) => ToggleAutoStart();

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _week, _reset, _updated, new ToolStripSeparator(),
            _refresh, intervals, _autostart, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit CoQuo", null, (_, _) => ExitThread())
        });
        _menu.Opening += (_, _) => UpdateUi();
        _tray = new NotifyIcon
        {
            Icon = TrayIconPainter.Create(null, stale: true),
            Text = "CoQuo — starting",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => RequestManualRefresh();

        _client.RateLimitsUpdated += OnLimitsUpdated;
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            if (_exiting) return;
            UpdateUi();
            if (!_busy && DateTimeOffset.UtcNow >= _nextPollUtc)
                _ = PollAsync();
        };
        _timer.Start(); // First poll starts on the message-loop thread, with WinForms SynchronizationContext.
    }

    private void OnLimitsUpdated(System.Text.Json.JsonElement parameters)
    {
        if (!UsageParser.TryParse(parameters, out var parsed) || parsed is null) return;
        try
        {
            if (_dispatcher.IsHandleCreated && !_dispatcher.IsDisposed)
                _dispatcher.BeginInvoke((MethodInvoker)(() => AcceptUsage(parsed)));
        }
        catch (ObjectDisposedException) { /* shutdown */ }
        catch (InvalidOperationException) { /* shutdown */ }
    }

    private void AcceptUsage(WeeklyUsage usage)
    {
        if (_exiting) return;
        _lastUsage = usage;
        _lastSuccessUtc = DateTimeOffset.UtcNow;
        _lastError = null;
        _failures = 0;
        _rateLimitedUntilUtc = DateTimeOffset.MinValue;
        UpdateUi();
    }

    private async Task PollAsync()
    {
        if (_busy || _exiting) return;
        // Manual menu clicks cannot create unbounded bursts of server requests.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAttemptUtc < TimeSpan.FromSeconds(15) || now < _rateLimitedUntilUtc) return;
        _busy = true;
        _refresh.Enabled = false;
        _lastAttemptUtc = now;
        _nextPollUtc = now.AddSeconds(_settings.RefreshSeconds);
        try
        {
            if (!_client.IsRunning) await _client.StartAsync(_lifetime.Token);
            var response = await _client.ReadRateLimitsAsync(_lifetime.Token);
            if (!UsageParser.TryParse(response, out var usage) || usage is null)
                throw new FormatException("Weekly (7-day) quota missing in the Codex response");
            AcceptUsage(usage);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _failures++;
            // Network/server trouble: 30s, 60s, 120s ... capped at 15m.
            // Respect 429 and other overload responses instead of repeatedly retrying.
            var backoff = Math.Min(900, 30 * (int)Math.Pow(2, Math.Min(5, _failures - 1)));
            if (_lastError.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                _lastError.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
                _rateLimitedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(15);
            _nextPollUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(_settings.RefreshSeconds, backoff));
            if (_nextPollUtc < _rateLimitedUntilUtc) _nextPollUtc = _rateLimitedUntilUtc;
            if (!_exiting)
            {
                // An RPC or broken pipe may poison this subprocess; restart on next poll.
                await _client.StopAsync();
            }
        }
        finally
        {
            _busy = false;
            if (!_exiting) { _refresh.Enabled = true; UpdateUi(); }
        }
    }

    private void RequestManualRefresh()
    {
        if (_exiting || _busy) return;
        if (DateTimeOffset.UtcNow - _lastAttemptUtc < TimeSpan.FromSeconds(15) ||
            DateTimeOffset.UtcNow < _rateLimitedUntilUtc) return;
        _nextPollUtc = DateTimeOffset.MinValue;
        _ = PollAsync();
    }

    private void ChangeInterval(int seconds)
    {
        _settings.RefreshSeconds = seconds;
        foreach (var item in _intervals) item.Value.Checked = item.Key == seconds;
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "CoQuo — settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // No automatic extra request: choose the new schedule after the most recent attempt.
        _nextPollUtc = _lastAttemptUtc.AddSeconds(seconds);
        if (_nextPollUtc < _rateLimitedUntilUtc) _nextPollUtc = _rateLimitedUntilUtc;
        UpdateUi();
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

    private bool IsStale()
    {
        if (_lastUsage is null || _lastSuccessUtc is null || _lastError is not null) return true;
        if (_lastUsage.ResetAt is { } reset && DateTimeOffset.UtcNow >= reset) return true;
        return DateTimeOffset.UtcNow - _lastSuccessUtc >
               TimeSpan.FromSeconds(Math.Max(180, _settings.RefreshSeconds * 2 + 30));
    }

    private int? _displayedValue;
    private bool _displayedStale = true;
    private void UpdateUi()
    {
        if (_exiting) return;
        var value = _lastUsage?.RoundedRemaining;
        var stale = IsStale();
        if (value != _displayedValue || stale != _displayedStale)
        {
            var icon = TrayIconPainter.Create(value, stale);
            var old = _tray.Icon;
            _tray.Icon = icon;
            old?.Dispose();
            _displayedValue = value;
            _displayedStale = stale;
        }
        _week.Text = _lastUsage is null ? "Weekly: unavailable" :
            $"Weekly: {_lastUsage.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture)}% left" +
            (stale ? " (STALE)" : "");
        _reset.Text = _lastUsage?.ResetAt is { } reset ?
            $"Resets: {reset.ToLocalTime():ddd, dd MMM HH:mm}" : "Resets: unknown";
        _updated.Text = _lastError is not null ? $"Error: {Shorten(_lastError, 80)}" :
            _lastSuccessUtc is { } updated ? $"Updated: {updated.ToLocalTime():HH:mm:ss}" : "Updated: never";
        _tray.Text = Shorten(_lastUsage is null ? "CoQuo: weekly unavailable" :
            $"CoQuo: {value}% weekly left" + (stale ? " (stale)" : ""), 63);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _timer.Stop();
        _lifetime.Cancel();
        _tray.Visible = false;
        var old = _tray.Icon;
        _tray.Dispose();
        old?.Dispose();
        _menu.Dispose();
        _timer.Dispose();
        _dispatcher.Dispose();
        // Must not block the WinForms message pump while awaiting the stdio reader.
        _ = _client.DisposeAsync().AsTask();
        _lifetime.Dispose();
        base.ExitThreadCore();
    }
}
