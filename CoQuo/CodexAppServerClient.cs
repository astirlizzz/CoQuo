using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CoQuo;

/// <summary>
/// Owns only its child 'codex app-server' process; uses the supported stdio JSONL protocol.
/// Does not read, copy, persist, or refresh OpenAI credentials.
/// </summary>
internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private Process? _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _pumpCts;
    private Task? _reader;
    private Task? _stderr;
    private long _requestId;
    private bool _disposed;

    public event Action<JsonElement>? RateLimitsUpdated;
    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CodexAppServerClient));
        if (IsRunning) return;
        await StopAsync();
        var codex = CodexLocator.Find() ?? throw new FileNotFoundException(
            "Codex CLI not found in PATH. Install Codex or set COQUO_CODEX_PATH.");

        var info = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        if (codex.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            codex.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // For npm shims, launch through Windows cmd. No untrusted arguments.
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"\"{codex}\" app-server\"";
        }
        else
        {
            info.FileName = codex;
            info.ArgumentList.Add("app-server");
        }

        try
        {
            _process = Process.Start(info) ?? throw new IOException("Failed to start Codex app-server");
            _pumpCts = new CancellationTokenSource();
            _reader = ReadPumpAsync(_process, _pumpCts.Token);
            _stderr = DrainStderrAsync(_process, _pumpCts.Token);
            await RequestAsync("initialize", new
            {
                clientInfo = new { name = "coquo", title = "CoQuo tray quota monitor", version = "0.1.0" }
            }, ct);
            await SendAsync(new { method = "initialized", @params = new { } }, ct);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public Task<JsonElement> ReadRateLimitsAsync(CancellationToken ct) =>
        RequestAsync("account/rateLimits/read", null, ct);

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
    {
        if (!IsRunning) throw new IOException("Codex app-server is not running");
        var id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs)) throw new InvalidOperationException("Duplicate RPC request id");
        try
        {
            if (parameters is null)
                await SendAsync(new { id, method }, ct);
            else
                await SendAsync(new { id, method, @params = parameters }, ct);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object message, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var process = _process ?? throw new IOException("Codex process stopped");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadPumpAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var msg = document.RootElement;
                    if (msg.ValueKind != JsonValueKind.Object) continue;
                    if (msg.TryGetProperty("id", out var prop) &&
                        prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var id))
                    {
                        if (!_pending.TryRemove(id, out var completion)) continue;
                        if (msg.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                        {
                            var detail = err.TryGetProperty("message", out var text) ? text.ToString() : "RPC error";
                            completion.TrySetException(new InvalidOperationException($"Codex: {detail}"));
                        }
                        else if (msg.TryGetProperty("result", out var result))
                            completion.TrySetResult(result.Clone());
                        else completion.TrySetException(new InvalidOperationException("Invalid RPC response"));
                    }
                    else if (msg.TryGetProperty("method", out var m) &&
                             m.ValueKind == JsonValueKind.String &&
                             m.GetString() == "account/rateLimits/updated" &&
                             msg.TryGetProperty("params", out var parameters))
                    {
                        try { RateLimitsUpdated?.Invoke(parameters.Clone()); }
                        catch { /* a broken UI subscriber must not kill the transport */ }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            foreach (var entry in _pending)
                if (_pending.TryRemove(entry.Key, out var waiter))
                    waiter.TrySetException(new IOException("Codex connection closed"));
        }
    }

    private static async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        // Drain without retaining potentially sensitive diagnostic messages.
        try
        {
            while (await process.StandardError.ReadLineAsync(ct) is not null) { }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async Task StopAsync()
    {
        _pumpCts?.Cancel();
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            process.Dispose();
        }
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is TimeoutException or IOException) { }
            _reader = null;
        }
        if (_stderr is not null)
        {
            try { await _stderr.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is TimeoutException or IOException) { }
            _stderr = null;
        }
        _pumpCts?.Dispose();
        _pumpCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _writeGate.Dispose();
    }
}
