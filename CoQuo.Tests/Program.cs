using System.Text.Json;
using CoQuo;

var codexTests = new (string Name, string Json, double? Expected, bool ResetValid)[]
{
    ("secondary weekly", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":80,"windowDurationMins":300},"secondary":{"usedPercent":27,"windowDurationMins":10080,"resetsAt":1780000000}}}""", 73, true),
    ("primary weekly", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":1.5,"windowDurationMins":10080}}}""", 98.5, false),
    ("multi-bucket picks codex", """{"rateLimitsByLimitId":{"codex_other":{"primary":{"usedPercent":99,"windowDurationMins":10080}},"codex":{"primary":{"usedPercent":4,"windowDurationMins":10080}}}}""", 96, false),
    ("notification", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":10080}}}""", 75, false),
    ("direct notification", """{"limitId":"codex","secondary":{"usedPercent":20,"windowDurationMins":10080}}""", 80, false),
    ("only 5h", """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300}}}""", null, false),
    ("wrong bucket", """{"rateLimits":{"limitId":"codex_other","primary":{"usedPercent":10,"windowDurationMins":10080}}}""", null, false),
    ("malformed percent", """{"rateLimits":{"primary":{"usedPercent":120,"windowDurationMins":10080}}}""", null, false),
    ("empty", """{}""", null, false),
};

var claudeTests = new (string Name, string Json, double? Expected, bool ResetValid)[]
{
    ("five hour", """{"five_hour":{"utilization":36.0,"resets_at":"2026-10-23T18:00:00.123456+00:00"},"seven_day":{"utilization":80,"resets_at":null}}""", 64, true),
    ("no reset yet", """{"five_hour":{"utilization":0,"resets_at":null}}""", 100, false),
    ("over limit", """{"five_hour":{"utilization":104.5,"resets_at":"2026-10-23T18:00:00Z"}}""", 0, true),
    ("null window", """{"five_hour":null,"seven_day":{"utilization":10}}""", null, false),
    ("negative", """{"five_hour":{"utilization":-1}}""", null, false),
    ("string utilization", """{"five_hour":{"utilization":"36"}}""", null, false),
    ("empty", """{}""", null, false),
};

int failures = 0, total = 0;
Run("codex", codexTests, UsageParser.TryParse);
Run("claude", claudeTests, ClaudeUsageParser.TryParse);
Console.WriteLine($"{total - failures}/{total} tests passed");
return failures == 0 ? 0 : 1;

void Run(string suite, (string Name, string Json, double? Expected, bool ResetValid)[] tests, Parser parse)
{
    foreach (var test in tests)
    {
        using var doc = JsonDocument.Parse(test.Json);
        var found = parse(doc.RootElement, out var value);
        bool ok = test.Expected is null
            ? !found && value is null
            : found && value is not null &&
              Math.Abs(value.RemainingPercent - test.Expected.Value) < 0.0001 &&
              (value.ResetAt is not null) == test.ResetValid;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {suite}: {test.Name}");
        total++;
        if (!ok) failures++;
    }
}

delegate bool Parser(JsonElement json, out QuotaUsage? usage);
