using System.Globalization;

namespace CoQuo;

/// <summary>Thrown when a reading cannot be attempted locally (no request sent); retried at the normal pace.</summary>
internal sealed class SourceUnavailableException(string message) : Exception(message);

/// <summary>The server answered 429; RetryAfter is its Retry-After header when present.</summary>
internal sealed class RateLimitedException(string message, TimeSpan? retryAfter) : HttpRequestException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// One tray icon plus its menu rows for a single quota source: polling schedule, backoff and stale state.
/// </summary>
internal sealed class QuotaIndicator : IDisposable
{
    private readonly string _name;
    private readonly IconProvider _provider;
    private readonly Func<CancellationToken, Task<QuotaReading>> _fetch;
    private readonly Func<Task>? _onFailure;
    private readonly int _minIntervalSeconds;
    private readonly Func<int> _refreshSeconds;
    private readonly CancellationToken _lifetime;
    private readonly NotifyIcon _tray;
    private readonly QuotaTooltip _tooltip;
    private readonly ToolStripMenuItem _value;
    private readonly ToolStripMenuItem _reset;
    private readonly ToolStripMenuItem _updated;
    private readonly ToolStripSeparator _separator = new();
    private QuotaWindow _window;
    private QuotaReading? _lastReading;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset _nextPollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private int _failures;
    private DateTimeOffset _rateLimitedUntilUtc = DateTimeOffset.MinValue;
    private bool _busy;
    private bool _enabled;
    private bool _disposed;
    private string? _lastError;
    private int? _displayedValue;
    private bool _displayedStale = true;
    private TimeBar? _displayedBar;

    public QuotaIndicator(string name, QuotaWindow window, IconProvider provider,
        Func<CancellationToken, Task<QuotaReading>> fetch, Func<Task>? onFailure,
        int minIntervalSeconds, Func<int> refreshSeconds, ContextMenuStrip menu, CancellationToken lifetime)
    {
        _name = name;
        _window = window;
        _provider = provider;
        _fetch = fetch;
        _onFailure = onFailure;
        _minIntervalSeconds = minIntervalSeconds;
        _refreshSeconds = refreshSeconds;
        _lifetime = lifetime;
        _value = new ToolStripMenuItem($"{Title}: —") { Enabled = false };
        _reset = new ToolStripMenuItem("Resets: —") { Enabled = false };
        _updated = new ToolStripMenuItem("Updated: —") { Enabled = false };
        _tray = new NotifyIcon
        {
            Icon = TrayIconPainter.Create(null, stale: true, provider),
            ContextMenuStrip = menu
        };
        _tooltip = new QuotaTooltip(_tray, _provider, GetTooltipContent);
        _tray.DoubleClick += (_, _) => RequestManualRefresh();
    }

    private string Title => $"{_name} {(_window == QuotaWindow.FiveHour ? "5-hour" : "weekly")}";

    private QuotaUsage? Usage => _lastReading?[_window];

    /// <summary>Which window the icon shows; served from the last reading without a new request.</summary>
    public QuotaWindow Window
    {
        get => _window;
        set
        {
            if (_window == value) return;
            _window = value;
            _displayedValue = int.MinValue; // repaint even if both windows show the same number
            UpdateUi();
        }
    }

    /// <summary>Whether the last reading contained the window; null before the first reading.</summary>
    public bool? Reports(QuotaWindow window) => _lastReading is null ? null : _lastReading[window] is not null;

    /// <summary>Status rows plus a trailing separator, for the shared context menu.</summary>
    public ToolStripItem[] MenuItems => new ToolStripItem[] { _value, _reset, _updated, _separator };

