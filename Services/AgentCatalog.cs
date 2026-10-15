using AgentDock.Models;

namespace AgentDock.Services;

public static class AgentCatalog
{
    /// <summary>
    /// Resolves every configured agent. <paramref name="findDesktopApp"/> maps a desktop agent to its
    /// shell:AppsFolder id when it has neither an explicit path nor app id.
    /// </summary>
    public static List<AgentItem> Resolve(DockSettings settings, Func<AgentDefinition, string?> findDesktopApp) =>
        settings.Agents.Select(def => def.Kind == AgentKind.Cli
                ? new AgentItem(def) { ExecutablePath = PathResolver.Find(def.Command) }
                : ResolveDesktop(def, findDesktopApp))
            .ToList();

    private static AgentItem ResolveDesktop(AgentDefinition def, Func<AgentDefinition, string?> findDesktopApp)
    {
        if (!string.IsNullOrWhiteSpace(def.Path))
        {
            var path = Environment.ExpandEnvironmentVariables(def.Path);
            return new AgentItem(def) { ExecutablePath = File.Exists(path) ? path : null };
        }

        if (!string.IsNullOrWhiteSpace(def.AppId))
            return new AgentItem(def) { AppId = def.AppId };

        return new AgentItem(def) { AppId = findDesktopApp(def) };
    }

    public static Func<AgentDefinition, string?> FromStartMenu(IReadOnlyDictionary<string, string> apps) =>
        def => (def.StartMenuNames ?? []).Select(name => apps.GetValueOrDefault(name)).FirstOrDefault(id => id is not null);
}
