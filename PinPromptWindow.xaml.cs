using AgentDock.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace AgentDock;

/// <summary>
/// Shown at startup when Agent Dock isn't pinned, since the taskbar icon is the only way to open
/// it. Windows doesn't let unpackaged apps pin themselves, so this asks the user to pin it from
/// this window's own taskbar button, and closes by itself once the pin appears.
/// </summary>
public sealed partial class PinPromptWindow : Window
{
    private const double WidthDip = 440;

    private readonly TaskCompletionSource<bool> _result = new();
    private readonly DispatcherQueueTimer _pinWatch;
    private readonly string _exePath = Environment.ProcessPath!;

    public PinPromptWindow()
    {
        InitializeComponent();

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentDock.ico");
        AppWindow.SetIcon(icon);
        AppIcon.Source = new BitmapImage(new Uri(icon));

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        AppWindow.SetPresenter(presenter);

        // Closing the window counts as declining.
        Closed += (_, _) => _result.TrySetResult(false);
        Root.Loaded += (_, _) => FitAndCentre();

        // Polling a handful of shortcuts once a second is cheaper and simpler than a file watcher.
        _pinWatch = DispatcherQueue.CreateTimer();
        _pinWatch.Interval = TimeSpan.FromSeconds(1);
        _pinWatch.Tick += (_, _) =>
        {
            if (TaskbarPins.IsPinned(_exePath))
                Finish(pinned: true);
        };
    }

    /// <summary>
    /// True if Agent Dock is pinned (already, or after asking); false if the user declined.
    /// Debug builds skip the check unless AGENTDOCK_PIN_CHECK=1, so bin\Debug doesn't need its own pin.
    /// </summary>
    public static Task<bool> EnsurePinnedAsync()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("AGENTDOCK_PIN_CHECK") != "1")
            return Task.FromResult(true);
#endif
        if (TaskbarPins.IsPinned(Environment.ProcessPath!))
            return Task.FromResult(true);

        var prompt = new PinPromptWindow();
        prompt._pinWatch.Start();
        prompt.Activate();
        return prompt._result.Task;
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Finish(pinned: false);

    private void Finish(bool pinned)
    {
        _pinWatch.Stop();
        _result.TrySetResult(pinned);
        Close();
    }

    /// <summary>Sizes the window to its content and centres it on the monitor it opened on.</summary>
    private void FitAndCentre()
    {
        var scale = Root.XamlRoot.RasterizationScale;
        Root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));

        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Ceiling(WidthDip * scale),
            (int)Math.Ceiling(Root.DesiredSize.Height * scale)));

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - AppWindow.Size.Width) / 2,
            work.Y + (work.Height - AppWindow.Size.Height) / 2));
    }
}
