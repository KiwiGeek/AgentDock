using System.Diagnostics;
using System.Text.Json;

namespace AgentDock.Services;

/// <summary>What <see cref="CopilotUsage.FetchAsync"/> found: premium requests used this month, or why not.</summary>
public sealed record CopilotUsageResult(double? Used, string? Problem, DateTimeOffset FetchedAt);

/// <summary>
/// GitHub Copilot premium requests used this month. Copilot keeps no record of its quota locally,
/// so this asks GitHub's documented billing API
/// (https://docs.github.com/rest/billing/usage#get-billing-premium-request-usage-report-for-a-user)
/// through the GitHub CLI. gh uses its own sign-in, so Agent Dock never handles a token. The API
/// needs gh's "user" scope, and only covers plans the user pays for themselves. It reports usage,
/// not the plan's allowance; that comes from the agent's "monthlyAllowance" setting.
/// </summary>
public static class CopilotUsage
{
    public const string ScopeCommand = "gh auth refresh -h github.com -s user";

    private static string? _login;

    public static Task<CopilotUsageResult> FetchAsync() => Task.Run(Fetch);

    private static CopilotUsageResult Fetch()
    {
        var now = DateTimeOffset.UtcNow;
        CopilotUsageResult Problem(string message) => new(null, message, now);

        if (PathResolver.Find("gh") is not { } gh)
            return Problem("Install the GitHub CLI (gh) to see your Copilot usage here.");

        if (_login is null)
        {
            var (code, output, error) = Run(gh, "api", "user", "--jq", ".login");
            if (code != 0 || string.IsNullOrWhiteSpace(output))
                return Problem(error.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
                    ? "Sign in to the GitHub CLI (gh auth login) to see your Copilot usage here."
                    : "GitHub couldn't be reached to check your Copilot usage.");
            _login = output.Trim();
        }

        // No leading slash: Git Bash's MSYS layer rewrites "/users/..." into a file path.
        var (status, json, stderr) = Run(gh, "api", $"users/{_login}/settings/billing/premium_request/usage");
        if (status != 0)
            return Problem(stderr.Contains("\"user\" scope", StringComparison.OrdinalIgnoreCase)
                ? $"Agent Dock needs one more GitHub permission to read your Copilot usage. Run: {ScopeCommand}"
                : stderr.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
                    ? "GitHub only reports usage for Copilot plans you pay for yourself."
                    : "GitHub couldn't be reached to check your Copilot usage.");

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("usageItems", out var items) || items.ValueKind != JsonValueKind.Array)
                return Problem("GitHub's usage report wasn't in the expected format.");

            double used = 0;
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("grossQuantity", out var quantity) && quantity.ValueKind == JsonValueKind.Number)
                    used += quantity.GetDouble();
            return new CopilotUsageResult(used, null, now);
        }
        catch (JsonException)
        {
            return Problem("GitHub's usage report wasn't in the expected format.");
        }
    }

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
