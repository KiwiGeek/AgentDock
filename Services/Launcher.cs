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
                StartInTerminal(item.Name, command, folder, item.Definition.Shell ?? settings.Shell, settings.Terminal);
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
    private static void StartInTerminal(string? title, string? command, string folder, string shellSetting, string terminalSetting)
    {
        var (shellExe, shellArgs) = BuildShell(shellSetting, command);
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

    private static (string Exe, List<string> Args) BuildShell(string shellSetting, string? command)
    {
        var shell = shellSetting.ToLowerInvariant() switch
        {
            "pwsh" => "pwsh",
            "powershell" => "powershell",
            "cmd" => "cmd",
            _ => PathResolver.Find("pwsh.exe") is not null ? "pwsh" : "powershell"
        };

        if (shell == "cmd")
            return ("cmd.exe", command is null ? [] : ["/k", command]);

        var exe = shell == "pwsh" ? (PathResolver.Find("pwsh.exe") ?? "pwsh.exe") : "powershell.exe";
        return command is null
            ? (exe, ["-NoLogo"])
            : (exe, ["-NoLogo", "-NoExit", "-Command", command]);
    }
}
