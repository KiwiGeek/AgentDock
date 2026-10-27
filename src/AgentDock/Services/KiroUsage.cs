using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Kiro CLI monthly credit usage, from <c>kiro-cli chat --no-interactive /usage</c>. Kiro owns its
/// sign-in (AWS Builder ID); Agent Dock never reads a token. Same idea as Copilot's <c>gh api</c>
/// route. Verified 2026-10-21 against Kiro CLI 2.33.0.
/// </summary>
public static class KiroUsage
{
    public const string Checking = "Checking your Kiro usage…";
    public const string NoWindows = "Your Kiro usage appears here once Kiro CLI reports it.";
    public const string NotInstalled = "Install Kiro CLI (kiro-cli) to see your usage here.";
    public const string NotSignedIn = "Sign in to Kiro CLI (kiro-cli login), then open this again.";
    public const string Unreachable = "Kiro CLI couldn't report usage.";
    public const string NotUnderstood = "Kiro CLI's usage reply wasn't understood.";
    public const string ShowingSaved = "Showing the last saved usage.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private static string UsagePath { get; } =
        Path.Combine(AppIdentity.SharedLocalFolder, "kiro-usage.json");

    // Current CLI (2.33): "Estimated Usage | resets on 2026-11-01 | KIRO FREE"
    //                      "Credits (0.16 of 50 covered in plan), 0.3%"
    // Older box UI: "100% (resets on 01/01)" and "(0.00 of 50 covered in plan)".
    private static readonly Regex HeaderLine = new(
        @"resets\s+on\s+(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2})(?:\s*\|\s*([A-Za-z0-9 +_-]+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PlanOnly = new(
        @"^\s*Plan:\s*(.+?)(?:\s*\|\s*\d+\s+usage\s+breakdowns?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CreditsLine = new(
        @"(?:Credits\s*)?\((\d+(?:\.\d+)?)\s+of\s+(\d+(?:\.\d+)?)\s+covered\s+in\s+plan\)(?:\s*,\s*(\d+(?:\.\d+)?)%)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BarPercent = new(
        @"(\d+(?:\.\d+)?)%\s*\(resets\s+on\s+(\d{1,2}/\d{1,2})\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BonusLine = new(
        @"Bonus\s+credits:\s*(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)\s+credits\s+used",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\][^\x07]*(?:\x07|\x1B\\)", RegexOptions.Compiled);

    /// <summary>Checks once, off the UI thread. A successful answer is saved.</summary>
    public static Task<UsageFetch> FetchAsync() => Task.Run(Fetch);

    private static UsageFetch Fetch()
    {
        if (PathResolver.Find("kiro-cli") is not { } exe)
            return new UsageFetch(null, NotInstalled);

        var (status, output, error) = Run(exe, "chat", "--no-interactive", "/usage");
        var text = StripAnsi($"{output}\n{error}");
        if (LooksSignedOut(text, status))
            return new UsageFetch(null, NotSignedIn);
        if (status != 0 && string.IsNullOrWhiteSpace(output))
            return new UsageFetch(null, Unreachable);

        var result = Parse(text, DateTimeOffset.UtcNow);
        if (result.Usage is { } usage)
            Save(usage);
        return result;
    }

