using System.Text.Json;

namespace CoQuo;

public sealed record QuotaUsage(double RemainingPercent, DateTimeOffset? ResetAt, int WindowMinutes)
{
    public int RoundedRemaining => (int)Math.Round(RemainingPercent, MidpointRounding.AwayFromZero);
}

public static class UsageParser
{
    // Do not assume primary=5h and secondary=7d: the server may return only one window.
    public const int WeeklyMinutes = 7 * 24 * 60;

    public static bool TryParse(JsonElement json, out QuotaUsage? usage)
    {
        usage = null;
        if (json.ValueKind != JsonValueKind.Object) return false;

        // Response shape: {rateLimitsByLimitId:{codex:{primary,secondary}},rateLimits:{...}}.
        // Notification shape: {rateLimits:{limitId:"codex",primary,secondary}}.
        if (TryGetObject(json, "rateLimitsByLimitId", out var byId) &&
            TryGetObject(byId, "codex", out var canonical) &&
            TryFindWeekly(canonical, out usage)) return true;

        if (TryGetObject(json, "rateLimits", out var limits) &&
            (!limits.TryGetProperty("limitId", out var id) ||
             (id.ValueKind == JsonValueKind.String && id.GetString() == "codex")) &&
            TryFindWeekly(limits, out usage)) return true;

        // Some notifications deliver the bucket directly.
        return (!json.TryGetProperty("limitId", out var directId) ||
                (directId.ValueKind == JsonValueKind.String && directId.GetString() == "codex")) &&
               TryFindWeekly(json, out usage);
    }

    private static bool TryFindWeekly(JsonElement rateLimits, out QuotaUsage? usage)
    {
        usage = null;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!TryGetObject(rateLimits, key, out var window)) continue;
            if (!TryNumber(window, "windowDurationMins", out var duration) ||
                Math.Abs(duration - WeeklyMinutes) > 0.01) continue;
            if (!TryNumber(window, "usedPercent", out var used) ||
                !double.IsFinite(used) || used < 0 || used > 100) continue;

            DateTimeOffset? reset = null;
            if (TryNumber(window, "resetsAt", out var seconds) &&
                seconds >= 0 && seconds <= 253402300799)
            {
                try { reset = DateTimeOffset.FromUnixTimeSeconds((long)seconds); }
                catch (ArgumentOutOfRangeException) { /* unknown reset time */ }
            }

            usage = new QuotaUsage(100 - used, reset, WeeklyMinutes);
            return true;
        }
        return false;
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
