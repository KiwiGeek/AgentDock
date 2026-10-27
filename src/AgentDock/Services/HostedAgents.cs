using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Long-lived CLI agents that Agent Dock runs itself (no Terminal window), so the user can stop
/// them from the flyout. Today: DeepSeek Harness (<c>dsh web</c>), which serves a browser UI and
/// would otherwise leave a Terminal tab open for the server.
/// </summary>
public static class HostedAgents
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Process> Running = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient ReadyClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// How long to wait after start for an immediate failure (port in use, …). Kept short so the
    /// flyout's UI thread isn't frozen; a healthy <c>dsh web</c> is still up after this.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(800);

    /// <summary>Default listen port for <c>dsh web</c> when the agent doesn't pass <c>--port</c>.</summary>
    private const int DefaultWebPort = 3080;

    /// <summary>Raised on a thread-pool thread when a hosted agent starts, stops or exits.</summary>
    public static event Action? Changed;

    private static string PidDirectory =>
        Path.Combine(AppIdentity.LocalFolder, "hosted");

    /// <summary>
    /// Whether this agent is one Agent Dock hosts in the background. Matched by catalog id
    /// (<c>deepseek</c>) or command (<c>dsh</c>).
    /// </summary>
    public static bool IsHosted(AgentDefinition agent)
    {
        if (agent.Kind != AgentKind.Cli)
            return false;
        var catalog = (agent.Catalog ?? agent.Id).ToLowerInvariant();
        if (catalog == "deepseek")
            return true;
        var command = Path.GetFileNameWithoutExtension(agent.Command?.Trim().Trim('"') ?? "")
            .ToLowerInvariant();
        return command == "dsh";
    }

    /// <summary>Whether a hosted process for this agent id is still running.</summary>
    public static bool IsRunning(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return false;
        lock (Gate)
        {
            if (Running.TryGetValue(agentId, out var process))
            {
                try
                {
                    if (!process.HasExited)
                        return true;
                }
                catch (InvalidOperationException)
                {
                    // Fall through and check the pid file from a previous session.
                }
                Running.Remove(agentId);
            }
        }
        return TryGetPid(agentId) is { } pid && ProcessAlive(pid);
    }

    /// <summary>
    /// Starts (or restarts) the agent with no window. The working folder is passed as
    /// <c>AGENTDOCK_WORKSPACE</c> and opened in the browser UI via the shipped dsh plugin
    /// (<c>tools/dsh-plugins</c>). Returns null on success, or a short message on failure.
    /// </summary>
    public static string? Start(AgentItem item, string folder, DockSettings settings, bool waifuBackgrounds = false)
    {
        if (!IsHosted(item.Definition))
            return null;
        if (!item.IsAvailable)
            return item.Subtitle;
        if (!Directory.Exists(folder))
            return "That folder no longer exists. Choose another one.";

        var agentId = item.Preset?.Agent ?? item.Definition.Id;
        // Kill this session's process and any orphan left after Agent Dock was force-quit (pid file).
        Stop(agentId);

        try
        {
            var exe = item.ExecutablePath ?? item.Definition.Command
                ?? throw new InvalidOperationException("No command.");
            // Prefer node + bin.js so the process we track is the real server (not a cmd.exe that
            // can exit while leaving node behind). Fall back to cmd for odd installs.
            var start = BuildStartInfo(exe, item.Definition.Arguments, folder);
            ApplyDeepSeekLaunch(start, folder, waifuBackgrounds);
            ApplyEnvironment(start, ApiUsage.EnvironmentFor(item.Definition, settings.ApiAccounts));
            // Capture the printed "dsh web: http://…/?token=…" line — without the token the
            // browser gets 401, and we need to add agentdock_workspace onto that same URL.
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.CreateNoWindow = true;

            var printedUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (TryFindWebUrl(e.Data) is { } url)
                    printedUrl.TrySetResult(url);
            };
            process.ErrorDataReceived += (_, _) => { }; // drain so the pipe can't fill and stall
            process.Exited += (_, _) => OnExited(agentId, process);
            if (!process.Start())
                return $"{item.Name} didn't start.";

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            WritePid(agentId, process.Id);

            // Port already in use (orphan we couldn't kill) or a bad start exits almost at once.
            if (process.WaitForExit((int)StartupGrace.TotalMilliseconds))
            {
                ClearPid(agentId);
                try { process.Dispose(); } catch (InvalidOperationException) { }
                return $"{item.Name} stopped right away. If another copy is already running, stop it first.";
            }

            lock (Gate)
                Running[agentId] = process;
            Changed?.Invoke();

            // Browser open is fire-and-forget: Start must stay snappy for the flyout.
            var port = PortFromArgs(start.ArgumentList) ?? DefaultWebPort;
            _ = OpenBrowserWhenReadyAsync(folder, port, process, printedUrl.Task);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"{item.Name} didn't start: {ex.Message}";
        }
    }

    /// <summary>Stops the hosted process for <paramref name="agentId"/>, if any.</summary>
    public static void Stop(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return;
        Process? process;
        lock (Gate)
            Running.Remove(agentId, out process);

        if (process is not null)
            Kill(process);
        else if (TryGetPid(agentId) is { } pid)
            KillPid(pid);

        ClearPid(agentId);
        Changed?.Invoke();
    }

    /// <summary>Stops every hosted agent. Called when Agent Dock quits.</summary>
    public static void StopAll()
    {
        List<string> ids;
        lock (Gate)
            ids = Running.Keys.ToList();
        foreach (var id in ids.Concat(PidFiles().Select(Path.GetFileNameWithoutExtension).OfType<string>())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            Stop(id);
    }

    /// <summary>
    /// Env + argv additions that make DeepSeek open the launch folder: workspace env for the
    /// host plugin, <c>--no-open</c> so we control the browser URL (hash for the client
    /// plugin), and <c>--patch</c> for the shipped overlays when present.
    /// <c>--patch</c> is a top-level dsh flag and must come <em>before</em> the profile; a bare
    /// <c>web</c> argument is rewritten to <c>--profile web</c> so that ordering stays valid.
    /// </summary>
    private static void ApplyDeepSeekLaunch(ProcessStartInfo start, string folder, bool waifuBackgrounds)
    {
        start.Environment["AGENTDOCK_WORKSPACE"] = Path.GetFullPath(folder);
        if (waifuBackgrounds && FindWaifuPng() is { } png)
            start.Environment["AGENTDOCK_WAIFU_PATH"] = png;

        var args = start.ArgumentList;
        ExpandBareWebProfile(args);

        // Insert patches just after the script path, before --profile (repeatable --patch).
        var at = PatchInsertIndex(args);
        if (FindPluginPatch("agentdock-workspace.patch.yml") is { } workspace)
        {
            args.Insert(at, "--patch");
            args.Insert(at + 1, workspace);
            at += 2;
        }
        if (waifuBackgrounds && FindPluginPatch("agentdock-waifu.patch.yml") is { } waifu)
        {
            args.Insert(at, "--patch");
            args.Insert(at + 1, waifu);
        }

        // --no-open: we open the printed token URL ourselves, with agentdock_workspace on it.
        if (!HasFlag(args, "--no-open"))
            args.Add("--no-open");
    }

    /// <summary>
    /// <c>dsh web</c> is shorthand for <c>dsh --profile web</c>. Once we insert <c>--patch</c>
    /// before the profile, the bare token is no longer accepted — rewrite it in place.
    /// </summary>
    private static void ExpandBareWebProfile(IList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "web", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i > 0 && string.Equals(args[i - 1], "--profile", StringComparison.OrdinalIgnoreCase))
                return;
            args[i] = "--profile";
            args.Insert(i + 1, "web");
            return;
        }
    }

    /// <summary>
    /// Index after the node script (or after <c>cmd /c dsh</c>) so top-level flags sit before
    /// <c>--profile</c> / app args.
    /// </summary>
    private static int PatchInsertIndex(IList<string> args)
    {
        if (args.Count >= 1 && args[0].EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (args.Count >= 2 && string.Equals(args[0], "/c", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 0;
    }

    private static string? FindPluginPatch(string fileName)
    {
        // Published / Debug with CopyToOutputDirectory: next to the exe.
        var besideExe = Path.Combine(AppContext.BaseDirectory, "dsh-plugins", fileName);
        if (File.Exists(besideExe))
            return besideExe;

        // Fallback while iterating without a rebuild: the repo's tools\ from src\AgentDock\bin\Debug\net…\win-x64.
        try
        {
            var fromRepo = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tools", "dsh-plugins", fileName));
            if (File.Exists(fromRepo))
                return fromRepo;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return null;
    }

    private static string? FindWaifuPng()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "dsh-plugins", "agentdock-waifu", "waifu.png");
        if (File.Exists(besideExe))
            return besideExe;
        try
        {
            var fromRepo = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tools", "dsh-plugins",
                "agentdock-waifu", "waifu.png"));
            if (File.Exists(fromRepo))
                return fromRepo;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return null;
    }

    private static bool HasFlag(IEnumerable<string> args, string flag)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int? PortFromArgs(IList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(args[i + 1], out var port) && port is > 0 and < 65536)
                return port;
        }
        return null;
    }

    private static async Task OpenBrowserWhenReadyAsync(
        string folder, int port, Process process, Task<string> printedUrl)
    {
        var root = $"http://127.0.0.1:{port}/";
        // Prefer the URL dsh prints (includes the browser-trust token). Fall back to polling.
        string? baseUrl = null;
        try
        {
            var finished = await Task.WhenAny(printedUrl, Task.Delay(TimeSpan.FromSeconds(15)))
                .ConfigureAwait(false);
            if (finished == printedUrl && printedUrl.IsCompletedSuccessfully)
                baseUrl = printedUrl.Result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
        {
        }

        if (baseUrl is null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (process.HasExited)
                        return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                try
                {
                    using var response = await ReadyClient.GetAsync(root).ConfigureAwait(false);
                    // 401 without a token still means the listener is up.
                    if ((int)response.StatusCode < 500)
                    {
                        baseUrl = root;
                        break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
                {
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        if (baseUrl is null)
            return;

        var url = WithWorkspaceQuery(baseUrl, folder);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Don't fail the launch over a browser hand-off.
        }
    }

    /// <summary>Pull the first http(s) URL out of a dsh stdout line.</summary>
    private static string? TryFindWebUrl(string line)
    {
        var http = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        var https = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        var start = http < 0 ? https : https < 0 ? http : Math.Min(http, https);
        if (start < 0)
            return null;
        var end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
            end++;
        var url = line[start..end].TrimEnd('.', ',', ';', ')', ']');
        return url.Length > 0 ? url : null;
    }

    /// <summary>
    /// Put the folder in the URL <em>hash</em>. dsh's launch-token handshake answers with a
    /// 303 to <c>./</c>, which drops every query param; fragments survive that redirect, so
    /// the client plugin can still read <c>agentdock_workspace</c>.
    /// </summary>
    private static string WithWorkspaceQuery(string baseUrl, string folder)
    {
        var path = Uri.EscapeDataString(Path.GetFullPath(folder));
        var hash = "agentdock_workspace=" + path;
        var cut = baseUrl.IndexOf('#');
        if (cut >= 0)
            baseUrl = baseUrl[..cut];
        return baseUrl + "#" + hash;
    }

    private static ProcessStartInfo BuildStartInfo(string executable, string? arguments, string folder)
    {
        // npm's dsh.cmd runs: node "<npm>\node_modules\@deepseek-ai\dsh\lib\bin.js" …
        var npmDir = Path.GetDirectoryName(executable);
        var binJs = npmDir is null ? null
            : Path.Combine(npmDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        var node = PathResolver.Find("node");

        ProcessStartInfo start;
        if (node is not null && binJs is not null && File.Exists(binJs))
        {
            start = new ProcessStartInfo(node)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder
            };
            start.ArgumentList.Add(binJs);
        }
        else
        {
            // .cmd shims need cmd.exe when UseShellExecute is false.
            start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(executable);
        }

        if (arguments?.Trim() is { Length: > 0 } args)
        {
            foreach (var part in args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                start.ArgumentList.Add(part);
        }
        return start;
    }

    private static void OnExited(string agentId, Process process)
    {
        lock (Gate)
        {
            if (!Running.TryGetValue(agentId, out var current) || !ReferenceEquals(current, process))
                return;
            Running.Remove(agentId);
        }
        ClearPid(agentId);
        try { process.Dispose(); } catch (InvalidOperationException) { }
        Changed?.Invoke();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true); // node → children, or cmd → node → …
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or we can't signal it.
        }
        try { process.Dispose(); } catch (InvalidOperationException) { }
    }

    private static void KillPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
                                       or NotSupportedException)
        {
            // Gone already, or access denied.
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string PidPath(string agentId) =>
        Path.Combine(PidDirectory, $"{SanitizeFileName(agentId)}.pid");

    private static IEnumerable<string> PidFiles()
    {
        try
        {
            return Directory.Exists(PidDirectory)
                ? Directory.EnumerateFiles(PidDirectory, "*.pid")
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int? TryGetPid(string agentId)
    {
        try
        {
            var path = PidPath(agentId);
            if (!File.Exists(path))
                return null;
            return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WritePid(string agentId, int pid)
    {
        try
        {
            Directory.CreateDirectory(PidDirectory);
            File.WriteAllText(PidPath(agentId), pid.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tracking across restarts is best-effort; Stop in this session still works.
        }
    }

    private static void ClearPid(string agentId)
    {
        try
        {
            var path = PidPath(agentId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SanitizeFileName(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }

    /// <summary>
    /// Puts the agent's variables on the process. <c>%NAME%</c> values expand from the environment
    /// (same idea as the Terminal path, where cmd expands them).
    /// </summary>
    private static void ApplyEnvironment(ProcessStartInfo start, IReadOnlyDictionary<string, string>? env)
    {
        if (env is null)
            return;
        foreach (var (name, value) in env)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            start.Environment[name] = Environment.ExpandEnvironmentVariables(value);
        }
    }
}