    /// <summary>
    /// Pure. Accepts the compact 2.33 report and the older box-drawing meter. Monthly plan credits
    /// become <see cref="PlanUsage.SevenDay"/> (the drawer's long window); bonus credits, when
    /// present, become <see cref="PlanUsage.Extra"/>. Credit amounts sit on Extra even when the
    /// bar is the plan gauge, so the drawer can say "0.16 of 50" under the percent.
    /// </summary>
    internal static UsageFetch Parse(string text, DateTimeOffset now)
    {
        var plain = StripAnsi(text);
        if (string.IsNullOrWhiteSpace(plain))
            return new UsageFetch(null, NotUnderstood);
        if (LooksSignedOut(plain, 0))
            return new UsageFetch(null, NotSignedIn);

        var usage = new PlanUsage { UpdatedAt = now.ToUnixTimeSeconds() };
        long resetsAt = 0;
        double? percent = null;
        double? used = null;
        double? limit = null;

        foreach (var raw in plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim();
            if (HeaderLine.Match(line) is { Success: true } header)
            {
                resetsAt = ParseReset(header.Groups[1].Value, now) ?? resetsAt;
                if (header.Groups[2].Success && header.Groups[2].Value.Trim() is { Length: > 0 } plan)
                    usage.Plan = CleanPlan(plan);
                continue;
            }

            if (PlanOnly.Match(line) is { Success: true } planOnly
                && planOnly.Groups[1].Value.Trim() is { Length: > 0 } named)
            {
                usage.Plan = CleanPlan(named);
                continue;
            }

            if (CreditsLine.Match(line) is { Success: true } credits)
            {
                used = ParseDouble(credits.Groups[1].Value);
                limit = ParseDouble(credits.Groups[2].Value);
                if (credits.Groups[3].Success)
                    percent = ParseDouble(credits.Groups[3].Value);
                else if (used is { } u && limit is > 0 and var cap)
                    percent = Math.Clamp(u * 100 / cap, 0, 100);
                continue;
            }

            if (BarPercent.Match(line) is { Success: true } bar)
            {
                percent ??= ParseDouble(bar.Groups[1].Value);
                resetsAt = ParseReset(bar.Groups[2].Value, now) ?? resetsAt;
                continue;
            }

            if (BonusLine.Match(line) is { Success: true } bonus
                && ParseDouble(bonus.Groups[1].Value) is { } bonusUsed
                && ParseDouble(bonus.Groups[2].Value) is { } bonusLimit)
            {
                usage.Extra = new ExtraUsage
                {
                    Enabled = true,
                    Used = bonusUsed,
                    Limit = bonusLimit
                };
            }
        }

        if (percent is { } usedPercent)
        {
            usage.SevenDay = new UsageWindow
            {
                UsedPercentage = Math.Clamp(usedPercent, 0, 100),
                ResetsAt = resetsAt
            };
        }

        // Plan credit counts for the drawer caption; not "extra" unless bonus already filled Extra.
        if (used is { } creditUsed && limit is { } creditLimit && usage.Extra is null)
        {
            usage.Extra = new ExtraUsage
            {
                Enabled = false,
                Used = creditUsed,
                Limit = creditLimit
            };
        }

        if (usage.SevenDay is null && usage.Plan is null)
            return new UsageFetch(null, NotUnderstood);
        return new UsageFetch(usage, null);
    }

    private static bool LooksSignedOut(string text, int status) =>
        text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("please log in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("please login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("kiro-cli login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
        || (status != 0 && text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase));

    private static string CleanPlan(string plan) =>
        plan.Trim().Trim('|').Trim();

    private static string StripAnsi(string text) => Ansi.Replace(text, "");

    private static double? ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// <c>YYYY-MM-DD</c> is midnight UTC that day; <c>MM/DD</c> is the next occurrence of that
    /// month-day in local time (this year, or next if it has already passed).
    /// </summary>
    private static long? ParseReset(string text, DateTimeOffset now)
    {
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso))
            return iso.ToUnixTimeSeconds();

        if (!DateTime.TryParseExact(text, "M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var md)
            && !DateTime.TryParseExact(text, "MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out md))
            return null;

        var local = now.ToLocalTime();
        var candidate = new DateTimeOffset(local.Year, md.Month, md.Day, 0, 0, 0, local.Offset);
        if (candidate <= local)
            candidate = candidate.AddYears(1);
        return candidate.ToUnixTimeSeconds();
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
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, DockJsonContext.Default.PlanUsage), Encoding.UTF8);
        File.Move(temp, UsagePath, overwrite: true);
    }

    /// <summary>Runs kiro-cli with no window and a time limit; returns its exit code and output.</summary>
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
