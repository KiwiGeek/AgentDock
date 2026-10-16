using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>What <see cref="ClaudeUsageApi.FetchAsync"/> found: the limits, or a sentence saying why not.</summary>
public sealed record ClaudeUsageFetch(PlanUsage? Usage, string? Problem);

/// <summary>
/// Claude plan limits asked for directly: the same call Claude Code's /usage makes,
/// <c>GET /api/oauth/usage</c>, with the access token Claude Code already stored. Off unless the user
/// picks "Ask Claude directly" on the settings page. Undocumented, so it can stop working, and
/// Anthropic's terms describe subscription OAuth as being for Claude Code and claude.ai; the
/// settings page and the README say so. One GET of the user's own limits, nothing wider.
/// <para>
/// The token is never refreshed: a refresh token is single-use, so refreshing it here would race
/// Claude Code and sign the user out. It's never written anywhere either (not state.json, not
/// claude-usage.json, not logs, not the UI), and is held only for the request.
/// </para>
/// </summary>
public static partial class ClaudeUsageApi
{
    // Notes for the drawer. The first sentence only when nothing saved is on screen;
    // with saved numbers the drawer adds "Showing the last saved limits."
    public const string Checking = "Checking your Claude limits…";
    public const string NoWindows = "Your limits appear here after the next reply in Claude.";
    public const string NotSignedIn = "Sign in to Claude Code once, then open this again.";
    public const string SignInExpired = "Open Claude Code once so its sign-in can refresh.";
    public const string RateLimited = "Claude is limiting these checks.";
    public const string Unreachable = "Claude couldn't be reached.";
    public const string NotUnderstood = "Claude's reply wasn't understood.";
    public const string ShowingSaved = "Showing the last saved limits.";

    /// <summary>Claude Code's own minimum between checks, and the wait after a 429 without Retry-After.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    /// <summary>Used when <c>claude --version</c> can't be read.</summary>
    private const string FallbackVersion = "2.1.201";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Lock Gate = new();
    /// <summary>No request before this (after a success, or a 429). In memory only.</summary>
    private static DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private static PlanUsage? _lastSuccess;
    /// <summary><see cref="_notBefore"/> comes from a 429, not a success.</summary>
    private static bool _backingOff;
    private static string? _version;

    public static Task<ClaudeUsageFetch> FetchAsync() => Task.Run(FetchCoreAsync);

    private static async Task<ClaudeUsageFetch> FetchCoreAsync()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            lock (Gate)
            {
                // Too soon: after a 429 say so; after a success, that answer still stands.
                if (now < _notBefore)
                    return _backingOff || _lastSuccess is null
                        ? new ClaudeUsageFetch(null, RateLimited)
                        : new ClaudeUsageFetch(_lastSuccess, null);
            }

