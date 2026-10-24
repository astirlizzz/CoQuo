using System.Globalization;
using System.Text.Json;

namespace CoQuo;

public static class ClaudeUsageParser
{
    // Response shape: {five_hour:{utilization:36.0,resets_at:"2026-10-23T18:00:00+00:00"},seven_day:{...}}.
    public static bool TryParse(JsonElement json, out QuotaReading? reading)
    {
        reading = null;
        if (json.ValueKind != JsonValueKind.Object) return false;
        var fiveHour = ParseWindow(json, "five_hour", UsageParser.FiveHourMinutes);
        var weekly = ParseWindow(json, "seven_day", UsageParser.WeeklyMinutes);
        if (fiveHour is null && weekly is null) return false;
        reading = new QuotaReading(fiveHour, weekly);
        return true;
    }

    private static QuotaUsage? ParseWindow(JsonElement json, string key, int minutes)
    {
        if (!json.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var prop) || prop.ValueKind != JsonValueKind.Number ||
            !prop.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0) return null;

        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resets_at", out var resetProp) && resetProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetProp.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            reset = parsed;

        // Utilization can exceed 100 when extra usage is enabled; the allowance is then exhausted.
        return new QuotaUsage(Math.Max(0, 100 - used), reset, minutes);
    }
}
