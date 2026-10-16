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

    /// <summary>
    /// Opened to sign in (the drawer's offer): close as soon as grok.com answers the usage request.
    /// Not when opened from settings, where the user may be signing out or switching accounts.
    /// </summary>
    private bool _closeWhenSignedIn;
    private bool _closing;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _signInWatch;

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

        // Signing in can finish without a full page load (a popup, or the app routing itself), so
        // look again every few seconds as well as after each load.
        _signInWatch = DispatcherQueue.CreateTimer();
        _signInWatch.Interval = TimeSpan.FromSeconds(3);
        _signInWatch.IsRepeating = true;
        _signInWatch.Tick += (_, _) => _ = ProbeSignInAsync();

        Closed += (_, _) =>
        {
            _signInWatch.Stop();
            _open = null;
            Finished?.Invoke();
        };
        _ = StartAsync();
    }

    /// <summary>
    /// Opens the window, or brings the open one forward. <paramref name="closeWhenSignedIn"/>: it's
    /// for signing in, so it closes itself once that's done.
    /// </summary>
    public static void Show(bool closeWhenSignedIn = false)
    {
        _open ??= new GrokSignInWindow();
        _open._closeWhenSignedIn = closeWhenSignedIn;
        if (closeWhenSignedIn)
            _open._signInWatch.Start();
        else
            _open._signInWatch.Stop();
        _open.Activate();
    }

    /// <summary>On grok.com, asks for usage the way the drawer does; an answer means signed in.</summary>
    private async Task ProbeSignInAsync()
    {
        if (!_closeWhenSignedIn || _closing || Browser.CoreWebView2 is not { } web
            || !Uri.TryCreate(web.Source, UriKind.Absolute, out var page)
            || !page.Host.Equals("grok.com", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            await web.ExecuteScriptAsync(GrokUsage.FetchScript);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The page went away mid-probe; the next one will try again.
        }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? message;
        try { message = args.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        // ReadReply saves the answer, so the drawer has the numbers straight away.
        if (!_closeWhenSignedIn || _closing || GrokUsage.ReadReply(message) is not { Usage: not null })
            return;

        _closing = true;
        _signInWatch.Stop();
        StatusLine.Text = "Signed in. Agent Dock can now show your Grok Build usage.";
        var close = DispatcherQueue.CreateTimer();
        close.Interval = TimeSpan.FromSeconds(1.2);
        close.IsRepeating = false;
        close.Tick += (_, _) => Close();
        close.Start();
    }

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(GrokUsage.DataFolder);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", GrokUsage.DataFolder, new CoreWebView2EnvironmentOptions());
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.WebMessageReceived += OnWebMessage;
            Browser.CoreWebView2.NavigationCompleted += (_, _) => _ = ProbeSignInAsync();
            Browser.Source = UsagePage;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine.Text = "The sign-in page couldn't open. The Microsoft Edge WebView2 runtime may be missing.";
        }
    }
}
