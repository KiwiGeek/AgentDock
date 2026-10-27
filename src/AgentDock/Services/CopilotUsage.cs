using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AgentDock.Services;

/// <summary>
/// What <see cref="CopilotUsage.FetchAsync"/> found: this month's premium usage against the plan's
/// entitlement, or why not. <see cref="Unit"/> is "AI credits" (token-based billing) or "premium
/// requests" (GitHub's legacy billing).
/// </summary>
public sealed record CopilotUsageResult(
    double? Used, double? Entitlement, bool Unlimited, string Unit, DateTimeOffset? ResetsAt,
    bool OveragePermitted, double Overage, string? Problem, DateTimeOffset FetchedAt);

/// <summary>
/// GitHub Copilot usage this month, as Copilot's own clients see it: <c>GET /copilot_internal/user</c>
/// (undocumented) returns <c>quota_snapshots.premium_interactions</c> with the entitlement, what's
/// left and the reset date. Copilot CLI keeps no quota locally and has no usage command for scripts,
/// so this asks GitHub through the GitHub CLI, with gh's own sign-in: Agent Dock never handles a
/// token, and no extra scope is needed (it worked with gist, read:org, repo, workflow; 2026-10-16).
/// </summary>
/// <remarks>
/// It replaced the documented billing API (<c>users/{login}/settings/billing/premium_request/usage</c>),
/// which needs gh's "user" scope, only covers plans the user pays for, and has no allowance, so the
/// agent needed a "monthlyAllowance" setting. Owner's decision, 2026-10-16.
/// </remarks>
public static class CopilotUsage
{
    public static Task<CopilotUsageResult> FetchAsync() => Task.Run(Fetch);

    private static CopilotUsageResult Fetch()
    {
        var now = DateTimeOffset.UtcNow;
        CopilotUsageResult Problem(string message) => new(null, null, false, "", null, false, 0, message, now);

        if (PathResolver.Find("gh") is not { } gh)
            return Problem("Install the GitHub CLI (gh) to see your Copilot usage here.");

        // No leading slash: Git Bash's MSYS layer rewrites "/copilot_internal/..." into a file path.
        var (status, json, stderr) = Run(gh, "api", "copilot_internal/user");
        if (status != 0)
            return Problem(stderr.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
                           || stderr.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
                ? "Sign in to the GitHub CLI (gh auth login) to see your Copilot usage here."
                : stderr.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
                    ? "This GitHub account doesn't have Copilot."
                    : "GitHub couldn't be reached to check your Copilot usage.");
        return Parse(json, now);
    }

    /// <summary>
    /// Pure. As seen 2026-10-16: <c>token_based_billing</c> (true: units are AI credits),
    /// <c>quota_reset_date_utc</c> (ISO) or <c>quota_reset_date</c> ("2026-11-01"), and
    /// <c>quota_snapshots.premium_interactions</c> with <c>entitlement</c> (7000), <c>remaining</c>,
    /// <c>credits_used</c> (token-based billing), <c>unlimited</c>, <c>overage_permitted</c> and
    /// <c>overage_count</c>. Used is <c>credits_used</c> when given, else entitlement minus remaining.
    /// </summary>
    internal static CopilotUsageResult Parse(string json, DateTimeOffset now)
    {
        CopilotUsageResult Problem(string message) => new(null, null, false, "", null, false, 0, message, now);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object
                || !snapshots.TryGetProperty("premium_interactions", out var premium) || premium.ValueKind != JsonValueKind.Object)
                return Problem("GitHub didn't report a Copilot quota for this account.");

            var tokenBased = root.TryGetProperty("token_based_billing", out var billing) && billing.ValueKind == JsonValueKind.True;
            var unlimited = premium.TryGetProperty("unlimited", out var open) && open.ValueKind == JsonValueKind.True;
            var entitlement = Number(premium, "entitlement");
            double? used = Number(premium, "credits_used") is { } credits && tokenBased
                ? credits
                : entitlement is { } total && Number(premium, "remaining") is { } left ? Math.Max(0, total - left) : null;

            return new CopilotUsageResult(
                used,
                entitlement is > 0 ? entitlement : null,
                unlimited,
                tokenBased ? "AI credits" : "premium requests",
                ResetDate(root),
                premium.TryGetProperty("overage_permitted", out var overage) && overage.ValueKind == JsonValueKind.True,
                Number(premium, "overage_count") ?? 0,
                null,
                now);
        }
        catch (JsonException)
        {
            return Problem("GitHub's reply wasn't understood.");
        }
    }

    private static DateTimeOffset? ResetDate(JsonElement root)
    {
        foreach (var name in new[] { "quota_reset_date_utc", "quota_reset_date" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return at;
        return null;
    }

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>Runs gh with no window and a time limit; returns its exit code and output.</summary>
    private static (int Code, string Output, string Error) Run(string gh, params string[] args)
    {
        var start = new ProcessStartInfo(gh)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        // Plain text, never an interactive pager or prompt.
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_PAGER"] = "";
        start.Environment["NO_COLOR"] = "1";

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return (-1, "", "");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
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
