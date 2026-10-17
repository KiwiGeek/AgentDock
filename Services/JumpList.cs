using System.Runtime.InteropServices;
using AgentDock.Interop;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// The taskbar icon's right-click menu. WinUI has no jump list API for unpackaged apps, so this
/// talks to the shell's ICustomDestinationList directly.
/// </summary>
internal static class JumpList
{
    public static void Update(IEnumerable<AgentItem> presets, IEnumerable<AgentItem> agents, string folder)
    {
        try
        {
            var self = Environment.ProcessPath!;
            Check(ShellCom.CoCreateInstance(ShellCom.CLSID_DestinationList, 0, ShellCom.CLSCTX_INPROC_SERVER,
                ShellCom.IID_ICustomDestinationList, out ICustomDestinationList list));

            list.BeginList(out _, ShellCom.IID_IObjectArray, out var removed);
            if (removed != 0)
                Marshal.Release(removed);

            var availablePresets = presets.Where(p => p.IsAvailable && p.Preset is not null).ToList();
            if (availablePresets.Count > 0)
            {
                var items = NewCollection();
                foreach (var preset in availablePresets)
                {
                    var icon = AgentIcons.JumpListIconFor(preset.IconSource) ?? self;
                    items.AddObject(NewLink(self, $"--preset \"{preset.Name}\"", preset.Name,
                        $"Open {preset.Definition.Name} in {preset.Preset!.Folder}", icon));
                }
                list.AppendCategory("Presets", items);
            }

            var available = agents.Where(a => a.IsAvailable).ToList();
            foreach (var group in available.GroupBy(a => a.IsCli))
            {
                var items = NewCollection();
                foreach (var agent in group)
                {
                    var icon = AgentIcons.JumpListIconFor(agent.IconSource) ?? self;
                    var description = agent.TakesFolder ? $"Open {agent.Name} in {folder}" : $"Open {agent.Name}";
                    items.AddObject(NewLink(self, $"--launch \"{agent.Definition.Id}\"", agent.Name, description, icon));
                }
                // Fails harmlessly if the user has turned off "recently opened items" in Settings.
                list.AppendCategory(group.Key ? "Command line" : "Desktop apps", items);
            }

            var tasks = NewCollection();
            tasks.AddObject(NewLink(self, "--quit", "Quit Agent Dock", "Stop the background instance", self));
            list.AddUserTasks(tasks);

            list.CommitList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The jump list is a nicety; never fail the menu over it.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static IObjectCollection NewCollection()
    {
        Check(ShellCom.CoCreateInstance(ShellCom.CLSID_EnumerableObjectCollection, 0, ShellCom.CLSCTX_INPROC_SERVER,
            ShellCom.IID_IObjectCollection, out IObjectCollection collection));
        return collection;
    }

    private static IShellLinkW NewLink(string path, string arguments, string title, string description, string iconPath)
    {
        Check(ShellCom.CoCreateInstance(ShellCom.CLSID_ShellLink, 0, ShellCom.CLSCTX_INPROC_SERVER,
            ShellCom.IID_IShellLinkW, out IShellLinkW link));
        link.SetPath(path);
        link.SetArguments(arguments);
        link.SetDescription(description);
        link.SetIconLocation(iconPath, 0);

        // Jump list entries take their visible text from the link's Title property.
        var titleValue = new PROPVARIANT { vt = ShellCom.VT_LPWSTR, value = Marshal.StringToCoTaskMemUni(title) };
        try
        {
            var store = (IPropertyStore)link;
            store.SetValue(ShellCom.PKEY_Title, titleValue);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(titleValue.value);
        }
        return link;
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
}
