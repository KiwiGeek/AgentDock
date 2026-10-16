using Microsoft.Win32;

namespace AgentDock.Services;

/// <summary>Finds commands on PATH the same way cmd.exe does.</summary>
public static class PathResolver
{
    /// <summary>
    /// Rebuilds this process's PATH from the registry (machine, then user), the way Explorer does
    /// for a new process. The resident instance starts at sign-in, so without this an agent
    /// installed later (which adds itself to the user PATH) would never be found, and shells
    /// started from here wouldn't find it either. Entries only this process had are kept at the end.
    /// </summary>
    public static void RefreshPath()
    {
        try
        {
            var fresh = new[]
                {
                    ReadPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
                    ReadPath(Registry.CurrentUser, "Environment")
                }
                .SelectMany(p => p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
            if (fresh.Count == 0)
                return;
            var current = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var merged = fresh.Concat(current).Distinct(StringComparer.OrdinalIgnoreCase);
            Environment.SetEnvironmentVariable("PATH", string.Join(';', merged));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the PATH we have.
        }
    }

    private static string ReadPath(RegistryKey root, string subKey)
    {
        using var key = root.OpenSubKey(subKey);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path
            ? Environment.ExpandEnvironmentVariables(path)
            : "";
    }

    public static string? Find(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        command = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        IEnumerable<string> Candidates(string basePath) =>
            Path.HasExtension(basePath)
                ? [basePath]
                : extensions.Select(ext => basePath + ext.ToLowerInvariant());

        if (Path.IsPathRooted(command))
            return Candidates(command).FirstOrDefault(File.Exists);

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in dirs)
        {
            string full;
            try
            {
                full = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), command);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var hit = Candidates(full).FirstOrDefault(File.Exists);
            if (hit is not null)
                return hit;
        }
        return null;
    }
}
