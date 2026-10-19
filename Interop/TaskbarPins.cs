namespace AgentDock.Interop;

/// <summary>
/// Finds out whether an exe is pinned to the taskbar. Windows has no public query for unpackaged
/// apps, but every taskbar pin of a desktop app is a shortcut in the user's "User Pinned\TaskBar"
/// folder, so this reads those shortcuts' targets.
/// </summary>
internal static class TaskbarPins
{
    private static string PinFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>Must be called on an STA thread (the shell link object needs one).</summary>
    public static bool IsPinned(string exePath)
    {
        try
        {
            if (!Directory.Exists(PinFolder))
                return false;

            return Directory.EnumerateFiles(PinFolder, "*.lnk")
                .Select(TargetOf)
                .Any(target => string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The pin shortcuts that start this exe (normally one). Must be called on an STA thread.</summary>
    public static List<string> ShortcutsTo(string exePath)
    {
        try
        {
            return Directory.Exists(PinFolder)
                ? Directory.EnumerateFiles(PinFolder, "*.lnk")
                    .Where(lnk => string.Equals(TargetOf(lnk), exePath, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Sets a pin's icon: an .ico file, or empty for its target's own icon. Everything else in the
    /// shortcut is kept (it's loaded, changed and saved in place), so the pin itself stays.
    /// </summary>
    public static bool SetIcon(string shortcut, string iconPath)
    {
        try
        {
            Check(ShellCom.CoCreateInstance(ShellCom.CLSID_ShellLink, 0, ShellCom.CLSCTX_INPROC_SERVER,
                ShellCom.IID_IShellLinkW, out IShellLinkW link));
            var file = (IPersistFile)link;
            file.Load(shortcut, ShellCom.STGM_READWRITE);
            link.SetIconLocation(iconPath, 0);
            file.Save(null, true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static unsafe string? TargetOf(string shortcut)
    {
        try
        {
            Check(ShellCom.CoCreateInstance(ShellCom.CLSID_ShellLink, 0, ShellCom.CLSCTX_INPROC_SERVER,
                ShellCom.IID_IShellLinkW, out IShellLinkW link));
            ((IPersistFile)link).Load(shortcut, ShellCom.STGM_READ);

            var buffer = new char[1024];
            fixed (char* p = buffer)
                link.GetPath((nint)p, buffer.Length, 0, 0);
            var target = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length);
            return target.Length > 0 ? Path.GetFullPath(target) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unreadable or broken shortcut just isn't ours.
            return null;
        }
    }

    private static void Check(int hr) => System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
}
