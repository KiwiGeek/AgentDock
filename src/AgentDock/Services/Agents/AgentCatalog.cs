using AgentDock.Models;
using AgentDock.Services.Launching;

namespace AgentDock.Services.Agents;

public static class AgentCatalog
{
    /// <summary>
    /// Resolves every configured agent. <paramref name="findDesktopApp"/> maps a desktop agent to its
    /// shell:AppsFolder id when it has neither an explicit path nor app id.
    /// </summary>
    public static List<AgentItem> Resolve(DockSettings settings, Func<AgentDefinition, string?> findDesktopApp) =>
        settings.Agents.Select(def => def.Kind == AgentKind.Cli ? ResolveCli(def) : ResolveDesktop(def, findDesktopApp))
            .ToList();

    private static AgentItem ResolveCli(AgentDefinition def) =>
        new(def) { ExecutablePath = PathResolver.Find(def.Command), IconSource = AgentIcons.Find(def) };

    private static AgentItem ResolveDesktop(AgentDefinition def, Func<AgentDefinition, string?> findDesktopApp)
    {
        if (!string.IsNullOrWhiteSpace(def.Path))
        {
            var path = Environment.ExpandEnvironmentVariables(def.Path);
            var exe = File.Exists(path) ? path : null;
            return new AgentItem(def) { ExecutablePath = exe, IconSource = AgentIcons.Find(def) ?? exe };
        }

        // Without a configured or compiled-in icon, use the one the Start menu shows.
        var appId = !string.IsNullOrWhiteSpace(def.AppId) ? def.AppId : findDesktopApp(def);
        return new AgentItem(def)
        {
            AppId = appId,
            IconSource = AgentIcons.Find(def) ?? (appId is not null ? $@"shell:AppsFolder\{appId}" : null)
        };
    }

    /// <summary>Turns presets into rows, using the already-resolved <paramref name="agents"/> they refer to.</summary>
    public static List<AgentItem> ResolvePresets(IEnumerable<Preset> presets, IReadOnlyList<AgentItem> agents) =>
        presets.Select(preset => new AgentItem(preset,
                agents.FirstOrDefault(a => a.Definition.Id.Equals(preset.Agent, StringComparison.OrdinalIgnoreCase))
                // Unknown id: a placeholder that shows as unavailable, so the preset can still be removed.
                ?? new AgentItem(new AgentDefinition { Id = preset.Agent, Name = preset.Agent })))
            .ToList();

    public static Func<AgentDefinition, string?> FromStartMenu(IReadOnlyDictionary<string, string> apps) =>
        def => (def.StartMenuNames ?? []).Select(name => apps.GetValueOrDefault(name)).FirstOrDefault(id => id is not null);
}
