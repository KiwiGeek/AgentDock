using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Models;
using AgentDock.Services.Launching;

namespace AgentDock.Services.Usage;

/// <summary>
/// Amp's credit balance, from Amp CLI's own report: <c>amp usage</c>. Amp owns its sign-in, so Agent
/// Dock never reads a token — the same route as Kiro's <c>kiro-cli chat /usage</c> and Antigravity's
/// <c>agy -p /usage</c>. Verified 2026-10-27 with Amp CLI 0.x on an individual account, whose report
/// is one line: "Individual credits: $10 remaining".
/// </summary>
public static class AmpUsage
{
    public const string Checking = "Checking your Amp credits…";
    public const string NoWindows = "Your Amp credits appear here once Amp reports them.";
    public const string NotInstalled = "Install Amp CLI (amp) to see your credits here.";
    public const string NotSignedIn = "Sign in to Amp CLI (amp login), then open this again.";
    public const string Unreachable = "Amp CLI couldn't report your credits.";
    public const string ShowingSaved = "Showing the last saved credits.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private static string UsagePath { get; } =
        Path.Combine(AppIdentity.SharedLocalFolder, "amp-usage.json");

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\][^\x07]*(?:\x07|\x1B\\)", RegexOptions.Compiled);

    /// <summary>
    /// A credit line: "Individual credits: $10 remaining", or a workspace's own (several accounts can
    /// be signed in at once). The name is kept so a workspace's balance isn't passed off as yours.
    /// </summary>
    private static readonly Regex Credits = new(
        @"^(?<name>[A-Za-z][A-Za-z0-9 '""-]*?credits)\s*:\s*\$(?<amount>\d[\d,]*(?:\.\d+)?)\s*(?<state>remaining|left)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Checks once, off the UI thread. A successful answer is saved.</summary>
    public static Task<UsageFetch> FetchAsync() => Task.Run(Fetch);

    private static UsageFetch Fetch()
    {
        if (PathResolver.Find("amp") is not { } exe)
            return new UsageFetch(null, NotInstalled);

        var (status, output, error) = Run(exe, "usage");
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
    /// Pure. Amp's plain report is a few markdown-ish lines; the balance is what it always gives. The
    /// details report (<c>--details</c>) is a tables dump that also counts against Amp's hourly lookup
    /// limit, so it isn't asked for.
    /// </summary>
    internal static UsageFetch Parse(string text, DateTimeOffset now)
    {
        var plain = StripAnsi(text).Replace("**", "");
        if (string.IsNullOrWhiteSpace(plain))
            return new UsageFetch(null, NoWindows);
        if (LooksSignedOut(plain))
            return new UsageFetch(null, NotSignedIn);

        string? individual = null;
        var others = new List<string>();
        foreach (var raw in plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Credits.Match(raw) is not { Success: true } match)
                continue;
            var name = Sentence(match.Groups["name"].Value);
            var amount = Dollars(match.Groups["amount"].Value);
            // Amp reports the balance left, not what's spent.
            if (name.StartsWith("individual", StringComparison.OrdinalIgnoreCase))
                individual = amount;
            else
                others.Add($"{name}: {amount}");
        }

        if (individual is null && others.Count == 0)
            return new UsageFetch(null, NoWindows);

        // The drawer has one line for it, so your own credits come first and a workspace's follow.
        var line = individual is not null ? $"Individual credits: {individual} remaining" : "";
        if (others.Count > 0)
            line = line.Length > 0 ? $"{line} · {string.Join(" · ", others)}" : string.Join(" · ", others);

        var usage = new PlanUsage
        {
            UpdatedAt = now.ToUnixTimeSeconds(),
            // Not "enabled" extra usage: there are no bars for it to sit under, so the drawer shows this
            // as its one line (Amp's CreditsLine) instead.
            Extra = new ExtraUsage { Balance = line }
        };
        return new UsageFetch(usage, null);
    }

    /// <summary>The drawer's and the overview's wording for the balance, or null when there isn't one.</summary>
    public static string? CreditsLine(PlanUsage? usage) =>
        usage?.Extra?.Balance is { Length: > 0 } line ? line : null;

    /// <summary>"10" or "1,234.50" as "$10.00", "$1,234.50".</summary>
    private static string Dollars(string text) =>
        double.TryParse(text.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("C2", CultureInfo.GetCultureInfo("en-US"))
            : $"${text}";

    /// <summary>
    /// The name with its later words in lower case ("Workspace "Acme" credits" stays, "individual
    /// credits" reads as typed), so a line reads in sentence case.
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

    private static bool LooksSignedOut(string text) =>
        text.Contains("Invalid or missing API key", StringComparison.OrdinalIgnoreCase)
        || text.Contains("amp login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Runs amp with no window and a time limit; returns its exit code and output.</summary>
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
            // A balance comes back in a couple of seconds; a slow one is a stuck CLI, not a result.
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
