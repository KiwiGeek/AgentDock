using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Models;
using AgentDock.Services.Launching;

namespace AgentDock.Services.Usage;

/// <summary>
/// Antigravity CLI's model quotas, from its own print-mode answer: <c>agy -p /usage</c>. Google
/// documents <c>/usage</c> as an interactive panel, but since 1.1.11 print mode answers the read-only
/// slash commands instead: one tab-separated record per line, without starting an agent turn or
/// spending quota. Antigravity owns its sign-in (Google), so Agent Dock never reads a token — the same
/// route as Kiro's <c>kiro-cli chat /usage</c> and Copilot's <c>gh api</c>. Verified 2026-10-27
/// against Antigravity CLI 1.4.8.
/// </summary>
public static class AntigravityUsage
{
    public const string Checking = "Checking your Antigravity limits…";
    public const string NoWindows = "Your Antigravity limits appear here once Antigravity CLI reports them.";
    public const string NotInstalled = "Install Antigravity CLI (agy) to see your limits here.";
    public const string NotSignedIn = "Sign in to Antigravity CLI, then open this again.";
    public const string Unreachable = "Antigravity CLI couldn't report your limits.";
    public const string NotUnderstood = "Antigravity CLI's limits weren't understood.";
    public const string ShowingSaved = "Showing the last saved limits.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private static string UsagePath { get; } =
        Path.Combine(AppIdentity.SharedLocalFolder, "antigravity-usage.json");

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\][^\x07]*(?:\x07|\x1B\\)", RegexOptions.Compiled);

    /// <summary>Checks once, off the UI thread. A successful answer is saved.</summary>
    public static Task<UsageFetch> FetchAsync() => Task.Run(Fetch);

    private static UsageFetch Fetch()
    {
        if (PathResolver.Find("agy") is not { } exe)
            return new UsageFetch(null, NotInstalled);

        var (status, output, error) = Run(exe, "-p", "/usage");
        var text = $"{output}\n{error}";
        if (LooksSignedOut(text))
            return new UsageFetch(null, NotSignedIn);
        if (status != 0 && string.IsNullOrWhiteSpace(output))
            return new UsageFetch(null, Unreachable);

        var result = Parse(text, DateTimeOffset.UtcNow);
        if (result.Usage is { } usage)
            Save(usage);
        return result;
    }

    /// <summary>
    /// Pure. Each line is "name, what the limit is, the share left or used, when it resets", for
    /// example "Gemini Models	Weekly Limit Remaining	98%	2026-11-03T20:55:22Z".
    /// Antigravity reports <em>remaining</em>, so the bar's used share is the rest. Every family's
    /// week becomes a labelled bar; the tightest also fills the drawer's main Week bar, which the
    /// overview and the suggestion compare against other agents.
    /// </summary>
    internal static UsageFetch Parse(string text, DateTimeOffset now)
    {
        var plain = StripAnsi(text);
        if (string.IsNullOrWhiteSpace(plain))
            return new UsageFetch(null, NotUnderstood);
        if (LooksSignedOut(plain))
            return new UsageFetch(null, NotSignedIn);

        var windows = new List<UsageWindow>();
        foreach (var raw in plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = raw.Split('\t', StringSplitOptions.TrimEntries);
            if (fields.Length < 3 || fields[0].Length == 0)
                continue;
            if (Share(fields[2]) is not { } percent)
                continue;

            // "Weekly Limit Remaining" is the share left; anything else ("… Used") is the share gone.
            var remaining = fields[1].Contains("remaining", StringComparison.OrdinalIgnoreCase);
            var title = Sentence(fields[0]);
            windows.Add(new UsageWindow
            {
                Title = title,
                UsedPercentage = Math.Clamp(remaining ? 100 - percent : percent, 0, 100),
                ResetsAt = fields.Length > 3 ? Reset(fields[3]) : 0
            });
        }

        if (windows.Count == 0)
            return new UsageFetch(null, NotUnderstood);

        var tightest = windows.MaxBy(window => window.UsedPercentage)!;
        var usage = new PlanUsage
        {
            UpdatedAt = now.ToUnixTimeSeconds(),
            SevenDay = new UsageWindow { UsedPercentage = tightest.UsedPercentage, ResetsAt = tightest.ResetsAt },
            ModelWeeks = windows
        };
        return new UsageFetch(usage, null);
    }

    /// <summary>"98%" or "98 %" as a number; null if it isn't one.</summary>
    private static double? Share(string text) =>
        double.TryParse(text.Trim().TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100)
            : null;

    /// <summary>
    /// The name with its later words in lower case ("Gemini Models" → "Gemini models"), so the drawer
    /// reads in sentence case without flattening names like "GPT".
    /// </summary>
    private static string Sentence(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length > 1 && char.IsUpper(word[0]) && word.Any(char.IsLower))
                words[i] = char.ToLowerInvariant(word[0]) + word[1..];
        }
        return string.Join(' ', words);
    }

    /// <summary>An ISO-8601 instant (Antigravity sends UTC with a Z), in Unix seconds; 0 if unreadable.</summary>
    private static long Reset(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when.ToUnixTimeSeconds()
            : 0;

    private static bool LooksSignedOut(string text) =>
        text.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        || text.Contains("please sign in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("please log in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("agy login", StringComparison.OrdinalIgnoreCase);

    private static string StripAnsi(string text) => Ansi.Replace(text, "");

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
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, DockJsonContext.Default.PlanUsage), Encoding.UTF8);
        File.Move(temp, UsagePath, overwrite: true);
    }

    /// <summary>Runs agy with no window and a time limit; returns its exit code and output.</summary>
    private static (int Code, string Output, string Error) Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return (-1, "", "");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            // A quota answer comes back in a few seconds; a slow one is a stuck CLI, not a result.
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, "", "timed out");
            }
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "", ex.Message);
        }
    }
}
