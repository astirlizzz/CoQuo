using System.Text.Json;
using CoQuo;

// Expected remaining percent per window (null = window absent); null for both = parse must fail.
// ResetValid refers to the weekly window when present, otherwise the 5-hour one.
var codexTests = new (string Name, string Json, double? FiveHour, double? Weekly, bool ResetValid)[]
{
    ("5h primary + weekly secondary", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":80,"windowDurationMins":300},"secondary":{"usedPercent":27,"windowDurationMins":10080,"resetsAt":1780000000}}}""", 20, 73, true),
    ("weekly only, in primary", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":1.5,"windowDurationMins":10080}}}""", null, 98.5, false),
    ("multi-bucket picks codex", """{"rateLimitsByLimitId":{"codex_other":{"primary":{"usedPercent":99,"windowDurationMins":10080}},"codex":{"primary":{"usedPercent":4,"windowDurationMins":10080}}}}""", null, 96, false),
    ("notification", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":10080}}}""", null, 75, false),
    ("direct notification", """{"limitId":"codex","secondary":{"usedPercent":20,"windowDurationMins":10080}}""", null, 80, false),
    ("5h only", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1780000000}}}""", 90, null, true),
    ("unknown duration ignored", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":60}}}""", null, null, false),
    ("wrong bucket", """{"rateLimits":{"limitId":"codex_other","primary":{"usedPercent":10,"windowDurationMins":10080}}}""", null, null, false),
    ("malformed percent", """{"rateLimits":{"primary":{"usedPercent":120,"windowDurationMins":10080}}}""", null, null, false),
    ("empty", """{}""", null, null, false),
};

var claudeTests = new (string Name, string Json, double? FiveHour, double? Weekly, bool ResetValid)[]
{
    ("both windows", """{"five_hour":{"utilization":36.0,"resets_at":"2026-10-23T18:00:00.123456+00:00"},"seven_day":{"utilization":80,"resets_at":"2026-10-28T10:00:00+00:00"}}""", 64, 20, true),
    ("5h, no reset yet", """{"five_hour":{"utilization":0,"resets_at":null}}""", 100, null, false),
    ("over limit", """{"five_hour":{"utilization":104.5,"resets_at":"2026-10-23T18:00:00Z"}}""", 0, null, true),
    ("weekly only", """{"five_hour":null,"seven_day":{"utilization":10}}""", null, 90, false),
    ("negative", """{"five_hour":{"utilization":-1}}""", null, null, false),
    ("string utilization", """{"five_hour":{"utilization":"36"}}""", null, null, false),
    ("empty", """{}""", null, null, false),
};

int failures = 0, total = 0;
Run("codex", codexTests, UsageParser.TryParse);
Run("claude", claudeTests, ClaudeUsageParser.TryParse);

// A partial notification keeps windows it does not mention.
var earlier = new QuotaReading(new QuotaUsage(50, null, 300), new QuotaUsage(70, null, 10080));
var merged = new QuotaReading(null, new QuotaUsage(65, null, 10080)).Over(earlier);
Check("merge keeps missing window", merged.FiveHour?.RemainingPercent == 50 && merged.Weekly?.RemainingPercent == 65);

// Time bar: share of the window ahead and the pace against it.
var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
QuotaUsage FiveHour(double remaining, double hoursToReset) => new(remaining, now.AddHours(hoursToReset), 300);
Check("time left mid-window", Math.Abs(FiveHour(50, 2.5).TimeLeft(now)!.Value - 0.5) < 1e-9);
Check("time left after reset", FiveHour(50, -1).TimeLeft(now) == 0);
Check("time left before window starts", new QuotaUsage(100, null, 300).TimeLeft(now) == 1);
Check("time left unknown", new QuotaUsage(64, null, 300).TimeLeft(now) is null && new QuotaUsage(64, null, 300).Pace(now) is null);
Check("pace fresh window on track", FiveHour(95, 5).Pace(now) == QuotaPace.OnTrack);
Check("pace ahead on track", FiveHour(80, 1).Pace(now) == QuotaPace.OnTrack);
Check("pace behind at margin", FiveHour(40, 2.5).Pace(now) == QuotaPace.OnTrack && FiveHour(39, 2.5).Pace(now) == QuotaPace.Behind);
Check("pace short", FiveHour(25, 2.5).Pace(now) == QuotaPace.Behind && FiveHour(24, 2.5).Pace(now) == QuotaPace.Short);

Console.WriteLine($"{total - failures}/{total} tests passed");
return failures == 0 ? 0 : 1;

void Run(string suite, (string Name, string Json, double? FiveHour, double? Weekly, bool ResetValid)[] tests, Parser parse)
{
    foreach (var test in tests)
    {
        using var doc = JsonDocument.Parse(test.Json);
        var found = parse(doc.RootElement, out var reading);
        bool expectNone = test.FiveHour is null && test.Weekly is null;
        bool ok = expectNone
            ? !found && reading is null
            : found && reading is not null &&
              Matches(reading.FiveHour, test.FiveHour) && Matches(reading.Weekly, test.Weekly) &&
              ((reading.Weekly ?? reading.FiveHour)!.ResetAt is not null) == test.ResetValid;
        Check($"{suite}: {test.Name}", ok);
    }
}

static bool Matches(QuotaUsage? actual, double? expected) =>
    expected is null ? actual is null : actual is not null && Math.Abs(actual.RemainingPercent - expected.Value) < 0.0001;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    total++;
    if (!ok) failures++;
}

delegate bool Parser(JsonElement json, out QuotaReading? reading);
