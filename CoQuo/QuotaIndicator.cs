using System.Globalization;

namespace CoQuo;

/// <summary>Thrown when a reading cannot be attempted locally (no request sent); retried at the normal pace.</summary>
internal sealed class SourceUnavailableException(string message) : Exception(message);

/// <summary>
/// One tray icon plus its menu rows for a single quota source: polling schedule, backoff and stale state.
/// </summary>
internal sealed class QuotaIndicator : IDisposable
{
    private readonly string _title;
    private readonly string _shortTitle;
    private readonly IconProvider _provider;
    private readonly Func<CancellationToken, Task<QuotaUsage>> _fetch;
    private readonly Func<Task>? _onFailure;
    private readonly int _minIntervalSeconds;
    private readonly Func<int> _refreshSeconds;
    private readonly CancellationToken _lifetime;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _value;
    private readonly ToolStripMenuItem _reset;
    private readonly ToolStripMenuItem _updated;
    private readonly ToolStripSeparator _separator = new();
    private QuotaUsage? _lastUsage;
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

    public QuotaIndicator(string title, string shortTitle, IconProvider provider,
        Func<CancellationToken, Task<QuotaUsage>> fetch, Func<Task>? onFailure,
        int minIntervalSeconds, Func<int> refreshSeconds, ContextMenuStrip menu, CancellationToken lifetime)
    {
        _title = title;
        _shortTitle = shortTitle;
        _provider = provider;
        _fetch = fetch;
        _onFailure = onFailure;
        _minIntervalSeconds = minIntervalSeconds;
        _refreshSeconds = refreshSeconds;
        _lifetime = lifetime;
        _value = new ToolStripMenuItem($"{title}: —") { Enabled = false };
        _reset = new ToolStripMenuItem("Resets: —") { Enabled = false };
        _updated = new ToolStripMenuItem("Updated: —") { Enabled = false };
        _tray = new NotifyIcon
        {
            Icon = TrayIconPainter.Create(null, stale: true, provider),
            Text = $"CoQuo {shortTitle} — starting",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => RequestManualRefresh();
    }

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
            _tray.Visible = value;
            foreach (var item in MenuItems) item.Visible = value;
            if (value) _nextPollUtc = DateTimeOffset.MinValue;
            ApplyIconSize();
            UpdateUi();
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
        TrayIconSizeClient.Apply(_tray, wanted, error => IconSizeError = error);
    }

    private int IntervalSeconds => Math.Max(_minIntervalSeconds, _refreshSeconds());

    public void Tick()
    {
        if (_disposed || !_enabled) return;
        UpdateUi();
        if (!_busy && DateTimeOffset.UtcNow >= _nextPollUtc)
            _ = PollAsync();
    }

    public void AcceptUsage(QuotaUsage usage)
    {
        if (_disposed || !_enabled) return;
        _lastUsage = usage;
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
        try
        {
            AcceptUsage(await _fetch(_lifetime));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (SourceUnavailableException ex)
        {
            // Nothing reached the server; check again at the normal pace without escalating backoff.
            _lastError = ex.Message;
        }
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

    public void RequestManualRefresh()
    {
        if (_disposed || _busy || !_enabled) return;
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

    private bool IsStale()
    {
        if (_lastUsage is null || _lastSuccessUtc is null || _lastError is not null) return true;
        if (_lastUsage.ResetAt is { } reset && DateTimeOffset.UtcNow >= reset) return true;
        return DateTimeOffset.UtcNow - _lastSuccessUtc >
               TimeSpan.FromSeconds(Math.Max(180, IntervalSeconds * 2 + 30));
    }

    public void UpdateUi()
    {
        if (_disposed || !_enabled) return;
        var value = _lastUsage?.RoundedRemaining;
        var stale = IsStale();
        if (value != _displayedValue || stale != _displayedStale)
        {
            var icon = TrayIconPainter.Create(value, stale, _provider);
            var old = _tray.Icon;
            _tray.Icon = icon;
            old?.Dispose();
            _displayedValue = value;
            _displayedStale = stale;
        }
        _value.Text = _lastUsage is null ? $"{_title}: unavailable" :
            $"{_title}: {_lastUsage.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture)}% left" +
            (stale ? " (STALE)" : "");
        _reset.Text = _lastUsage?.ResetAt is { } reset ?
            $"Resets: {reset.ToLocalTime():ddd, dd MMM HH:mm}{Until(reset)}" : "Resets: unknown";
        _updated.Text = _lastError is not null ? $"Error: {Shorten(_lastError, 80)}" :
            _lastSuccessUtc is { } updated ? $"Updated: {updated.ToLocalTime():HH:mm:ss}" : "Updated: never";
        _tray.Text = Shorten(_lastUsage is null ? $"CoQuo: {_shortTitle} unavailable" :
            $"CoQuo: {value}% {_shortTitle} left" + (stale ? " (stale)" : ""), 63);
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
        _tray.Visible = false;
        var old = _tray.Icon;
        _tray.Dispose();
        old?.Dispose();
    }
}
