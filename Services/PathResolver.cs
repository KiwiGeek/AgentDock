namespace AgentDock.Services;

/// <summary>Finds commands on PATH the same way cmd.exe does.</summary>
public static class PathResolver
{
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
