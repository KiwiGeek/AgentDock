using System.Diagnostics;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>What <see cref="CodexUsage.FetchAsync"/> found: the limits, or a sentence saying why not.</summary>
public sealed record CodexUsageFetch(PlanUsage? Usage, string? Problem);

/// <summary>
/// Codex plan usage, asked of Codex itself: <c>codex app-server</c> is the local JSON-RPC interface
/// Codex's own TUI and desktop app use (JSON lines over stdin/stdout), and its
/// <c>account/rateLimits/read</c> returns the account's live 5-hour and weekly windows from OpenAI.
/// Codex does its own sign-in and token refresh, so Agent Dock never reads auth.json or holds a
/// token. Live and account-wide: it doesn't depend on when Codex last ran on this PC, and it counts
/// use elsewhere too. The last answer is saved, for when a check fails. Read-only: the server is
/// started with <c>-s read-only -a never</c>, sent one request, and closed.
/// </summary>
public static class CodexUsage
{
    public const string Checking = "Checking your Codex limits…";
    public const string NoWindows = "Your limits appear here after your next Codex reply.";
    public const string NotSignedIn = "Sign in to Codex once (codex login), then open this again.";
    public const string Unreachable = "Codex couldn't check your limits.";
    public const string NotUnderstood = "Codex's reply wasn't understood.";
    public const string ShowingSaved = "Showing the last saved limits.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static string UsagePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock", "codex-usage.json");

    public static Task<CodexUsageFetch> FetchAsync() => Task.Run(FetchCoreAsync);

    private static async Task<CodexUsageFetch> FetchCoreAsync()
    {
        Process? process = null;
        try
        {
            if (PathResolver.Find("codex") is not { } codex)
                return new CodexUsageFetch(null, Unreachable);

            var start = new ProcessStartInfo(codex)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            foreach (var arg in new[] { "-s", "read-only", "-a", "never", "app-server" })
                start.ArgumentList.Add(arg);
            process = Process.Start(start);
            if (process is null)
                return new CodexUsageFetch(null, Unreachable);
            _ = process.StandardError.ReadToEndAsync(); // drain, so a chatty server can't block

            var input = process.StandardInput;
            var output = process.StandardOutput;

            await input.WriteLineAsync("""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"agent-dock","title":"Agent Dock","version":"1.0.0"}}}""");
            await input.FlushAsync();
            if (await ReadResponseAsync(output, 1, StartTimeout) is not { } initialized)
                return new CodexUsageFetch(null, Unreachable);
            if (initialized.Error is not null)
                return new CodexUsageFetch(null, Unreachable);

            await input.WriteLineAsync("""{"method":"initialized"}""");
            // Background check: skip the separate reset-credit lookup.
            await input.WriteLineAsync("""{"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}""");
            await input.FlushAsync();
            if (await ReadResponseAsync(output, 2, RequestTimeout) is not { } reply)
                return new CodexUsageFetch(null, Unreachable);
            if (reply.Error is { } error)
                return new CodexUsageFetch(null, LooksSignedOut(error) ? NotSignedIn : Unreachable);

            var result = Parse(reply.Result!, DateTimeOffset.UtcNow);
            if (result.Usage is { } usage)
                Save(usage);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A missing or broken codex, a closed pipe, an unwritable file: never the resident's
            // problem, and never shown as the exception's text.
            return new CodexUsageFetch(null, Unreachable);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.StandardInput.Close(); // the server exits when its input ends
                    if (!process.WaitForExit(2_000))
                        process.Kill(entireProcessTree: true); // .cmd shim → node → codex
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                {
                    // Already gone.
                }
                process.Dispose();
            }
        }
    }

    private sealed record RpcReply(string? Result, string? Error);

    /// <summary>
    /// Reads lines until the response to <paramref name="id"/>, skipping notifications. Null if the
    /// server stops or takes longer than <paramref name="timeout"/>.
    /// </summary>
    private static async Task<RpcReply?> ReadResponseAsync(StreamReader output, int id, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            while (await output.ReadLineAsync(cancel.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var replyId)
                    || replyId.ValueKind != JsonValueKind.Number || replyId.GetInt32() != id)
                    continue;
                var result = root.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetRawText() : null;
                var error = root.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetRawText() : null;
                // A reply with neither counts as an error.
                return new RpcReply(result, error ?? (result is null ? "{}" : null));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static bool LooksSignedOut(string error) =>
        error.Contains("auth", StringComparison.OrdinalIgnoreCase)
        || error.Contains("login", StringComparison.OrdinalIgnoreCase)
        || error.Contains("log in", StringComparison.OrdinalIgnoreCase)
        || error.Contains("sign in", StringComparison.OrdinalIgnoreCase)
        || error.Contains("401", StringComparison.Ordinal);

    /// <summary>
    /// Pure. The session and week windows from an <c>account/rateLimits/read</c> result: the
    /// <c>codex</c> bucket of <c>rateLimitsByLimitId</c> if there is one, else <c>rateLimits</c>.
    /// Windows are sorted by length rather than primary/secondary: a day or less is the session.
    /// A result with no windows is still a success (no limits yet), not an error.
    /// </summary>
    internal static CodexUsageFetch Parse(string resultJson, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new CodexUsageFetch(null, NotUnderstood);

            var snapshot = root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object
                           && buckets.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object
                ? codex
                : root.TryGetProperty("rateLimits", out var limits) && limits.ValueKind == JsonValueKind.Object
                    ? limits
                    : default;
            if (snapshot.ValueKind != JsonValueKind.Object)
                return new CodexUsageFetch(null, NotUnderstood);

            var usage = new PlanUsage { UpdatedAt = now.ToUnixTimeSeconds() };
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
                    || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number)
                    continue;
                var minutes = window.TryGetProperty("windowDurationMins", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0;
                var resetsAt = window.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number ? (long)r.GetDouble() : 0;

                var parsed = new UsageWindow { UsedPercentage = used.GetDouble(), ResetsAt = resetsAt };
                if (minutes is > 0 and <= 24 * 60 || (minutes == 0 && name == "primary"))
                    usage.FiveHour ??= parsed;
                else
                    usage.SevenDay ??= parsed;
            }
            return new CodexUsageFetch(usage, null);
        }
        catch (JsonException)
        {
            return new CodexUsageFetch(null, NotUnderstood);
        }
    }

    /// <summary>The last answer saved, or null if there's none yet.</summary>
    public static PlanUsage? LoadSaved()
    {
        try
        {
            return File.Exists(UsagePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(UsagePath), DockJsonContext.Default.PlanUsage)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save(PlanUsage usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UsagePath)!);
        var temp = $"{UsagePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, DockJsonContext.Default.PlanUsage));
        File.Move(temp, UsagePath, overwrite: true);
    }
}
