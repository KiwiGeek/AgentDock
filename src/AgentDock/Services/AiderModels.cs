using System.ComponentModel;
using System.Diagnostics;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>Asks the locally installed Aider which model identifiers match a search term.</summary>
public static class AiderModels
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs Aider's own <c>--list-models</c> command. This is a catalogue search, not a check that
    /// the user's provider account can use a returned model.
    /// </summary>
    public static Task<AiderModelSearch> FindAsync(AgentDefinition agent, string search) =>
        Task.Run(() => FindCore(agent, search));

    private static AiderModelSearch FindCore(AgentDefinition agent, string search)
    {
        var aider = PathResolver.Find(agent.Command);
        if (aider is null)
            return new([], "Aider is not installed or is no longer on PATH.");

        var start = new ProcessStartInfo(aider)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--list-models");
        start.ArgumentList.Add(search);
        // Aider's normal colour sequences obscure the identifiers when its output is parsed.
        start.Environment["NO_COLOR"] = "1";
        if (agent.Env is not null)
            foreach (var pair in agent.Env)
                start.Environment[pair.Key] = Environment.ExpandEnvironmentVariables(pair.Value);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return new([], "Aider could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new([], "Aider took too long to list models.");
            }

            var models = Parse(output.Result);
            return models.Count > 0
                ? new(models, null)
                : new([], $"Aider did not find models matching \"{search}\".");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new([], "Aider could not be started.");
        }
    }

    /// <summary>
    /// Aider prints each result as a friendly name followed by its usable id in parentheses, e.g.
    /// <c>- GPT-4o (openai/gpt-4o)</c>. Older versions can print the id directly.
    /// </summary>
    private static List<string> Parse(string output)
    {
        var results = new List<string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var open = line.LastIndexOf('(');
            var close = line.LastIndexOf(')');
            var candidate = open >= 0 && close > open
                ? line[(open + 1)..close].Trim()
                : line.TrimStart('-', ' ', '\t').Trim();
            if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace)
                || !candidate.Any(char.IsLetterOrDigit))
                continue;
            if (!results.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                results.Add(candidate);
            if (results.Count == 100)
                break;
        }
        return results;
    }
}

/// <summary>Aider's matching model identifiers, or a safe message suitable for the settings page.</summary>
public sealed record AiderModelSearch(List<string> Models, string? Message);
