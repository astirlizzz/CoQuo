using System.Text.Json;

namespace CoQuo;

public sealed record QuotaUsage(double RemainingPercent, DateTimeOffset? ResetAt, int WindowMinutes)
{
    // Allowance may lag the clock by this many points before the pace turns amber / red,
    // so the first request of a fresh window does not alarm.
    public const double BehindMargin = 10;
    public const double ShortMargin = 25;

    public int RoundedRemaining => (int)Math.Round(RemainingPercent, MidpointRounding.AwayFromZero);

    /// <summary>Share of the window still ahead (1 = just started); null when unknown.</summary>
    public double? TimeLeft(DateTimeOffset now)
    {
        // Without a reset time an untouched allowance means the window starts with the next request.
        if (ResetAt is not { } reset) return RoundedRemaining == 100 ? 1 : null;
        if (WindowMinutes <= 0) return null;
        return Math.Clamp((reset - now).TotalMinutes / WindowMinutes, 0, 1);
    }

    /// <summary>Whether the allowance outlasts the window at the current rate of use.</summary>
    public QuotaPace? Pace(DateTimeOffset now)
    {
        if (TimeLeft(now) is not { } left) return null;
        double lag = left * 100 - RemainingPercent;
        return lag <= BehindMargin ? QuotaPace.OnTrack : lag <= ShortMargin ? QuotaPace.Behind : QuotaPace.Short;
    }
}

public enum QuotaPace { OnTrack, Behind, Short }

public enum QuotaWindow { FiveHour, Weekly }

/// <summary>Every window one response reported; switching the displayed window needs no new request.</summary>
public sealed record QuotaReading(QuotaUsage? FiveHour, QuotaUsage? Weekly)
{
    public QuotaUsage? this[QuotaWindow window] => window == QuotaWindow.FiveHour ? FiveHour : Weekly;

    public bool IsEmpty => FiveHour is null && Weekly is null;

    /// <summary>For partial updates: windows missing here keep their earlier value.</summary>
    public QuotaReading Over(QuotaReading? earlier) =>
        earlier is null ? this : new QuotaReading(FiveHour ?? earlier.FiveHour, Weekly ?? earlier.Weekly);
}

public static class UsageParser
{
    // Do not assume primary=5h and secondary=7d: the server may return only one window.
    public const int FiveHourMinutes = 5 * 60;
    public const int WeeklyMinutes = 7 * 24 * 60;

    public static bool TryParse(JsonElement json, out QuotaReading? reading)
    {
        reading = null;
        if (json.ValueKind != JsonValueKind.Object) return false;

        // Response shape: {rateLimitsByLimitId:{codex:{primary,secondary}},rateLimits:{...}}.
        // Notification shape: {rateLimits:{limitId:"codex",primary,secondary}}.
        if (TryGetObject(json, "rateLimitsByLimitId", out var byId) &&
            TryGetObject(byId, "codex", out var canonical) &&
            TryFindWindows(canonical, out reading)) return true;

        if (TryGetObject(json, "rateLimits", out var limits) &&
            (!limits.TryGetProperty("limitId", out var id) ||
             (id.ValueKind == JsonValueKind.String && id.GetString() == "codex")) &&
            TryFindWindows(limits, out reading)) return true;

        // Some notifications deliver the bucket directly.
        return (!json.TryGetProperty("limitId", out var directId) ||
                (directId.ValueKind == JsonValueKind.String && directId.GetString() == "codex")) &&
               TryFindWindows(json, out reading);
    }

    // Windows are recognized by duration, whichever of primary/secondary carries them.
    private static bool TryFindWindows(JsonElement rateLimits, out QuotaReading? reading)
    {
        QuotaUsage? fiveHour = null, weekly = null;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!TryGetObject(rateLimits, key, out var window) ||
                !TryNumber(window, "windowDurationMins", out var duration)) continue;
            bool isFiveHour = Math.Abs(duration - FiveHourMinutes) <= 0.01;
            bool isWeekly = Math.Abs(duration - WeeklyMinutes) <= 0.01;
            if (!isFiveHour && !isWeekly) continue;
            if (!TryNumber(window, "usedPercent", out var used) ||
                !double.IsFinite(used) || used < 0 || used > 100) continue;

            DateTimeOffset? reset = null;
            if (TryNumber(window, "resetsAt", out var seconds) &&
                seconds >= 0 && seconds <= 253402300799)
            {
                try { reset = DateTimeOffset.FromUnixTimeSeconds((long)seconds); }
                catch (ArgumentOutOfRangeException) { /* unknown reset time */ }
            }

            var usage = new QuotaUsage(100 - used, reset, isWeekly ? WeeklyMinutes : FiveHourMinutes);
            if (isWeekly) weekly ??= usage;
            else fiveHour ??= usage;
        }
        reading = fiveHour is null && weekly is null ? null : new QuotaReading(fiveHour, weekly);
        return reading is not null;
    }

    private static bool TryGetObject(JsonElement parent, string key, out JsonElement child)
    {
        child = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(key, out child) && child.ValueKind == JsonValueKind.Object;
    }

    private static bool TryNumber(JsonElement parent, string key, out double number)
    {
        number = 0;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(key, out var prop) &&
               prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out number);
    }
}
