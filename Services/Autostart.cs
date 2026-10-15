using Microsoft.Win32;

namespace AgentDock.Services;

/// <summary>
/// Keeps the HKCU Run entry in sync with the startWithWindows setting, so the background instance
/// is already warm the first time you click after signing in.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentDock";

    public static void Apply(bool enabled)
    {
#if DEBUG
        // Don't register bin\Debug builds.
        return;
#else
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            var desired = $"\"{Environment.ProcessPath}\" --background";
            var current = key.GetValue(ValueName) as string;

            if (enabled && !string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, desired);
            else if (!enabled && current is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Policy may block it; the app still works, just cold on first click.
        }
#endif
    }
}
