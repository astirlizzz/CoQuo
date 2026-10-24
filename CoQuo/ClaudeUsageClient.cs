using System.Net.Http.Headers;
using System.Text.Json;

namespace CoQuo;

/// <summary>
/// Reads the Claude Code subscription usage with the OAuth token Claude Code already stores.
/// The token is read fresh for every request, kept only in a local, and sent only to api.anthropic.com.
/// Never refreshes the token or touches the refresh token: Claude Code owns the login.
/// </summary>
internal static class ClaudeUsageClient
{
    private static readonly Uri UsageUri = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Http = new() { Timeout = RequestTimeout };

    private static string CredentialsPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir, ".credentials.json");
        }
    }

    public static async Task<JsonElement> ReadUsageAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ReadAccessTokenAsync(ct));
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("CoQuo/0.1.0");
        request.Headers.Accept.ParseAdd("application/json");

        // HttpClient.Timeout stops at the headers; a stalled body (e.g. across sleep) would hang the poll forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Status code only: the body is not echoed into UI error text.
            if ((int)response.StatusCode == 429)
            {
                var retryAfter = response.Headers.RetryAfter is { } header
                    ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                    : null;
                throw new RateLimitedException("Claude usage HTTP 429", retryAfter);
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException((int)response.StatusCode == 401
                    ? "Claude token rejected (401) — open Claude Code"
                    : $"Claude usage HTTP {(int)response.StatusCode}");
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Claude usage request timed out");
        }
    }

    private static async Task<string> ReadAccessTokenAsync(CancellationToken ct)
    {
        string text;
        try { text = await File.ReadAllTextAsync(CredentialsPath, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SourceUnavailableException("Claude Code is not logged in");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(token.GetString()))
                throw new SourceUnavailableException("Claude Code is not logged in with a subscription");
            if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number &&
                expires.TryGetInt64(out var expiresMs) &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresMs)
                throw new SourceUnavailableException("Claude token expired — open Claude Code");
            return token.GetString()!;
        }
        catch (JsonException)
        {
            throw new SourceUnavailableException("Claude credentials file is unreadable");
        }
    }
}
