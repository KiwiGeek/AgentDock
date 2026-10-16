using AgentDock.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace AgentDock;

/// <summary>
/// grok.com in a window of Agent Dock's own, for Grok Build usage (<see cref="GrokUsage"/>). The user
/// signs in here, once, in a WebView2 profile that belongs to Agent Dock (<see cref="GrokUsage.DataFolder"/>):
/// no browser cookies or CLI tokens are read. It opens on grok.com's usage page, the same address
/// Grok Build links to when a limit is hit, so signing out or checking the numbers works here too.
/// </summary>
public sealed partial class GrokSignInWindow : Window
{
    private static readonly Uri UsagePage = new("https://grok.com/?_s=usage");

    private static GrokSignInWindow? _open;

    /// <summary>Raised when the window closes, so the next hover checks with the new sign-in.</summary>
    public static event Action? Finished;

    private GrokSignInWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AgentDock.ico"));

        // A normal window, sized for a sign-in page, centred on the monitor it opens on.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Interop.Win32.ScaleFor(Interop.Win32.MonitorFromPoint(
            new Interop.POINT { X = work.X + work.Width / 2, Y = work.Y + work.Height / 2 }, 2));
        var size = new SizeInt32((int)(560 * scale), (int)Math.Min(760 * scale, work.Height - 40 * scale));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2,
            size.Width, size.Height));

        Closed += (_, _) =>
        {
            _open = null;
            Finished?.Invoke();
        };
        _ = StartAsync();
    }

    /// <summary>Opens the window, or brings the open one forward.</summary>
    public static void Show()
    {
        _open ??= new GrokSignInWindow();
        _open.Activate();
    }

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(GrokUsage.DataFolder);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", GrokUsage.DataFolder, new CoreWebView2EnvironmentOptions());
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.Source = UsagePage;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine.Text = "The sign-in page couldn't open. The Microsoft Edge WebView2 runtime may be missing.";
        }
    }
}
