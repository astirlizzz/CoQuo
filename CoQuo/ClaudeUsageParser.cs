using System.Globalization;
using System.Text.Json;

namespace CoQuo;

public static class ClaudeUsageParser
{
    public const int FiveHourMinutes = 5 * 60;

    // Response shape: {five_hour:{utilization:36.0,resets_at:"2026-10-23T18:00:00+00:00"},seven_day:{...}}.
    public static bool TryParse(JsonElement json, out QuotaUsage? usage)
    {
        usage = null;
        if (json.ValueKind != JsonValueKind.Object ||
            !json.TryGetProperty("five_hour", out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var prop) || prop.ValueKind != JsonValueKind.Number ||
            !prop.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0) return false;

        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resets_at", out var resetProp) && resetProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetProp.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            reset = parsed;

        // Utilization can exceed 100 when extra usage is enabled; the 5-hour allowance is then exhausted.
        usage = new QuotaUsage(Math.Max(0, 100 - used), reset, FiveHourMinutes);
        return true;
    }
}
