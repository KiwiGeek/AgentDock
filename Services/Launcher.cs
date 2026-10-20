using System.ComponentModel;
using System.Diagnostics;
using AgentDock.Models;

namespace AgentDock.Services;

public static class Launcher
{
    /// <summary>Launches an agent. Returns null on success, or a message describing what went wrong.</summary>
    public static string? Launch(AgentItem item, string folder, DockSettings settings, bool waifuBackgrounds = false)
    {
        if (!item.IsAvailable)
            return item.Subtitle;

        try
        {
            if (item.IsCli)
            {
                if (!Directory.Exists(folder))
                    return "That folder no longer exists. Choose another one.";

                if (IsAider(item.Definition) && item.Definition.Model?.Any(char.IsWhiteSpace) == true)
                    return "An Aider model name cannot contain spaces.";

                // DeepSeek Harness and similar: no Terminal window; Agent Dock owns the process.
                if (HostedAgents.IsHosted(item.Definition))
                    return HostedAgents.Start(item, folder, settings);

                var command = string.Join(' ', new[] { item.Definition.Command, item.Definition.Arguments,
                        AiderModelArgument(item.Definition) }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                var profile = waifuBackgrounds ? TerminalProfiles.TryGetReadyProfile(item.Definition) : null;
                // An agent linked to an API account also gets that key's variable (ApiUsage.EnvironmentFor).
                StartInTerminal(item.Definition.Name, command, folder, item.Definition.Shell ?? settings.Shell, settings.Terminal,
                    ApiUsage.EnvironmentFor(item.Definition, settings.ApiAccounts), settings.CloseTerminalWhenAgentExits, profile);
            }
            else if (KnownAgents.FolderLink(item.Definition) is { } link)
            {
                // A desktop app that opens in a folder through its own link (Claude Desktop's Code tab).
                if (!Directory.Exists(folder))
                    return "That folder no longer exists. Choose another one.";
                Process.Start(new ProcessStartInfo(link.Replace("{folder}", Uri.EscapeDataString(folder))) { UseShellExecute = true });
            }
            else if (KnownAgents.OpensFolderThroughCli(item.Definition))
            {
                // GitHub Copilot desktop: Copilot CLI's "copilot app", run in the folder, opens a session there.
                if (!Directory.Exists(folder))
                    return "That folder no longer exists. Choose another one.";
                if (PathResolver.Find("copilot") is { } cli)
                {
                    var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
                    start.ArgumentList.Add("app");
                    Process.Start(start);
                }
                else
                {
                    StartApp(item); // without Copilot CLI it can't be given the folder, but still opens
                }
            }
            else if (KnownAgents.OpensFolderInExe(item.Definition) && KnownAgents.FolderAppExe(item) is { } app)
            {
                // A desktop app that opens a folder given on its command line (Cursor).
                if (!Directory.Exists(folder))
                    return "That folder no longer exists. Choose another one.";
                var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = folder };
                start.ArgumentList.Add(folder);
                Process.Start(start);
            }
            else
            {
                StartApp(item);
            }
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"{item.Name} didn't start: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens a desktop agent one of its other ways (<see cref="KnownAgents.OtherLaunches"/>), with no
    /// folder. Returns null on success, or a message describing what went wrong.
    /// </summary>
    public static string? Launch(AgentItem item, OtherLaunch other)
    {
        if (!item.IsAgentAvailable)
            return item.Subtitle;
        try
        {
            if (other.Link is { } link)
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            else
                StartApp(item);
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"{item.Name} didn't start: {ex.Message}";
        }
    }

    private static void StartApp(AgentItem item) =>
        Process.Start(new ProcessStartInfo(item.ExecutablePath ?? $@"shell:AppsFolder\{item.AppId}") { UseShellExecute = true });

    /// <summary>
    /// Aider model names are provider/model identifiers, so they do not contain spaces. Keeping this
    /// value a single shell token avoids turning a setting field into another command-line surface.
    /// </summary>
    private static string? AiderModelArgument(AgentDefinition agent) =>
        IsAider(agent) && agent.Model?.Trim() is { Length: > 0 } model ? $"--model {model}" : null;

    private static bool IsAider(AgentDefinition agent) =>
        string.Equals(agent.Catalog, "aider", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileNameWithoutExtension(agent.Command?.Trim().Trim('\"')), "aider", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a plain shell in the folder, no agent.</summary>
    public static string? OpenShell(string folder, DockSettings settings)
    {
        if (!Directory.Exists(folder))
            return "That folder no longer exists. Choose another one.";
        try
        {
            StartInTerminal(null, null, folder, settings.Shell, settings.Terminal);
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"The terminal didn't start: {ex.Message}";
        }
    }

    /// <summary>
    /// The agent runs inside a shell (rather than directly) so that npm .cmd shims work and you're
    /// left at a prompt in the folder when the agent exits.
    /// </summary>
    private static void StartInTerminal(string? title, string? command, string folder, string shellSetting, string terminalSetting,
        IReadOnlyDictionary<string, string>? env = null, bool closeWhenDone = false, string? profile = null)
    {
        var (shellExe, shellArgs) = BuildShell(shellSetting, command, env, closeWhenDone);
        var wt = terminalSetting.Equals("console", StringComparison.OrdinalIgnoreCase) ? null : PathResolver.Find("wt.exe");

        ProcessStartInfo psi;
        if (wt is not null)
        {
            psi = new ProcessStartInfo(wt);
            if (profile is not null)
            {
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add(profile);
            }
            if (title is not null)
            {
                psi.ArgumentList.Add("--title");
                psi.ArgumentList.Add($"{title} · {Path.GetFileName(folder.TrimEnd('\\'))}");
            }
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(folder);
            psi.ArgumentList.Add(shellExe);
            // wt treats a bare ';' as "new command"; escape it so it reaches the shell.
            foreach (var arg in shellArgs)
                psi.ArgumentList.Add(arg.Replace(";", @"\;"));
        }
        else
        {
            // A console app started from a GUI process gets its own window (or the user's
            // default terminal, if one is set in Windows settings).
            psi = new ProcessStartInfo(shellExe) { WorkingDirectory = folder };
            foreach (var arg in shellArgs)
                psi.ArgumentList.Add(arg);
        }

        psi.UseShellExecute = false;
        Process.Start(psi);
    }

    private static (string Exe, List<string> Args) BuildShell(string shellSetting, string? command,
        IReadOnlyDictionary<string, string>? env = null, bool closeWhenDone = false)
    {
        var shell = shellSetting.ToLowerInvariant() switch
        {
            "pwsh" => "pwsh",
            "powershell" => "powershell",
            "cmd" => "cmd",
            _ => PathResolver.Find("pwsh.exe") is not null ? "pwsh" : "powershell"
        };

        // The agent's variables are set inside the shell, in front of its command. Setting them on
        // the process wouldn't reach the agent: when Windows Terminal is already open, wt.exe hands
        // the new tab to that Terminal, which starts it with its own environment.
        var variables = (env ?? new Dictionary<string, string>())
            .Where(v => IsVariableName(v.Key))
            .ToList();

        if (shell == "cmd")
        {
            // cmd expands %NAME% itself. "set "N=v"" keeps trailing spaces and & out of trouble.
            var prefix = string.Concat(variables.Select(v => $"set \"{v.Key}={v.Value.Replace("\"", "")}\" && "));
            return ("cmd.exe", command is null ? [] : [closeWhenDone ? "/c" : "/k", prefix + command]);
        }

        var exe = shell == "pwsh" ? (PathResolver.Find("pwsh.exe") ?? "pwsh.exe") : "powershell.exe";
        var assignments = string.Concat(variables.Select(v => $"${{env:{v.Key}}} = {PowerShellString(v.Value)}; "));
        if (command is not null && variables.Count > 0)
        {
            // Windows Terminal uses semicolons as its own command separators, even inside the
            // PowerShell -Command argument. Passing a UTF-16LE encoded script leaves it one opaque
            // argument, so per-agent variables reach the shell that runs the agent.
            var script = assignments + command;
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            return (exe, closeWhenDone ? ["-NoLogo", "-EncodedCommand", encoded] : ["-NoLogo", "-NoExit", "-EncodedCommand", encoded]);
        }
        return command is null
            ? (exe, ["-NoLogo"])
            : (exe, closeWhenDone ? ["-NoLogo", "-Command", assignments + command] : ["-NoLogo", "-NoExit", "-Command", assignments + command]);
    }

    /// <summary>Letters, digits and underscores (and the parentheses of names like ProgramFiles(x86)).</summary>
    private static bool IsVariableName(string name) =>
        name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '(' or ')');

    /// <summary>
    /// A double-quoted PowerShell string for a value, with <c>%NAME%</c> turned into
    /// <c>${env:NAME}</c> so PowerShell reads it from the environment. Everything else is literal.
    /// </summary>
    internal static string PowerShellString(string value)
    {
        var text = new System.Text.StringBuilder("\"");
        var i = 0;
        while (i < value.Length)
        {
            if (value[i] == '%' && value.IndexOf('%', i + 1) is var end and > 0 && IsVariableName(value[(i + 1)..end]))
            {
                text.Append("${env:").Append(value[(i + 1)..end]).Append('}');
                i = end + 1;
                continue;
            }
            // Backtick escapes PowerShell's specials inside double quotes.
            if (value[i] is '`' or '"' or '$')
                text.Append('`');
            text.Append(value[i]);
            i++;
        }
        return text.Append('"').ToString();
    }
}