            var (token, problem) = ReadToken(now);
            if (token is null)
                return new ClaudeUsageFetch(null, problem);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl()}/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Claude Code's user agent puts this in Claude Code's rate-limit bucket. Any other agent
            // gets a lasting 429, which would make the option useless.
            request.Headers.TryAddWithoutValidation("User-Agent", $"claude-code/{Version()}");
            token = null;

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ClaudeUsageFetch(null, SignInExpired);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? delta : MinInterval;
                lock (Gate)
                {
                    _notBefore = DateTimeOffset.UtcNow + (wait > MaxBackoff ? MaxBackoff : wait);
                    _backingOff = true;
                }
                return new ClaudeUsageFetch(null, RateLimited);
            }
            if (!response.IsSuccessStatusCode)
                return new ClaudeUsageFetch(null, Unreachable);

            var result = Parse(await response.Content.ReadAsStringAsync(), DateTimeOffset.UtcNow);
            if (result.Usage is { } usage)
            {
                lock (Gate)
                {
                    _lastSuccess = usage;
                    _notBefore = DateTimeOffset.UtcNow + MinInterval;
                    _backingOff = false;
                }
                ClaudeStatusLine.Replace(usage);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Network failures, timeouts (TaskCanceledException), an unwritable usage file: never
            // the resident's problem, and never shown as the exception's text.
            return new ClaudeUsageFetch(null, Unreachable);
        }
    }

    /// <summary><c>CLAUDE_CODE_CUSTOM_OAUTH_URL</c> if Claude Code is pointed elsewhere, otherwise Anthropic's API.</summary>
    private static string BaseUrl() =>
        Environment.GetEnvironmentVariable("CLAUDE_CODE_CUSTOM_OAUTH_URL") is { Length: > 0 } custom
            ? custom.TrimEnd('/')
            : "https://api.anthropic.com";

    /// <summary>
    /// The access token: <c>CLAUDE_CODE_OAUTH_TOKEN</c> (no expiry to check), else Claude Code's
    /// .credentials.json. Only <c>accessToken</c> and <c>expiresAt</c> are read.
    /// </summary>
    private static (string? Token, string? Problem) ReadToken(DateTimeOffset now)
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is { } variable && !string.IsNullOrWhiteSpace(variable))
            return (variable.Trim(), null);

        var path = Path.Combine(ClaudeConfig.Directory, ".credentials.json");
        if (!File.Exists(path))
            return (null, NotSignedIn);

        // Claude Code rewrites this file, so share it rather than lock it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (null, NotSignedIn);

        // Usually under "claudeAiOauth"; some files have it on the root object.
        var holder = root.TryGetProperty("claudeAiOauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object
                     && HasToken(oauth)
            ? oauth
            : root;
        if (!HasToken(holder))
            return (null, NotSignedIn);

        if (holder.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number
            && DateTimeOffset.FromUnixTimeMilliseconds((long)expires.GetDouble()) <= now)
            return (null, SignInExpired);

        return (holder.GetProperty("accessToken").GetString(), null);

        static bool HasToken(JsonElement element) =>
            element.TryGetProperty("accessToken", out var token) && token.ValueKind == JsonValueKind.String
                                                                 && !string.IsNullOrWhiteSpace(token.GetString());
    }

    /// <summary>Claude Code's version, from one <c>claude --version</c> per process.</summary>
    private static string Version()
    {
        if (_version is not null)
            return _version;
        _version = FallbackVersion;
        try
        {
            if (PathResolver.Find("claude") is not { } claude)
                return _version;
            var start = new ProcessStartInfo(claude, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start);
            if (process is null)
                return _version;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return _version;
            }
            if (VersionNumber().Match(output.Result) is { Success: true } match)
                _version = match.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the fallback.
        }
        return _version;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();

    /// <summary>
    /// Pure. The session and week windows from a usage reply, or a problem. <c>utilization</c> is
    /// already 0–100. Per-model weeks and <c>extra_usage</c> are ignored. A reply with neither
    /// window is still a success (no limits yet), not an error.
    /// </summary>
    internal static ClaudeUsageFetch Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ClaudeUsageFetch(null, NotUnderstood);

            var session = ReadWindow(root, "five_hour");
            var week = ReadWindow(root, "seven_day");

            // Newer replies also list limits; they only fill a window the named object didn't give.
            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                foreach (var limit in limits.EnumerateArray())
                {
                    if (limit.ValueKind != JsonValueKind.Object || !limit.TryGetProperty("percent", out var percent)
                        || percent.ValueKind != JsonValueKind.Number)
                        continue;
                    var kind = Text(limit, "kind") ?? Text(limit, "group");
                    var window = new UsageWindow { UsedPercentage = percent.GetDouble(), ResetsAt = ClaudeStatusLine.ResetsAt(limit) };
                    if (session is null && kind is "session" or "five_hour")
                        session = window;
                    else if (week is null && kind is "seven_day" or "weekly_all" or "weekly")
                        week = window;
                }

            return new ClaudeUsageFetch(new PlanUsage
            {
                UpdatedAt = now.ToUnixTimeSeconds(),
                FiveHour = session,
                SevenDay = week
            }, null);
        }
        catch (JsonException)
        {
            return new ClaudeUsageFetch(null, NotUnderstood);
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>A named window; null when it's missing, null, or has no number (never an invented 0%).</summary>
    private static UsageWindow? ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        // "used_percentage" is the status line's name for the same number.
        if ((!window.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number)
            && (!window.TryGetProperty("used_percentage", out used) || used.ValueKind != JsonValueKind.Number))
            return null;
        return new UsageWindow { UsedPercentage = used.GetDouble(), ResetsAt = ClaudeStatusLine.ResetsAt(window) };
    }
}
