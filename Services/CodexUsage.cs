using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Codex plan usage. Codex records it itself: each session log (<c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>)
/// has <c>token_count</c> events whose <c>payload.rate_limits</c> holds a <c>primary</c> (5-hour) and
/// <c>secondary</c> (weekly) window, each with <c>used_percent</c>, <c>window_minutes</c> and
/// <c>resets_at</c>. That format isn't documented, so anything unexpected just means "no data".
/// Only these logs are read, never Codex's auth.json.
/// </summary>
public static class CodexUsage
{
    /// <summary>How much of the end of a log to search; the latest event is near the end.</summary>
    private const int TailBytes = 512 * 1024;

    /// <summary>Codex's data folder. CODEX_HOME moves it, the same as Codex itself.</summary>
    private static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>The usage from the most recent session that recorded any, or null.</summary>
    public static PlanUsage? Load()
    {
        try
        {
            // Newest logs first; a session that never got a reply has no rate_limits, so try a few.
            foreach (var log in RecentLogs().Take(10))
                if (FromLog(log) is { } usage)
                    return usage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: no data.
        }
        return null;
    }

    /// <summary>Session logs from the newest day folders, newest first, without walking the whole history.</summary>
    private static IEnumerable<FileInfo> RecentLogs()
    {
        var sessions = new DirectoryInfo(Path.Combine(CodexHome, "sessions"));
        if (!sessions.Exists)
            return [];

        var days = Newest(sessions).SelectMany(Newest).SelectMany(Newest).Take(3); // year / month / day
        return days.SelectMany(d => d.EnumerateFiles("rollout-*.jsonl")).OrderByDescending(f => f.LastWriteTimeUtc).ToList();

        static IEnumerable<DirectoryInfo> Newest(DirectoryInfo dir) =>
            dir.EnumerateDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal);
    }

    private static PlanUsage? FromLog(FileInfo log)
    {
        string tail;
        using (var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            tail = reader.ReadToEnd();
            if (start > 0 && tail.IndexOf('\n') is var firstBreak and >= 0)
                tail = tail[(firstBreak + 1)..]; // drop the partial first line
        }

        var lines = tail.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal))
                continue;
            if (Parse(lines[i], log.LastWriteTimeUtc) is { } usage)
                return usage;
        }
        return null;
    }

    private static PlanUsage? Parse(string line, DateTime fallbackTime)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
                return null;

            var updated = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : new DateTimeOffset(fallbackTime, TimeSpan.Zero);

            var usage = new PlanUsage { UpdatedAt = updated.ToUnixTimeSeconds() };
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
                    || !window.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number)
                    continue;

                var resetsAt = window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number
                    ? (long)r.GetDouble()
                    // Older Codex versions gave a countdown instead of a time.
                    : window.TryGetProperty("resets_in_seconds", out var inSeconds) && inSeconds.ValueKind == JsonValueKind.Number
                        ? updated.ToUnixTimeSeconds() + (long)inSeconds.GetDouble()
                        : 0;
                var minutes = window.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0;

                var parsedWindow = new UsageWindow { UsedPercentage = used.GetDouble(), ResetsAt = resetsAt };
                // Sort by length rather than trusting primary/secondary: a day or less is the session window.
                if (minutes is > 0 and <= 24 * 60 || (minutes == 0 && name == "primary"))
                    usage.FiveHour ??= parsedWindow;
                else
                    usage.SevenDay ??= parsedWindow;
            }
            return usage.FiveHour is null && usage.SevenDay is null ? null : usage;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