    public bool IsBusy => _busy;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed) return;
            _enabled = value;
            if (!value) _tooltip.Dismiss();
            _tray.Visible = value;
            foreach (var item in MenuItems) item.Visible = value;
            if (value) PollSoonIfOutdated();
            ApplyIconSize();
            UpdateUi();
        }
    }

    // Shown again or resumed: reuse a reading that is still within the interval instead of asking anew.
    private void PollSoonIfOutdated()
    {
        if (_lastSuccessUtc is null || DateTimeOffset.UtcNow - _lastSuccessUtc > TimeSpan.FromSeconds(IntervalSeconds))
            _nextPollUtc = DateTimeOffset.MinValue;
    }

    private bool _paused;

    /// <summary>No scheduled requests while true (locked session); resuming polls at once if the reading is outdated.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_disposed || _paused == value) return;
            _paused = value;
            if (value) _tooltip.Dismiss();
            else PollSoonIfOutdated();
        }
    }

    private int _iconSize = TrayIconSizeClient.StandardSize;
    private int _appliedIconSize = TrayIconSizeClient.StandardSize;

    /// <summary>Displayed tray size in DIP; 16 is the Windows standard and needs no TrayIconSize host.</summary>
    public int IconSize
    {
        get => _iconSize;
        set
        {
            _iconSize = value;
            ApplyIconSize();
        }
    }

    public string? IconSizeError { get; private set; }

    private void ApplyIconSize()
    {
        int wanted = _enabled ? _iconSize : TrayIconSizeClient.StandardSize;
        if (_disposed || wanted == _appliedIconSize || !TrayIconSizeClient.IsAvailable) return;
        _appliedIconSize = wanted;
        // Enlarged icons get the time bar, which needs a wider-than-tall box.
        int width = wanted > TrayIconSizeClient.StandardSize
            ? (int)Math.Round(wanted * TrayIconPainter.TimeBarAspect) : wanted;
        TrayIconSizeClient.Apply(_tray, width, wanted, error =>
        {
            IconSizeError = error;
            UpdateUi();
        });
    }

    // Only a successfully widened icon has room for the bar; a square one would show it squeezed.
    private bool ShowsTimeBar => _appliedIconSize > TrayIconSizeClient.StandardSize && IconSizeError is null;

    // Steps of the drawn level, after easing: more of the repaints fall near the reset.
    private const int TimeBarSteps = 48;

    private TimeBar? CurrentTimeBar(QuotaUsage? usage, DateTimeOffset now)
    {
        if (!ShowsTimeBar) return null;
        // Expand the tail so the last hour is still readable at tray size.
        // Quantize after easing to keep the extra detail near the reset.
        var level = usage?.TimeLeft(now) is { } left
            ? Math.Ceiling(Math.Sqrt(left) * TimeBarSteps) / TimeBarSteps : (double?)null;
        return new TimeBar(level, usage?.Pace(now));
    }

    private int IntervalSeconds => Math.Max(_minIntervalSeconds, _refreshSeconds());

    public void Tick()
    {
        if (_disposed || !_enabled) return;
        UpdateUi();
        if (!_busy && !_paused && DateTimeOffset.UtcNow >= _nextPollUtc)
            _ = PollAsync();
    }

    /// <param name="partial">A notification that may omit windows: keep the earlier values for those.</param>
    public void AcceptReading(QuotaReading reading, bool partial = false)
    {
        if (_disposed || !_enabled) return;
        _lastReading = partial ? reading.Over(_lastReading) : reading;
        _lastSuccessUtc = DateTimeOffset.UtcNow;
        _lastError = null;
        _failures = 0;
        _rateLimitedUntilUtc = DateTimeOffset.MinValue;
        UpdateUi();
    }

    private async Task PollAsync()
    {
        if (_busy || _disposed || !_enabled) return;
        // Manual menu clicks cannot create unbounded bursts of server requests.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAttemptUtc < TimeSpan.FromSeconds(15) || now < _rateLimitedUntilUtc) return;
        _busy = true;
        _lastAttemptUtc = now;
        _nextPollUtc = now.AddSeconds(IntervalSeconds);
        Log.Write(LogLevel.Verbose, $"{_name}: poll");
        try
        {
            AcceptReading(await _fetch(_lifetime));
            if (_lastLoggedError is not null) Log.Write(LogLevel.Info, $"{_name}: recovered");
            _lastLoggedError = null;
            Log.Write(LogLevel.Verbose, $"{_name}: poll ok");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (SourceUnavailableException ex)
        {
            // Nothing reached the server; check again at the normal pace without escalating backoff.
            _lastError = ex.Message;
            LogFailure(LogLevel.Warning, $"{_name}: not attempted: {ex.Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _failures++;
            LogFailure(ex is HttpRequestException or TimeoutException or IOException ? LogLevel.Warning : LogLevel.Error,
                $"{_name}: poll failed ({_failures} in a row): {ex.GetType().Name}: {ex.Message}", ex.Message);
            // Network/server trouble: 30s, 60s, 120s ... capped at 15m.
            // Respect 429 and other overload responses instead of repeatedly retrying:
            // the server's Retry-After (kept within 1 min..1 h) or else 15 minutes.
            var backoff = Math.Min(900, 30 * (int)Math.Pow(2, Math.Min(5, _failures - 1)));
            if (ex is RateLimitedException { RetryAfter: { } retryAfter })
                _rateLimitedUntilUtc = DateTimeOffset.UtcNow +
                    TimeSpan.FromSeconds(Math.Clamp(retryAfter.TotalSeconds, 60, 3600));
            else if (ex is RateLimitedException ||
                     _lastError.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                     _lastError.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
                _rateLimitedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(15);
            _nextPollUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(IntervalSeconds, backoff));
            if (_nextPollUtc < _rateLimitedUntilUtc) _nextPollUtc = _rateLimitedUntilUtc;
            if (!_disposed && _onFailure is not null) await _onFailure();
        }
        finally
        {
            _busy = false;
            UpdateUi();
        }
    }

    private string? _lastLoggedError;

    // A failure that repeats unchanged every few minutes would only grow the log: the first one is logged at its
    // level, repeats at Verbose.
    private void LogFailure(LogLevel level, string message, string reason)
    {
        Log.Write(reason == _lastLoggedError ? LogLevel.Verbose : level, message);
        _lastLoggedError = reason;
    }

    public void RequestManualRefresh()
    {
        if (_disposed || _busy || !_enabled || _paused) return;
        if (DateTimeOffset.UtcNow - _lastAttemptUtc < TimeSpan.FromSeconds(15) ||
            DateTimeOffset.UtcNow < _rateLimitedUntilUtc) return;
        _nextPollUtc = DateTimeOffset.MinValue;
        _ = PollAsync();
    }

    public void OnIntervalChanged()
    {
        // No automatic extra request: choose the new schedule after the most recent attempt.
        _nextPollUtc = _lastAttemptUtc.AddSeconds(IntervalSeconds);
        if (_nextPollUtc < _rateLimitedUntilUtc) _nextPollUtc = _rateLimitedUntilUtc;
        UpdateUi();
    }

    private bool IsStale(QuotaUsage? usage, DateTimeOffset now)
    {
        if (usage is null || _lastSuccessUtc is null || _lastError is not null) return true;
        if (usage.ResetAt is { } reset && now >= reset) return true;
        return now - _lastSuccessUtc >
               TimeSpan.FromSeconds(Math.Max(180, IntervalSeconds * 2 + 30));
    }

    public void UpdateUi()
    {
        if (_disposed || !_enabled) return;
        var now = DateTimeOffset.UtcNow;
        var usage = Usage;
        var value = usage?.RoundedRemaining;
        var stale = IsStale(usage, now);
        var bar = CurrentTimeBar(usage, now);
        if (value != _displayedValue || stale != _displayedStale || bar != _displayedBar)
        {
            var icon = TrayIconPainter.Create(value, stale, _provider, bar);
            var old = _tray.Icon;
            _tray.Icon = icon;
            old?.Dispose();
            _displayedValue = value;
            _displayedStale = stale;
            _displayedBar = bar;
        }
        // A reading without this window means the account has no such limit (e.g. Codex without 5-hour).
        var missing = usage is null && _lastReading is not null ? "not reported" : "unavailable";
        _value.Text = usage is null ? $"{Title}: {missing}" :
            $"{Title}: {usage.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture)}% left" +
            (stale ? " (STALE)" : "");
        var timeLeft = usage?.TimeLeft(now);
        var windowLeft = timeLeft is { } share ? $", {share * 100:0}% of window left" : "";
        _reset.Text = usage?.ResetAt is { } reset ?
            $"Resets: {reset.ToLocalTime():ddd, dd MMM HH:mm}{Until(reset)}{windowLeft}" :
            timeLeft == 1 ? "Resets: window starts with the next use" : "Resets: unknown";
        var retry = RetryText();
        _updated.Text = _lastError is not null ? $"Error: {Shorten(_lastError, 80)}{retry}" :
            _lastSuccessUtc is { } updated ? $"Updated: {updated.ToLocalTime():HH:mm:ss}" : "Updated: never";
        _tooltip.RefreshContent();
    }

    private QuotaTooltipContent GetTooltipContent()
    {
        var now = DateTimeOffset.UtcNow;
        var fiveHour = Row(_lastReading?.FiveHour);
        var weekly = Row(_lastReading?.Weekly);
        var status = _lastError is not null ? $"{_name}: {_lastError}{RetryText()}"
            : fiveHour is null && weekly is null ? $"{_name}: quota unavailable" : null;
        int iconSize = ShowsTimeBar ? _appliedIconSize : TrayIconSizeClient.StandardSize;
        return new QuotaTooltipContent(fiveHour, weekly, status, iconSize);

        QuotaTooltipRow? Row(QuotaUsage? usage)
        {
            if (usage is null) return null;
            var countdown = usage.ResetCountdown(now) ?? (usage.TimeLeft(now) == 1 ? "Next use" : "—");
            return new QuotaTooltipRow(usage.RoundedRemaining, IsStale(usage, now), CurrentTimeBar(usage, now), countdown);
        }
    }

    private string RetryText()
    {
        if (_lastError is null || _busy) return "";
        var next = _nextPollUtc > _rateLimitedUntilUtc ? _nextPollUtc : _rateLimitedUntilUtc;
        return next > DateTimeOffset.UtcNow ? $" (retry {next.ToLocalTime():HH:mm})" : "";
    }

    private static string Until(DateTimeOffset reset)
    {
        var left = reset - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "";
        return left.TotalHours >= 24 ? $" (in {(int)left.TotalDays}d {left.Hours}h)"
             : left.TotalMinutes >= 60 ? $" (in {(int)left.TotalHours}h {left.Minutes}m)"
             : $" (in {Math.Max(1, (int)left.TotalMinutes)}m)";
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tooltip.Dispose();
        _tray.Visible = false;
        var old = _tray.Icon;
        _tray.Dispose();
        old?.Dispose();
    }
}
