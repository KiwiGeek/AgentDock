namespace AgentDock.Interop;

/// <summary>Reads the Start menu's "All apps" list (shell:AppsFolder): display name → launch id.</summary>
internal static class StartMenuApps
{
    /// <summary>Call from an STA thread.</summary>
    public static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (ShellCom.SHCreateItemFromParsingName("shell:AppsFolder", 0, ShellCom.IID_IShellItem, out var folder) != 0)
                return result;

            folder.BindToHandler(0, ShellCom.BHID_EnumItems, ShellCom.IID_IEnumShellItems, out var items);
            while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1 && item is not null)
            {
                var name = ShellCom.DisplayName(item, ShellCom.SIGDN_NORMALDISPLAY);
                var id = ShellCom.DisplayName(item, ShellCom.SIGDN_PARENTRELATIVEPARSING);
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    result.TryAdd(name, id);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Return whatever was read.
        }
        return result;
    }

    /// <summary>Runs work on a dedicated STA thread, which the shell APIs require.</summary>
    public static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "Shell worker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
