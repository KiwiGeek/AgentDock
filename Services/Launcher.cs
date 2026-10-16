using System.ComponentModel;
using System.Diagnostics;
using AgentDock.Models;

namespace AgentDock.Services;

public static class Launcher
{
    /// <summary>Launches an agent. Returns null on success, or a message describing what went wrong.</summary>
    public static string? Launch(AgentItem item, string folder, DockSettings settings)
    {
        if (!item.IsAvailable)
            return item.Subtitle;

        try
        {
            if (item.IsCli)
            {
                if (!Directory.Exists(folder))
                    return "That folder no longer exists. Choose another one.";

                var command = string.Join(' ', new[] { item.Definition.Command, item.Definition.Arguments }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                StartInTerminal(item.Definition.Name, command, folder, item.Definition.Shell ?? settings.Shell, settings.Terminal,
                    item.Definition.Env);
            }
            else if (item.ExecutablePath is not null)
            {
                Process.Start(new ProcessStartInfo(item.ExecutablePath) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo($@"shell:AppsFolder\{item.AppId}") { UseShellExecute = true });
            }
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"{item.Name} didn't start: {ex.Message}";
        }
    }

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
        IReadOnlyDictionary<string, string>? env = null)
    {
        var (shellExe, shellArgs) = BuildShell(shellSetting, command, env);
        var wt = terminalSetting.Equals("console", StringComparison.OrdinalIgnoreCase) ? null : PathResolver.Find("wt.exe");

        ProcessStartInfo psi;
        if (wt is not null)
        {
            psi = new ProcessStartInfo(wt);
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
        IReadOnlyDictionary<string, string>? env = null)
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
            return ("cmd.exe", command is null ? [] : ["/k", prefix + command]);
        }

        var exe = shell == "pwsh" ? (PathResolver.Find("pwsh.exe") ?? "pwsh.exe") : "powershell.exe";
        var assignments = string.Concat(variables.Select(v => $"${{env:{v.Key}}} = {PowerShellString(v.Value)}; "));
        return command is null
            ? (exe, ["-NoLogo"])
            : (exe, ["-NoLogo", "-NoExit", "-Command", assignments + command]);
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
