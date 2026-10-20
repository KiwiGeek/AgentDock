using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AgentDock.Interop;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using FolderPicker = Microsoft.Windows.Storage.Pickers.FolderPicker;
using Launcher = AgentDock.Services.Launcher;

namespace AgentDock;

/// <summary>
/// The flyout. Built once at startup, then shown and hidden for the life of the process.
/// It opens next to wherever the taskbar was clicked and hides as soon as it loses focus,
/// the same way Windows 11's own flyouts behave.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    /// <summary>The banner plus the list itself (380). Matches ListColumn and FooterContent in the XAML.</summary>
    private const double ListWidthDip = 412;
    private const double BannerWidthDip = 32;
    /// <summary>How far the window grows when the drawer is open.</summary>
    private const double DrawerWidthDip = 240;
    private const double HoverBackdropOpacity = 0.3;
    private const double MaxHeightDip = 640;
    /// <summary>The shortest the user can drag the list to.</summary>
    private const double MinHeightDip = 240;

    /// <summary>A click on the taskbar icon hides the open flyout on mouse-down and relaunches on mouse-up; ignore that relaunch.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ShellRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _store;
    private readonly ShellIcons _icons = new();
    private readonly ObservableCollection<AgentItem> _presets = [];
    private readonly ObservableCollection<AgentItem> _cli = [];
    private readonly ObservableCollection<AgentItem> _desktop = [];
    private readonly ObservableCollection<FolderItem> _folders = [];
    private readonly Stopwatch _sinceHide = new();
    private readonly Stopwatch _sinceShellRefresh = new();
    private readonly TaskCompletionSource _rootLoaded = new();
    private readonly nint _hwnd;

    private DockSettings _settings = new();
    private DockState _state = new();
    private List<Preset> _presetList = [];
    private string? _presetsError;
    /// <summary>Every resolved agent, shown or not, from the last <see cref="ShowAgents"/>; presets refer to these.</summary>
    private List<AgentItem> _agents = [];
    private POINT _anchor;
    private int _generation;
    private bool _prepared;
    private bool _pendingShow;
    private string? _pendingStatus;
    private bool _keepOpen;
    private bool _hiddenByDeactivation;
    private bool _loadingFolders;
    /// <summary>Why agents.json couldn't be read this time, if it couldn't.</summary>
    private string? _settingsError;

    public FlyoutWindow(SettingsStore store)
    {
        _store = store;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Borderless, fixed-size, always on top, and absent from the taskbar and Alt+Tab.
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        // Windows would otherwise animate each resize and the list slides under the frame while the drawer grows.
        var transitionsDisabled = 1;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, ref transitionsDisabled, sizeof(int));
        // The drawer's width is animated. Follow that value directly: the column can be wider than
        // the window for a moment, so its arranged size isn't a reliable signal.
        DrawerHost.RegisterPropertyChangedCallback(FrameworkElement.WidthProperty, OnDrawerWidthChanged);
        AppWindow.Closing += (_, e) =>
        {
            // Alt+F4 just hides it; only quitting the app really closes it.
            if (!App.IsExiting)
            {
                e.Cancel = true;
                HideFlyout();
            }
        };

        Activated += OnActivated;
        Root.Loaded += (_, _) => _rootLoaded.TrySetResult();
        // Resizing the height changes how a picture needs to be scaled to cover the flyout.
        Root.SizeChanged += (_, _) =>
        {
            if (_layers is not null)
                LayoutBackdrop();
        };

        PresetList.ItemsSource = _presets;
        CliList.ItemsSource = _cli;
        DesktopList.ItemsSource = _desktop;
        FolderBox.ItemsSource = _folders;
    }

    public static string DefaultFolder()
    {
        var repos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        return Directory.Exists(repos) ? repos : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string CurrentFolder => (FolderBox.SelectedItem as FolderItem)?.Path ?? DefaultFolder();
    private IEnumerable<AgentItem> AllItems => _presets.Concat(_cli).Concat(_desktop);
    private ListView[] Lists => [PresetList, CliList, DesktopList];

    // ---------- Show / hide ----------

    /// <summary>
    /// Loads data and icons and renders the window once off-screen, so the first real show
    /// doesn't pay for XAML, theme resources, layout or JIT.
    /// </summary>
    public async Task PrepareAsync()
    {
        ReloadData();
        foreach (var item in AllItems.Where(i => i.IconSource is not null).ToList())
            item.Icon = await _icons.LoadFromDiskAsync(item.IconSource!);

        AppWindow.Move(new PointInt32(-20000, -20000));
        AppWindow.Show(activateWindow: false);
        await Task.WhenAny(_rootLoaded.Task, Task.Delay(2000));
        AppWindow.Hide();

        _prepared = true;
        _ = RefreshFromShellAsync(_generation);

        if (_pendingShow)
        {
            _pendingShow = false;
            ShowFlyout(_pendingStatus);
        }
    }

    /// <summary>Shows the flyout at the cursor. <paramref name="status"/> is an optional message for the footer.</summary>
    public void ShowFlyout(string? status = null)
    {
        if (!_prepared)
        {
            _pendingShow = true;
            _pendingStatus = status;
            return;
        }

        if (AppWindow.IsVisible)
        {
            if (status is not null)
                SetStatus(status);
            BringToFront();
            return;
        }

        if (status is null && _hiddenByDeactivation && _sinceHide.Elapsed < ReopenGuard)
            return; // the same click that just closed it: behave as a toggle

        _generation++;
        Win32.GetCursorPos(out _anchor);
        ReloadData();
        if (status is not null)
            StatusText.Text = status;

        PlaceWindow();
        AppWindow.Show(activateWindow: true);
        BringToFront();
        PlayOpenAnimation();
        SyncHearts();
        DispatcherQueue.TryEnqueue(FocusFirstAgent);
        // The overview opens beside the list once it's placed, and its checks start meanwhile.
        if (_state.ShowUsageSummary)
            DispatcherQueue.TryEnqueue(() =>
            {
                StartSummaryChecks();
                ShowSummary();
            });

        // A desktop agent in the list that wasn't found may have been installed since: look every time.
        if (!_sinceShellRefresh.IsRunning || _sinceShellRefresh.Elapsed > ShellRefreshInterval
            || AllItems.Any(a => a.Icon is null && a.IconSource is not null)
            || _agents.Any(a => !a.IsCli && !a.IsAgentAvailable))
            _ = RefreshFromShellAsync(_generation);
    }

    public void HideFlyout(bool byDeactivation = false)
    {
        if (!AppWindow.IsVisible)
            return;
        _hiddenByDeactivation = byDeactivation;
        _sinceHide.Restart();
        AppWindow.Hide();
        SyncHearts();
        // Don't reopen with the last hover's image or the drawer showing.
        FadeBackdrop(null);
        CloseDrawer(animate: false);
    }

    private void BringToFront()
    {
        Win32.SetForegroundWindow(_hwnd);
        Activate();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_keepOpen)
            HideFlyout(byDeactivation: true);
    }

    private void UpdateJumpList(string? folder = null) =>
        JumpList.Update(_presets, _cli.Concat(_desktop), folder ?? CurrentFolder, TaskbarIcon.FileFor(_state.TaskbarIcon));

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        if (AppWindow.IsVisible)
            PlaceWindow(); // a wrapped message may need more height
    }

    private void FocusFirstAgent()
    {
        var first = Lists.FirstOrDefault(l => l.Items.Count > 0)?.ContainerFromIndex(0) as ListViewItem;
        first?.Focus(FocusState.Programmatic);
    }

    // ---------- Banner ----------

    private const double BannerInsetDip = 14;
    /// <summary>Matches the fill's Opacity in the XAML.</summary>
    private const double BannerFillOpacity = 0.7;

    private void Banner_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutBanner();

    /// <summary>The heart goes with the waifu pictures: shown when they're on.</summary>
    private void UpdateBannerHeart()
    {
        BannerHeart.Visibility = _state.ShowHoverBackgrounds ? Visibility.Visible : Visibility.Collapsed;
        LayoutBanner();
        SyncHearts();
    }

    // Banner colours: the accent by default, the highlighted agent's colour while one is.

    private readonly Dictionary<string, Windows.UI.Color?> _themeColors = new(StringComparer.OrdinalIgnoreCase);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bannerResetTimer;
    private Storyboard? _bannerColorFade;
    /// <summary>The picture whose colour the banner should show; a late result for another one is dropped.</summary>
    private string? _bannerColorSource;

    /// <summary>
    /// The agent's picture when it has one, otherwise its logo. The picture still supplies its curated
    /// hover colour when backgrounds are hidden, so muted monochrome logos do not take over the banner.
    /// </summary>
    private string? ThemeSourceFor(AgentItem item) =>
        item.BackgroundSource is { } background ? background : item.IconSource;

    /// <summary>
    /// The agent's "color" setting, else the colour of its picture (or logo when no picture exists):
    /// hand-picked for built-in pictures, worked out from the image otherwise.
    /// </summary>
    private async void ColorBannerFor(AgentItem? item)
    {
        _bannerResetTimer?.Stop();
        if (item is not null && ThemeColors.Parse(item.Definition.Color) is { } configured)
        {
            _bannerColorSource = item.Definition.Id;
            SetBannerColors(configured);
            return;
        }

        // Do not make this depend on the image-source lookup: the built-in backgrounds have curated
        // colours, and that identity remains available even while the pictures themselves are hidden.
        if (item is not null)
        {
            var knownId = item.Definition.Catalog ?? item.Definition.Id;
            if (ThemeColors.ForBundledBackgroundName(knownId) is { } knownColor)
            {
                _bannerColorSource = knownId;
                SetBannerColors(knownColor);
                return;
            }
        }

        var source = item is not null ? ThemeSourceFor(item) : null;
        _bannerColorSource = source;
        if (source is null)
        {
            SetBannerColors(null);
            return;
        }

        if (AgentIcons.IsBundledBackground(source) && ThemeColors.ForBundledBackground(source) is { } picked)
        {
            SetBannerColors(picked);
            return;
        }

        if (!_themeColors.TryGetValue(source, out var color))
        {
            color = await LoadThemeColorAsync(source);
            _themeColors[source] = color;
            if (!string.Equals(_bannerColorSource, source, StringComparison.OrdinalIgnoreCase))
                return;
        }
        SetBannerColors(color);
    }

    private static async Task<Windows.UI.Color?> LoadThemeColorAsync(string source)
    {
        try
        {
            using var stream = AgentIcons.IsBundledBackground(source)
                ? AgentIcons.OpenBundledBackground(source)?.AsRandomAccessStream()
                : await ShellIcons.OpenAsync(source);
            return stream is null ? null : await ThemeColors.FromStreamAsync(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // the accent colour, then
        }
    }

    /// <summary>
    /// Back to the accent colour shortly after the pointer leaves, unless it lands on another agent (or
    /// the drawer) first, so moving between rows doesn't flash the accent.
    /// </summary>
    private void ScheduleBannerReset()
    {
        // A pop-up (a menu, a confirmation) took the pointer; the drawer and the agent's colour stay.
        if (_popupsOpen > 0)
            return;
        if (_bannerResetTimer is null)
        {
            _bannerResetTimer = DispatcherQueue.CreateTimer();
            _bannerResetTimer.Interval = TimeSpan.FromMilliseconds(250);
            _bannerResetTimer.IsRepeating = false;
            _bannerResetTimer.Tick += (_, _) =>
            {
                _bannerColorSource = null;
                SetBannerColors(null);
            };
        }
        _bannerResetTimer.Start();
    }

    /// <summary>
    /// Fades the banner to an agent's colour, or to the accent colours for the current theme when
    /// <paramref name="agentColor"/> is null.
    /// </summary>
    private void SetBannerColors(Windows.UI.Color? agentColor, bool animate = true)
    {
        Windows.UI.Color bottom, top;
        if (agentColor is { } color)
        {
            var tamed = ThemeColors.Tame(color);
            (bottom, top) = (ThemeColors.Shade(tamed, 0.15), ThemeColors.Shade(tamed, -0.2));
        }
        else
        {
            var light = Banner.ActualTheme == ElementTheme.Light;
            bottom = AccentColor(light ? "SystemAccentColorDark2" : "SystemAccentColorLight2");
            top = AccentColor(light ? "SystemAccentColorDark1" : "SystemAccentColorLight1");
        }
        // The fill is translucent, so pick the text colour against what shows: the fill over the flyout.
        var behind = Banner.ActualTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3)
            : Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20);
        var text = ThemeColors.TextOn(ThemeColors.Over(bottom, behind, BannerFillOpacity));

        _bannerColorFade?.Stop();
        if (!animate || !AppWindow.IsVisible || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            BannerStopBottom.Color = bottom;
            BannerStopTop.Color = top;
            BannerTextBrush.Color = text;
            return;
        }

        var storyboard = new Storyboard();
        Add(BannerStopBottom, bottom);
        Add(BannerStopTop, top);
        Add(BannerTextBrush, text);
        _bannerColorFade = storyboard;
        storyboard.Begin();

        void Add(DependencyObject target, Windows.UI.Color to)
        {
            // Gradient stops and brushes aren't composition properties, so these run on the UI thread.
            var fade = new ColorAnimation
            {
                To = to, Duration = new Duration(TimeSpan.FromMilliseconds(450)), EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Color");
            storyboard.Children.Add(fade);
        }
    }

    private static Windows.UI.Color AccentColor(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Windows.UI.Color color
            ? color
            : Windows.UI.Color.FromArgb(255, 0, 120, 212);

    // Hearts: the banner's heart beats, and small hearts float up from it.

    private Storyboard? _heartbeat;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _floatTimer;
    private readonly Random _random = new();
    private const double HeartbeatSeconds = 1.6;

    /// <summary>Runs the hearts only while they can be seen.</summary>
    private void SyncHearts()
    {
        var run = AppWindow.IsVisible && BannerHeart.Visibility == Visibility.Visible
                  && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        if (run)
            StartHearts();
        else
            StopHearts();
    }

    private void StartHearts()
    {
        if (_heartbeat is null)
        {
            _heartbeat = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            foreach (var property in new[] { "ScaleX", "ScaleY" })
            {
                // Two quick beats, then a rest.
                var beat = new DoubleAnimationUsingKeyFrames();
                foreach (var (seconds, scale) in new[] { (0.0, 1.0), (0.12, 1.3), (0.24, 1.0), (0.36, 1.2), (0.52, 1.0), (HeartbeatSeconds, 1.0) })
                    beat.KeyFrames.Add(new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), Value = scale,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
                Storyboard.SetTarget(beat, BannerHeartTransform);
                Storyboard.SetTargetProperty(beat, property);
                _heartbeat.Children.Add(beat);
            }
        }
        _heartbeat.Begin();

        if (_floatTimer is null)
        {
            // One-shot and rescheduled each time, so the gaps between hearts vary (owner's request, 2026-10-19).
            _floatTimer = DispatcherQueue.CreateTimer();
            _floatTimer.IsRepeating = false;
            _floatTimer.Tick += (_, _) =>
            {
                ReleaseHearts();
                ScheduleNextHeart();
            };
        }
        SeedHearts();
        ScheduleNextHeart();
    }

    /// <summary>
    /// Opening the flyout starts with a few hearts already on their way up, at different heights,
    /// instead of an empty banner that fills over several seconds (owner's request, 2026-10-19).
    /// </summary>
    private void SeedHearts()
    {
        if (BannerCanvas.Children.OfType<TextBlock>().Any(t => t.Tag is "float"))
            return;
        // This runs just after the window is placed, before layout has caught up with its new height,
        // so the big heart may still be where the last show left it; seeds from there sat on the name.
        Banner.UpdateLayout();
        LayoutBanner();
        // Spread over the trip, one per slice, so they don't bunch up.
        var count = 3 + _random.Next(3);
        for (var i = 0; i < count; i++)
            FloatHeart(TimeSpan.Zero, (i + 0.15 + _random.NextDouble() * 0.7) / count * 0.9);
    }

    private void ScheduleNextHeart()
    {
        if (_floatTimer is null)
            return;
        _floatTimer.Interval = TimeSpan.FromSeconds(0.4 + _random.NextDouble() * 2.2);
        _floatTimer.Start();
    }

    /// <summary>Usually one heart, now and then a little burst of two or three.</summary>
    private void ReleaseHearts()
    {
        var count = _random.NextDouble() switch { < 0.7 => 1, < 0.92 => 2, _ => 3 };
        for (var i = 0; i < count; i++)
            FloatHeart(TimeSpan.FromSeconds(i * (0.15 + _random.NextDouble() * 0.35)));
    }

    private void StopHearts()
    {
        _heartbeat?.Stop();
        _floatTimer?.Stop();
        foreach (var floater in BannerCanvas.Children.OfType<TextBlock>().Where(t => t.Tag is "float").ToList())
            BannerCanvas.Children.Remove(floater);
    }

    /// <summary>
    /// What floats up, with weights: mostly hearts, now and then a kiss, a blossom or sparkles
    /// (owner's request, 2026-10-19). Faces were left out: at 7–14 DIP they read as clutter.
    /// </summary>
    private static readonly (string Text, bool Sideways, int Weight)[] Floaters =
    [
        ("❤️", true, 4), ("💕", true, 3), ("💖", true, 3), ("💗", true, 3), ("💞", true, 2), ("🩷", true, 3),
        ("💋", false, 1), ("🌸", false, 1), ("✨", false, 1)
    ];

    private static readonly int FloaterWeights = Floaters.Sum(f => f.Weight);

    private (string Text, bool Sideways) PickFloater()
    {
        var roll = _random.Next(FloaterWeights);
        foreach (var (text, sideways, weight) in Floaters)
        {
            if (roll < weight)
                return (text, sideways);
            roll -= weight;
        }
        return (BannerHeart.Text, true);
    }

    /// <summary>At most this many floating hearts at once, so a burst on a tall flyout doesn't pile up.</summary>
    private const int MaxFloatingHearts = 8;

    /// <summary>
    /// A small sideways heart that drifts from the big one all the way to the top of the banner, and
    /// fades out there. Each is a little different (size, tilt, wobble, speed, brightness), and they
    /// rise at a steady speed, so a trip takes several seconds and a few are usually on the way up.
    /// <paramref name="progress"/> (0–1) starts it that far through its trip.
    /// </summary>
    private void FloatHeart(TimeSpan delay, double progress = 0)
    {
        if (BannerCanvas.Children.OfType<TextBlock>().Count(t => t.Tag is "float") >= MaxFloatingHearts)
            return;
        var heartTop = Canvas.GetTop(BannerHeart) + BannerHeart.DesiredSize.Height / 2 - BannerHeart.DesiredSize.Width / 2;
        var room = heartTop - 4;
        if (room < 30)
            return;

        var (text, sideways) = PickFloater();
        var tilt = (_random.NextDouble() - 0.5) * 36; // ±18° off sideways
        // Hearts lie sideways like the banner's; a kiss, a blossom or sparkles look odd that way, so stay nearly upright.
        var angle = sideways ? -90 + tilt : tilt * 0.5;
        var transform = new Microsoft.UI.Xaml.Media.CompositeTransform { Rotation = angle };
        var heart = new TextBlock
        {
            Text = text, FontSize = 7 + _random.NextDouble() * 7, Tag = "float", Opacity = 0,
            IsHitTestVisible = false, RenderTransform = transform
        };
        heart.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = heart.DesiredSize;
        transform.CenterX = size.Width / 2;
        transform.CenterY = size.Height / 2;
        // Turned sideways, its height is what takes up the banner's width; keep the wobble inside it.
        var slack = Math.Max(0, (BannerWidthDip - (sideways ? size.Height : size.Width)) / 2 - 1);
        var start = (_random.NextDouble() - 0.5) * slack;
        Canvas.SetLeft(heart, BannerWidthDip / 2 - size.Width / 2 + start);
        Canvas.SetTop(heart, heartTop - size.Height / 2);
        BannerCanvas.Children.Add(heart);

        var distance = room - size.Width / 2;
        var duration = TimeSpan.FromSeconds(distance / (55 + _random.NextDouble() * 55)); // 55–110 DIP/s
        var storyboard = new Storyboard { BeginTime = delay };

        var rise = new DoubleAnimation
        {
            To = -distance, Duration = duration,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } // drifts off slowly, then floats
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "TranslateY");

        var wobble = Math.Min(slack - Math.Abs(start), 1.5 + _random.NextDouble() * 3.5);
        var sway = new DoubleAnimation
        {
            From = -wobble, To = wobble, Duration = TimeSpan.FromSeconds(0.5 + _random.NextDouble() * 0.9),
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(duration),
            BeginTime = TimeSpan.FromSeconds(_random.NextDouble() * 0.4),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(sway, transform);
        Storyboard.SetTargetProperty(sway, "TranslateX");

        var rock = new DoubleAnimation
        {
            From = angle - 8, To = angle + 8, Duration = TimeSpan.FromSeconds(0.7 + _random.NextDouble() * 0.8),
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(duration),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(rock, transform);
        Storyboard.SetTargetProperty(rock, "Rotation");

        var peak = 0.55 + _random.NextDouble() * 0.4;
        var total = duration.TotalSeconds;
        var fade = new DoubleAnimationUsingKeyFrames();
        foreach (var (seconds, opacity) in new[] { (0.0, 0.0), (0.4, peak), (total * 0.75, peak * 0.85), (total, 0.0) })
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), Value = opacity });
        Storyboard.SetTarget(fade, heart);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(rise);
        storyboard.Children.Add(sway);
        storyboard.Children.Add(rock);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => BannerCanvas.Children.Remove(heart);
        storyboard.Begin();
        if (progress > 0)
            storyboard.Seek(TimeSpan.FromSeconds(total * progress));
    }

    /// <summary>
    /// Turns the banner text to read bottom to top, ending <see cref="BannerInsetDip"/> above the
    /// bottom edge, with the heart upright above it.
    /// </summary>
    private void LayoutBanner()
    {
        var height = Banner.ActualHeight;
        if (height <= 0)
            return;

        var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        BannerText.Measure(unbounded);
        var length = BannerText.DesiredSize.Width;
        var thickness = BannerText.DesiredSize.Height;
        var bottom = height - BannerInsetDip;

        // Rotating -90° about the top-left corner puts the text in x 0..thickness, y -length..0.
        BannerText.RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform
        {
            Rotation = -90,
            TranslateX = Math.Round((BannerWidthDip - thickness) / 2),
            TranslateY = Math.Round(bottom)
        };

        // The heart turns about its centre, so its (unturned) width is how much banner it takes up.
        BannerHeart.Measure(unbounded);
        var heart = BannerHeart.DesiredSize;
        BannerHeartTransform.CenterX = heart.Width / 2;
        BannerHeartTransform.CenterY = heart.Height / 2;
        var centreY = bottom - length - 8 - heart.Width / 2;
        Canvas.SetLeft(BannerHeart, Math.Round(BannerWidthDip / 2 - heart.Width / 2));
        Canvas.SetTop(BannerHeart, Math.Round(centreY - heart.Height / 2));
    }

    // ---------- Data ----------

    private void ReloadData()
    {
        (_settings, _settingsError) = _store.LoadSettings();
        _state = _store.LoadState();
        (_presetList, _presetsError) = _store.LoadPresets();
        StatusText.Text = _settingsError ?? _presetsError ?? "";
        Autostart.Apply(_settings.StartWithWindows);

        if (_state.ShowHoverBackgrounds)
            TerminalProfiles.Prepare(_settings.Agents);
        else
            TerminalProfiles.Clear();

        UpdateBannerHeart();
        _bannerColorSource = null;
        SetBannerColors(null, animate: false);
        LoadFolders();
        // Fast pass: PATH lookups (on a PATH re-read, so CLIs installed since show up) plus whatever
        // desktop apps were found last time.
        PathResolver.RefreshPath();
        ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
        ClearUsageCache();
    }

    /// <summary>Slow pass: read the Start menu and extract missing icons on a background STA thread.</summary>
    private async Task RefreshFromShellAsync(int generation)
    {
        _sinceShellRefresh.Restart();
        var settings = _settings;
        var inMemory = AllItems.Select(a => a.IconSource).Where(s => _icons.Has(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Known agents not in the list, to offer any that have been installed since.
        var notInList = new DockSettings { Agents = KnownAgents.All().Where(k => !KnownAgents.IsInList(k, settings.Agents)).ToList() };

        try
        {
            var (agents, extracted, newlyFound) = await StartMenuApps.RunOnStaThread(() =>
            {
                var startMenu = AgentCatalog.FromStartMenu(StartMenuApps.Load());
                var resolved = AgentCatalog.Resolve(settings, startMenu);
                var found = AgentCatalog.Resolve(notInList, startMenu).Where(a => a.IsAgentAvailable).ToList();
                var pixels = resolved
                    .Select(a => a.IconSource)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(s => !inMemory.Contains(s) && !ShellIcons.IsOnDisk(s))
                    .Select(s => (Source: s, Pixels: ShellIcons.Extract(s)))
                    .Where(x => x.Pixels is not null)
                    .ToList();
                return (resolved, pixels, found);
            });

            // Back on the UI thread.
            _state.DesktopApps = agents.Where(a => !a.IsCli && a.AppId is not null)
                .ToDictionary(a => a.Definition.Id, a => a.AppId!);
            _store.SaveState(_state);

            foreach (var (source, pixels) in extracted)
                await _icons.StoreAsync(source, pixels!);
            foreach (var source in agents.Select(a => a.IconSource).OfType<string>())
                await _icons.LoadFromDiskAsync(source);

            if (generation == _generation)
            {
                var before = string.Join('|', _desktop.Select(a => a.Definition.Id + a.AppId));
                var after = string.Join('|', agents.Where(IsShown).Where(a => !a.IsCli).Select(a => a.Definition.Id + a.AppId));
                if (before != after)
                {
                    ShowAgents(agents);
                    if (AppWindow.IsVisible)
                        PlaceWindow();
                }

                foreach (var item in AllItems.Where(i => i.Icon is null))
                    item.Icon = _icons.Get(item.IconSource);

                if (_state.AgentSetupDone)
                {
                    var wasOpen = NewAgentOffer.IsOpen;
                    OfferNewAgents(newlyFound);
                    if (AppWindow.IsVisible && wasOpen != NewAgentOffer.IsOpen)
                        PlaceWindow();
                }
            }

            UpdateJumpList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine(ex);
        }
    }

    private bool IsShown(AgentItem item) => item.IsAvailable || !_settings.HideUnavailable;

    private void ShowAgents(IEnumerable<AgentItem> agents)
    {
        _agents = agents.ToList();
        _presets.Clear();
        _cli.Clear();
        _desktop.Clear();

        // A preset whose folder is missing still shows (dimmed), so it can be fixed or removed.
        foreach (var preset in AgentCatalog.ResolvePresets(_presetList, _agents)
                     .Where(p => p.IsAgentAvailable || !_settings.HideUnavailable))
        {
            preset.Icon ??= _icons.Get(preset.IconSource);
            _presets.Add(preset);
        }
        foreach (var agent in _agents.Where(IsShown))
        {
            agent.Icon ??= _icons.Get(agent.IconSource);
            (agent.IsCli ? _cli : _desktop).Add(agent);
        }

        // 1–9, then letters once the digits run out (owner's request, 2026-10-19).
        const string keys = "123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var next = 0;
        foreach (var agent in AllItems)
            agent.Shortcut = agent.IsAvailable && next < keys.Length ? keys[next++].ToString() : null;

        PresetHeader.Visibility = _presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetList.Visibility = PresetHeader.Visibility;
        CliHeader.Visibility = _cli.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CliList.Visibility = CliHeader.Visibility;
        DesktopHeader.Visibility = _desktop.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DesktopList.Visibility = DesktopHeader.Visibility;
        // An empty list gets the welcome (or "no agents yet"); a list with nothing installed, the hint.
        EmptyText.Visibility = _settings.Agents.Count > 0 && _presets.Count + _cli.Count + _desktop.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateSetupPanel();
    }

    // ---------- Reordering by dragging ----------

    /// <summary>
    /// A row was dragged to a new place in its list (presets, command line, or desktop apps): save
    /// that order. Agents not shown (hidden as unavailable, or in the other list) keep their places.
    /// </summary>
    private void AgentList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            return;

        if (ReferenceEquals(sender, PresetList))
        {
            if (_presetsError is not null)
            {
                SetStatus(_presetsError); // never overwrite a file the user is part-way through editing
                return;
            }
            var shown = _presets.Select(p => p.Preset).OfType<Preset>().ToList();
            _presetList = ReorderShown(_presetList, shown);
            _store.SavePresets(_presetList);
            return;
        }

        var reordered = (ReferenceEquals(sender, CliList) ? _cli : _desktop).Select(i => i.Definition).ToList();
        _settings.Agents = ReorderShown(_settings.Agents, reordered);
        SaveAgentList();
    }

    /// <summary><paramref name="all"/>, with the items in <paramref name="shown"/> put in that order, in the places they held.</summary>
    private static List<T> ReorderShown<T>(List<T> all, List<T> shown) where T : class
    {
        var moving = shown.ToHashSet(ReferenceEqualityComparer.Instance);
        var next = new Queue<T>(shown);
        return all.Select(item => moving.Contains(item) && next.Count > 0 ? next.Dequeue() : item).ToList();
    }

    // ---------- Setting up the agent list ----------

    /// <summary>
    /// The welcome on first run (nothing in the list, never set up), and "no agents yet" once the
    /// user has chosen. Hidden when agents.json couldn't be read: the list there isn't really empty,
    /// and adding agents would write over it.
    /// </summary>
    private void UpdateSetupPanel()
    {
        var show = _settings.Agents.Count == 0 && _settingsError is null;
        SetupPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;
        NewAgentOffer.IsOpen = false;
        if (_state.AgentSetupDone)
        {
            SetupTitle.Text = "No agents yet";
            SetupText.Text = "Find the AI agents installed on this PC, or add your own command in settings.";
            SetupSkipButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetupTitle.Text = "Welcome to Agent Dock";
            SetupText.Text = "Agent Dock can look for the AI agents installed on this PC. You choose which ones appear here.";
            SetupSkipButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Opens Add agents in the settings window; adding there closes it, back to the dock.</summary>
    private void FindAgents_Click(object sender, RoutedEventArgs e) => OpenSettings(addAgents: true);

    private void SetupSkip_Click(object sender, RoutedEventArgs e)
    {
        _state.AgentSetupDone = true;
        _store.SaveState(_state);
        UpdateSetupPanel();
    }

    /// <summary>
    /// Saves the agent list and shows it as saved: the list, its icons and Start menu lookups, and
    /// the jump list. False (nothing saved) when agents.json couldn't be read this time, so a hand
    /// edit with a mistake in it is never written over.
    /// </summary>
    private bool SaveAgentList()
    {
        if (_settingsError is not null)
            return false;
        _store.SaveSettings(_settings);
        if (!_state.AgentSetupDone)
        {
            _state.AgentSetupDone = true;
            _store.SaveState(_state);
        }
        ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
        foreach (var item in AllItems.Where(i => i.Icon is null))
            item.Icon = _icons.Get(item.IconSource);
        _ = RefreshFromShellAsync(_generation); // desktop apps, icons, the jump list
        return true;
    }

    // New agents installed since: offered once, from the slow pass.

    private List<AgentItem> _offeredAgents = [];

    /// <summary>
    /// After the list is set up, a known agent that's installed, not in the list, and not turned
    /// down before is offered at the top of the flyout. Never added without asking.
    /// </summary>
    private void OfferNewAgents(IEnumerable<AgentItem> found)
    {
        _offeredAgents = found
            .Where(a => !_state.DismissedAgentOffers.Contains(a.Definition.Catalog ?? a.Definition.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (_offeredAgents.Count == 0 || _settings.Agents.Count == 0 || _settingsError is not null)
        {
            NewAgentOffer.IsOpen = false;
            return;
        }
        if (_offeredAgents.Count == 1)
        {
            NewAgentOffer.Title = $"{_offeredAgents[0].Definition.Name} is installed";
            NewAgentOffer.Message = "Add it to your agents?";
            NewAgentOfferButton.Content = "Add";
        }
        else
        {
            NewAgentOffer.Title = $"{_offeredAgents.Count} more agents are installed";
            NewAgentOffer.Message = string.Join(", ", _offeredAgents.Select(a => a.Definition.Name));
            NewAgentOfferButton.Content = "Choose";
        }
        NewAgentOffer.IsOpen = true;
    }

    private void NewAgentOfferButton_Click(object sender, RoutedEventArgs e)
    {
        NewAgentOffer.IsOpen = false;
        if (_offeredAgents.Count == 1)
        {
            AddAgents([_offeredAgents[0].Definition]);
            if (AppWindow.IsVisible)
                PlaceWindow();
            return;
        }
        OpenSettings(addAgents: true); // several: choose which in settings
    }

    private void NewAgentOffer_CloseButtonClick(InfoBar sender, object args)
    {
        DismissOffers(_offeredAgents.Select(a => a.Definition));
        if (AppWindow.IsVisible)
            PlaceWindow();
    }

    private void DismissOffers(IEnumerable<AgentDefinition> agents)
    {
        foreach (var id in agents.Select(a => a.Catalog ?? a.Id))
            if (!_state.DismissedAgentOffers.Contains(id, StringComparer.OrdinalIgnoreCase))
                _state.DismissedAgentOffers.Add(id);
        _store.SaveState(_state);
    }

    /// <summary>Adds known agents (copies, with ids that don't clash) to the end of the list.</summary>
    private void AddAgents(IEnumerable<AgentDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            var copy = definition.Clone();
            copy.Id = KnownAgents.UniqueId(copy.Id, _settings.Agents);
            _settings.Agents.Add(copy);
        }
        SaveAgentList();
    }

    // ---------- Folders ----------

    private void LoadFolders()
    {
        var current = _state.CurrentFolder ?? DefaultFolder();
        var folders = new[] { current }.Concat(_state.RecentFolders)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _settings.MaxRecentFolders))
            .ToList();
        if (folders.Count == 0)
            folders.Add(DefaultFolder());

        _loadingFolders = true;
        try
        {
            _folders.Clear();
            foreach (var folder in folders)
                _folders.Add(new FolderItem(folder));
            FolderBox.SelectedIndex = 0;
        }
        finally
        {
            _loadingFolders = false;
        }
    }

    private void UseFolder(string path)
    {
        _state.CurrentFolder = path;
        _state.RecentFolders = new[] { path }.Concat(_state.RecentFolders)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _settings.MaxRecentFolders))
            .ToList();
        _store.SaveState(_state);
        UpdateJumpList(path);
    }

    private void FolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingFolders && FolderBox.SelectedItem is FolderItem folder)
            UseFolder(folder.Path);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        _keepOpen = true;
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { SuggestedFolder = CurrentFolder };
            var result = await picker.PickSingleFolderAsync();
            if (result?.Path is not { Length: > 0 } path)
                return;

            var existing = _folders.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new FolderItem(path);
                _folders.Insert(0, existing);
            }
            FolderBox.SelectedItem = existing;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus($"The folder picker didn't open: {ex.Message}");
        }
        finally
        {
            _keepOpen = false;
            BringToFront();
        }
    }

    // ---------- Actions ----------

    private void Agent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AgentItem item)
            Launch(item);
    }

    private void Launch(AgentItem item, string? folder = null)
    {
        var dir = folder ?? item.Preset?.Folder ?? CurrentFolder;
        if (Launcher.Launch(item, dir, _settings, _state.ShowHoverBackgrounds) is { } error)
            SetStatus(error);
        else
        {
            RememberLaunch(item, dir);
            HideFlyout();
        }
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentItem { QuickLaunch: { } other } item })
            Launch(item, other);
    }

    /// <summary>A desktop agent opened without a folder, so there's no recent folder to remember.</summary>
    private void Launch(AgentItem item, OtherLaunch other)
    {
        if (Launcher.Launch(item, other) is { } error)
            SetStatus(error);
        else
            HideFlyout();
    }

    /// <summary>Only for the agents in <see cref="RecentLaunches.Records"/>. A folder already in the list moves to the front.</summary>
    public void RememberLaunch(AgentDefinition agent, string agentId, string folder)
    {
        // GitHub Copilot desktop without Copilot CLI opened without the folder.
        if (KnownAgents.CanOpenFolder(agent) && RecentLaunches.Remember(_state, agent, agentId, folder))
            _store.SaveState(_state);
    }

    private void RememberLaunch(AgentItem item, string folder) =>
        RememberLaunch(item.Definition, item.Preset?.Agent ?? item.Definition.Id, folder);

    private void OpenShell_Click(object sender, RoutedEventArgs e)
    {
        if (Launcher.OpenShell(CurrentFolder, _settings) is { } error)
            SetStatus(error);
        else
            HideFlyout();
    }

    private void EditAgents_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_store.SettingsPath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No app registered for .json files.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_store.SettingsPath}\""));
        }
        HideFlyout();
    }

    /// <summary>
    /// The gear: settings live in their own window (<see cref="SettingsWindow"/>), which hides the
    /// flyout first, so the flyout's hide-on-deactivate can't race the window taking focus.
    /// </summary>
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private static void OpenSettings(string? agentId = null, bool addAgents = false, string? section = null) =>
        ((App)Application.Current).OpenSettings(agentId, addAgents, section);

    /// <summary>
    /// Cursor CLI or the Cursor desktop app (one account, one usage), by catalog id or CLI command.
    /// The CLI's command can also be plain "agent", so the catalog id matters. Also used by settings.
    /// </summary>
    internal static bool IsCursor(AgentDefinition definition) =>
        (definition.Catalog ?? definition.Id).ToLowerInvariant() is "cursor" or "cursor-cli"
        || (definition.Kind == AgentKind.Cli && CommandName(definition.Command) == "cursor-agent");

    /// <summary>
    /// GitHub Copilot CLI or GitHub Copilot desktop: one GitHub account (gh's sign-in), so the same
    /// usage, drawer and overview row. The desktop app is matched by catalog id, having no command.
    /// Also used by settings.
    /// </summary>
    internal static bool IsGitHubCopilot(AgentDefinition definition) =>
        string.Equals(definition.Catalog ?? definition.Id, "github-copilot-desktop", StringComparison.OrdinalIgnoreCase)
        || (definition.Kind == AgentKind.Cli && CommandName(definition.Command) == "copilot");

    /// <summary>
    /// Settings changed something (the window saved it already): read the files again, so this
    /// window's own later saves don't write the old values back, and refresh what depends on them.
    /// Safe while the flyout is hidden, which it normally is while settings is open.
    /// </summary>
    public void NotifySettingsChanged(SettingsChange change)
    {
        if (!_prepared)
            return;

        _state = _store.LoadState();
        if (change.HasFlag(SettingsChange.ApiAccounts))
            _apiChecks.Clear(); // a key or service changed: the last answers may not be for what's there now
        ClearUsageCache();
        if (change.HasFlag(SettingsChange.ClaudeUsageSource))
            DropClaudeLiveChecks();

        if (change.HasFlag(SettingsChange.Agents))
        {
            (_settings, _settingsError) = _store.LoadSettings();
            // The rows hold the definitions in _settings, so they're rebuilt from the new ones.
            ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
            foreach (var item in AllItems.Where(i => i.Icon is null))
                item.Icon = _icons.Get(item.IconSource);
            _ = RefreshFromShellAsync(_generation); // desktop apps, icons, the jump list
            if (AppWindow.IsVisible)
                PlaceWindow();
        }
        else if (change.HasFlag(SettingsChange.TaskbarIcon))
            UpdateJumpList(); // the Quit entry's icon
    }

    /// <summary>Forgets the last direct check of every Claude account, after the choice of where usage comes from changed.</summary>
    private void DropClaudeLiveChecks()
    {
        foreach (var definition in _settings.Agents)
            if (UsageAccount.For(definition) is { Agent: "claude" } account)
                _liveChecks.Remove(account.Key);
        _liveCheckEpoch++;
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideFlyout();
            e.Handled = true;
            return;
        }

        if (FolderBox.IsDropDownOpen)
            return;

        var key = e.Key switch
        {
            >= VirtualKey.Number1 and <= VirtualKey.Number9 => (char)('1' + (e.Key - VirtualKey.Number1)),
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 => (char)('1' + (e.Key - VirtualKey.NumberPad1)),
            >= VirtualKey.A and <= VirtualKey.Z => (char)('A' + (e.Key - VirtualKey.A)),
            _ => '\0'
        };
        // Not with Ctrl, Alt or Windows held (Alt+F4 and the like), and letters still type into the
        // folder box when it has the focus.
        if (key == '\0' || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows)
            || IsDown(VirtualKey.RightWindows) || (char.IsLetter(key) && FolderBox.FocusState != FocusState.Unfocused))
            return;

        var target = AllItems.FirstOrDefault(a => a.Shortcut == key.ToString());
        if (target is not null)
        {
            Launch(target);
            e.Handled = true;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Lets Up/Down arrows cross between the lists.</summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListView list || FocusManager.GetFocusedElement(Root.XamlRoot) is not ListViewItem focused)
            return;

        var index = list.IndexFromContainer(focused);
        var lists = Lists.Where(l => l.Items.Count > 0).ToList();
        var position = lists.IndexOf(list);
        var target = e.Key switch
        {
            VirtualKey.Down when index == list.Items.Count - 1 && position < lists.Count - 1 => lists[position + 1],
            VirtualKey.Up when index == 0 && position > 0 => lists[position - 1],
            _ => null
        };
        if (target is null)
            return;

        var targetIndex = e.Key == VirtualKey.Down ? 0 : target.Items.Count - 1;
        if (target.ContainerFromIndex(targetIndex) is ListViewItem container)
        {
            container.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    // ---------- Presets ----------

    /// <summary>Right-click, Shift+F10 or the menu key on a row.</summary>
    private void Agent_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // From the mouse, OriginalSource is something inside the row; from the keyboard, the row itself.
        if (sender is not ListView list || ContainerOf(e.OriginalSource as DependencyObject) is not { } anchor
            || list.ItemFromContainer(anchor) is not AgentItem item)
            return;

        var menu = new MenuFlyout();
        if (item.Preset is { } preset)
        {
            menu.Items.Add(MenuItem("Rename", "", () => StartRename(preset, anchor)));
            menu.Items.Add(MenuItem("Remove", "", () => RemovePreset(preset)));
        }
        else
        {
            var others = KnownAgents.OtherLaunches(item.Definition);
            foreach (var other in others)
            {
                var entry = MenuItem(other.Text, other.Glyph, () => Launch(item, other));
                entry.IsEnabled = item.IsAgentAvailable;
                menu.Items.Add(entry);
            }
            if (others.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());

            // Most desktop apps don't open in a folder, so they can't be presets (Claude can).
            if (item.TakesFolder)
            {
                var folder = CurrentFolder;
                menu.Items.Add(MenuItem($"Save as preset for {new FolderItem(folder).Name}", "\uE718", () => SavePreset(item, folder)));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(MenuItem("Settings", "\uE713", () => OpenSettings(agentId: item.Definition.Id)));
            var remove = MenuItem("Remove from list", "\uE74D", () => ConfirmRemoveAgent(item, anchor));
            remove.IsEnabled = _settingsError is null; // agents.json that couldn't be read isn't written
            menu.Items.Add(remove);
        }

        HoldWhileOpen(menu);
        if (e.TryGetPosition(anchor, out var point))
            menu.ShowAt(anchor, point);
        else
            menu.ShowAt(anchor);
        e.Handled = true;
    }

    /// <summary>Asks beside the row, then takes the agent out of the list. Its presets stay.</summary>
    private void ConfirmRemoveAgent(AgentItem item, FrameworkElement anchor)
    {
        var confirm = new Button { Content = "Remove", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12, MaxWidth = 260,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Remove {item.Definition.Name} from your agents? You can add it again from settings.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    confirm
                }
            }
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            if (!_settings.Agents.Remove(item.Definition))
                return;
            SaveAgentList();
            if (AppWindow.IsVisible)
                PlaceWindow();
            DispatcherQueue.TryEnqueue(FocusFirstAgent);
        };
        HoldWhileOpen(flyout);
        flyout.ShowAt(anchor);
    }

    private static ListViewItem? ContainerOf(DependencyObject? element)
    {
        while (element is not null and not ListViewItem)
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        return element as ListViewItem;
    }

    private MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var menuItem = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        // After the menu has closed, so a follow-up flyout (rename) can open.
        menuItem.Click += (_, _) => DispatcherQueue.TryEnqueue(() => action());
        return menuItem;
    }

    private void SavePreset(AgentItem agent, string folder)
    {
        if (_presetsError is not null)
        {
            SetStatus(_presetsError); // never overwrite a file the user is part-way through editing
            return;
        }

        var existing = _presetList.FirstOrDefault(p =>
            p.Agent.Equals(agent.Definition.Id, StringComparison.OrdinalIgnoreCase) && SamePath(p.Folder, folder));
        if (existing is not null)
        {
            SetStatus($"That's already a preset: \"{existing.Name}\".");
            return;
        }

        var preset = new Preset { Name = UniquePresetName(new FolderItem(folder).Name), Agent = agent.Definition.Id, Folder = folder };
        _presetList.Add(preset);
        SavePresetsAndRefresh($"Saved \"{preset.Name}\". Right-click it to rename it.");
    }

    private void RemovePreset(Preset preset)
    {
        if (_presetsError is not null)
        {
            SetStatus(_presetsError);
            return;
        }
        _presetList.Remove(preset);
        SavePresetsAndRefresh($"Removed \"{preset.Name}\".");
    }

    /// <summary>A small flyout with the name in a text box. Enter saves, Esc cancels.</summary>
    private void StartRename(Preset preset, FrameworkElement anchor)
    {
        var box = new TextBox { Text = preset.Name, Width = 260 };
        AutomationProperties.SetName(box, "Preset name");
        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, MaxWidth = 260,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };
        var save = new Button
        {
            Content = "Rename", HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { box, error, save } } };

        void Commit()
        {
            if (TryRenamePreset(preset, box.Text) is { } message)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        save.Click += (_, _) => Commit();
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        HoldWhileOpen(flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>Returns null on success, or why the name can't be used.</summary>
    private string? TryRenamePreset(Preset preset, string newName)
    {
        if (_presetsError is not null)
            return _presetsError;

        // Quotes would break the jump list's --preset "<name>" argument.
        var name = newName.Replace("\"", "").Trim();
        if (name.Length == 0)
            return "Enter a name.";
        if (_presetList.Any(p => p != preset && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return "Another preset already has that name.";

        preset.Name = name;
        SavePresetsAndRefresh(null);
        return null;
    }

    private void SavePresetsAndRefresh(string? status)
    {
        _store.SavePresets(_presetList);
        ShowAgents(_agents);
        StatusText.Text = status ?? "";
        PlaceWindow();
        UpdateJumpList();
    }

    private string UniquePresetName(string baseName)
    {
        baseName = baseName.Replace("\"", "").Trim();
        var name = baseName;
        for (var n = 2; _presetList.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} {n}";
        return name;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // ---------- Plan usage (drawer content) ----------

    // Usage is per account (UsageAccount.Key): two copies on one account share it, two accounts never
    // mix. Files are read on the first hover after each show (ClearUsageCache), so opening stays fast.
    private readonly Dictionary<string, ClaudeStatusLine.Setup> _claudeSetups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlanUsage?> _savedUsage = new(StringComparer.OrdinalIgnoreCase);
    // Live checks (Claude asked directly, Codex) come over the network, so they're kept across shows.
    private readonly Dictionary<string, LiveCheck> _liveChecks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Changes when a usage setting does, so a check started before that is ignored.</summary>
    private int _liveCheckEpoch;
    // Copilot's comes over the network, so it's fetched in the background and kept across shows.
    private static readonly TimeSpan CopilotUsageMaxAge = TimeSpan.FromMinutes(5);
    private CopilotUsageResult? _copilotUsage;
    private bool _copilotFetching;
    /// <summary>The row whose content the drawer is showing.</summary>
    private AgentItem? _drawerItem;

    private sealed class LiveCheck
    {
        public UsageFetch? Result;
        public DateTimeOffset At;
        public bool Running;
    }

    /// <summary>
    /// Drops what was loaded from files, so the next hover reads them again. Live results survive a
    /// show; <paramref name="sourceChangedFor"/> (that account's usage setting changed) drops its too.
    /// </summary>
    private void ClearUsageCache(string? sourceChangedFor = null)
    {
        _claudeSetups.Clear();
        _savedUsage.Clear();
        if (sourceChangedFor is null)
            return;
        _liveChecks.Remove(sourceChangedFor);
        _liveCheckEpoch++;
    }

    /// <summary>
    /// Fills the drawer for <paramref name="item"/>. Returns false, leaving the drawer as it is, if
    /// that agent has nothing to show: so far Claude Code, Codex and GitHub Copilot (and their presets).
    /// </summary>
    private bool PrepareDrawer(AgentItem item)
    {
        if (!PrepareAgentDrawer(item))
            return false;
        // The agent's own content replaces the overview.
        _summaryShowing = false;
        SummaryPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool PrepareAgentDrawer(AgentItem item)
    {
        RecentSection.Visibility = Visibility.Collapsed;
        if (!item.IsAgentAvailable)
            return false;
        // Linked to an API key (Aider or OpenCode on DeepSeek, say): that balance, whatever the agent.
        if (ApiAccountFor(item.Definition) is { } api)
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartApiCheck(api);
            ShowApiUsage(api);
            ShowRecents(item);
            return true;
        }
        // The CLI and the desktop app share one account, so both show its usage.
        if (IsCursor(item.Definition))
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartCursorCheck();
            ShowCursorCheck();
            ShowRecents(item);
            return true;
        }
        if (IsGitHubCopilot(item.Definition))
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartCopilotCheck();
            ShowCopilotUsage();
            ShowRecents(item);
            return true;
        }
        // Claude and ChatGPT desktop share Claude Code's and Codex's default accounts (UsageAccount.For),
        // so they get the same drawer as those, below, with their own recents.
        if (!item.IsCli && UsageAccount.For(item.Definition) is null)
        {
            // Another desktop app that opens in a folder: its recent folders, when there are some.
            if (!item.TakesFolder)
                return false;
            UsagePanel.Visibility = Visibility.Collapsed;
            UsageOffer.IsOpen = false;
            if (!ShowRecents(item))
                return false;
            _drawerItem = item;
            return true;
        }

        var account = UsageAccount.For(item.Definition);
        // Pointed at an API key or another endpoint: no Claude or ChatGPT plan behind it.
        if (account is { UsesOtherProvider: true })
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            ShowUsage(account.Agent == "claude" ? "Claude usage" : "Codex usage", null,
                "Plan limits don't apply: this agent uses its own API key or endpoint.");
            ShowRecents(item);
            return true;
        }

        switch (account?.Agent ?? CommandName(item.Definition.Command))
        {
            case "claude":
                return PrepareClaudeDrawer(item, account!);
            case "codex":
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                // Live from Codex, as this account, at most every few minutes; what's known shows meanwhile.
                StartCodexCheck(account!);
                ShowCodexCheck(account!);
                ShowRecents(item);
                return true;
            case "grok":
                // From grok.com, with Grok Build's own sign-in.
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                StartGrokCheck();
                ShowGrokCheck();
                ShowRecents(item);
                return true;
            default:
                // These CLIs have no common provider quota, but they do open in folders and can show them again.
                if (!RecentLaunches.Records(item.Definition))
                    return false;
                _drawerItem = item;
                UsagePanel.Visibility = Visibility.Collapsed;
                UsageOffer.IsOpen = false;
                return ShowRecents(item);
        }
    }

    /// <summary>Where this Claude account's usage comes from (set in the settings window, on the agent's page).</summary>
    private ClaudeUsageSource SourceFor(UsageAccount account)
    {
        if (account.IsDefault)
            return _state.ClaudeUsageSource;
        foreach (var (dir, source) in _state.ClaudeUsageSources)
            if (string.Equals(dir, account.ConfigDir, StringComparison.OrdinalIgnoreCase))
                return source;
        return ClaudeUsageSource.StatusLine;
    }

    private bool OfferDismissed(UsageAccount account) =>
        account.IsDefault
            ? _state.ClaudeUsageOfferDismissed
            : _state.ClaudeUsageOfferDismissedFor.Contains(account.ConfigDir, StringComparer.OrdinalIgnoreCase);

    /// <summary>The usage last saved for an account (by its status line or its last live check).</summary>
    private PlanUsage? Saved(UsageAccount account)
    {
        if (!_savedUsage.TryGetValue(account.Key, out var usage))
            _savedUsage[account.Key] = usage = account.Agent == "claude"
                ? ClaudeStatusLine.Load(account.ConfigDir)
                : CodexUsage.LoadSaved(account.ConfigDir);
        return usage;
    }

    /// <summary>
    /// Claude Code's usage for this agent's account, from the source chosen on its settings page.
    /// Status line: the saved usage, or the offer to set Agent Dock up as that account's status line.
    /// Ask Claude directly: the last check, else the saved file, and a new check if that's over 3
    /// minutes old. Nothing checks while the flyout is hidden.
    /// </summary>
    private bool PrepareClaudeDrawer(AgentItem item, UsageAccount account)
    {
        if (SourceFor(account) == ClaudeUsageSource.Api)
        {
            UsageOffer.IsOpen = false;
            StartClaudeCheck(account);
            ShowClaudeCheck(account);
            ShowRecents(item);
            _drawerItem = item;
            return true;
        }

        if (!_claudeSetups.TryGetValue(account.Key, out var setup))
            _claudeSetups[account.Key] = setup = ClaudeStatusLine.CheckSetup(Environment.ProcessPath!, account.ConfigDir);
        switch (setup.State)
        {
            case ClaudeStatusLine.SetupState.Installed:
                UsageOffer.IsOpen = false;
                // Claude Code only refreshes its status line while a session is running.
                ShowUsage("Claude usage", Saved(account), "Your limits appear here after your next Claude Code reply.");
                break;
            case ClaudeStatusLine.SetupState.OtherCopy:
                // Not dismissable: usage here has silently stopped updating.
                OfferUsage(InfoBarSeverity.Warning, "Update",
                    "Claude Code's status line runs a different copy of Agent Dock, so usage here won't update.", closable: false);
                break;
            case ClaudeStatusLine.SetupState.NotInstalled when !OfferDismissed(account):
                OfferUsage(InfoBarSeverity.Informational, "Set up", setup.CurrentCommand is null
                    ? "See your plan's usage limits here. Agent Dock becomes Claude Code's status line to read them."
                    : $"See your plan's usage limits here. Agent Dock replaces Claude Code's status line ({DescribeCommand(setup.CurrentCommand)}). Its settings are backed up first.",
                    closable: true);
                break;
            default:
                UsagePanel.Visibility = Visibility.Collapsed;
                UsageOffer.IsOpen = false;
                if (!ShowRecents(item))
                    return false;
                _drawerItem = item;
                return true;
        }
        ShowRecents(item);
        _drawerItem = item;
        return true;
    }

    /// <summary>
    /// Starts a live check under <paramref name="key"/> (an account, or "grok") unless one is running
    /// or its last answer is still fresh. When it finishes, <paramref name="show"/> fills the drawer,
    /// if it's still on an agent <paramref name="showing"/> accepts, in the same show.
    /// </summary>
    private void StartLiveCheck(string key, TimeSpan maxAge, Func<Task<UsageFetch>> fetch, string unreachable,
        Func<AgentItem, bool> showing, Action show)
    {
        if (!_liveChecks.TryGetValue(key, out var check))
            _liveChecks[key] = check = new LiveCheck();
        if (check.Running || (check.Result is not null && DateTimeOffset.UtcNow - check.At <= maxAge))
            return;
        _ = RunLiveCheckAsync(key, check, fetch, unreachable, showing, show);
    }

    private async Task RunLiveCheckAsync(string key, LiveCheck check, Func<Task<UsageFetch>> fetch, string unreachable,
        Func<AgentItem, bool> showing, Action show)
    {
        var epoch = _liveCheckEpoch;
        var shown = _generation;
        check.Running = true;
        UsageFetch result;
        try
        {
            result = await fetch(); // resumes on the UI thread; never throws
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new UsageFetch(null, unreachable);
        }
        check.Running = false;
        // A usage setting changed while this ran: it isn't wanted any more.
        if (epoch != _liveCheckEpoch || !_liveChecks.TryGetValue(key, out var current) || current != check)
            return;
        check.Result = result;
        check.At = DateTimeOffset.UtcNow;
        if (result.Usage is not null)
            _savedUsage.Remove(key); // the file was just written; read it again when needed

        if (shown != _generation)
            return;
        if (_drawerOpen && !_drawerClosing && _drawerItem is { } item && showing(item))
            show();
        else if (_summaryShowing)
            ShowSummary();
    }

    /// <summary>Grok and Cursor have one account each, their CLI's sign-in.</summary>
    private const string GrokCheckKey = "grok";
    private const string CursorCheckKey = "cursor";

    // Each starts its agent's check unless one is running or fresh, and fills the drawer if it's
    // still on that agent when the answer comes (else the overview, if that's showing).

    private void StartClaudeCheck(UsageAccount account) =>
        StartLiveCheck(account.Key, ClaudeUsageApi.MinInterval, () => ClaudeUsageApi.FetchAsync(account), ClaudeUsageApi.Unreachable,
            shown => OnAccount(shown, account) && SourceFor(account) == ClaudeUsageSource.Api, () => ShowClaudeCheck(account));

    private void StartCodexCheck(UsageAccount account) =>
        StartLiveCheck(account.Key, CodexUsage.MinInterval, () => CodexUsage.FetchAsync(account), CodexUsage.Unreachable,
            shown => OnAccount(shown, account), () => ShowCodexCheck(account));

    private void StartGrokCheck() =>
        StartLiveCheck(GrokCheckKey, GrokUsage.MinInterval, GrokUsage.FetchAsync, GrokUsage.Unreachable,
            shown => CommandName(shown.Definition.Command) == "grok", ShowGrokCheck);

    private void StartCursorCheck() =>
        StartLiveCheck(CursorCheckKey, CursorUsage.MinInterval, CursorUsage.FetchAsync, CursorUsage.Unreachable,
            shown => IsCursor(shown.Definition), ShowCursorCheck);

    private void StartCopilotCheck()
    {
        if (!_copilotFetching && (_copilotUsage is null || DateTimeOffset.UtcNow - _copilotUsage.FetchedAt > CopilotUsageMaxAge))
            _ = FetchCopilotUsageAsync();
    }

    // ---------- Usage overview (the drawer while no agent with usage is hovered) ----------

    /// <summary>The drawer is showing the overview rather than one agent.</summary>
    private bool _summaryShowing;

    /// <summary>
    /// One line of the overview: an agent's usage, by account, or an API key's balance (<see cref="Api"/>;
    /// <see cref="Item"/> is the first agent linked to it, null when none is).
    /// </summary>
    private sealed record SummaryEntry(string Key, AgentItem? Item, PlanUsage? Usage, bool Checking, string WeekWord,
        CopilotUsageResult? Copilot = null, ApiAccount? Api = null, ApiBalanceState? Balance = null, string? Problem = null)
    {
        /// <summary>The row's name: the API account's, else the agent's.</summary>
        public string Title => Api?.Name ?? Item!.Definition.Name;
    }

    /// <summary>
    /// The agents in the list with usage to show, one per account (copies on one account share a
    /// line, named after the first), then API keys no listed agent is linked to. Presets are left
    /// out; their agent stands for them.
    /// </summary>
    private List<SummaryEntry> SummaryEntries()
    {
        var entries = new List<SummaryEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _cli.Concat(_desktop).Where(i => i.IsAgentAvailable))
            if (SummaryEntryFor(item) is { } entry && keys.Add(entry.Key))
                entries.Add(entry);
        foreach (var account in CheckedApiAccounts)
            if (ApiSummaryEntry(account, null) is var entry && keys.Add(entry.Key))
                entries.Add(entry);
        return entries;
    }

    private SummaryEntry ApiSummaryEntry(ApiAccount account, AgentItem? item)
    {
        var check = _apiChecks.GetValueOrDefault(account.Id);
        return new SummaryEntry($"api|{account.Id}", item, null, check?.Running == true, "balance",
            Api: account, Balance: BalanceOf(account) is { UpdatedAt: > 0 } state ? state : null, Problem: check?.Problem);
    }

    private SummaryEntry? SummaryEntryFor(AgentItem item)
    {
        if (ApiAccountFor(item.Definition) is { } api)
            return ApiSummaryEntry(api, item);
        LiveCheck? Check(string key) => _liveChecks.GetValueOrDefault(key);
        PlanUsage? SavedFor(string key, Func<PlanUsage?> load)
        {
            if (!_savedUsage.TryGetValue(key, out var saved))
                _savedUsage[key] = saved = load();
            return saved;
        }

        if (IsCursor(item.Definition))
        {
            var check = Check(CursorCheckKey);
            return new SummaryEntry(CursorCheckKey, item, check?.Result?.Usage ?? SavedFor(CursorCheckKey, CursorUsage.LoadSaved),
                check?.Running == true, "month");
        }
        if (IsGitHubCopilot(item.Definition))
            return new SummaryEntry("copilot", item, null, _copilotFetching && _copilotUsage is null, "month", _copilotUsage);
        // Claude desktop has Claude Code's account, so it shares that row (or has it, if alone).
        var account = UsageAccount.For(item.Definition);
        if (account is { UsesOtherProvider: true } || (!item.IsCli && account is null))
            return null;
        switch (account?.Agent ?? CommandName(item.Definition.Command))
        {
            case "claude":
            {
                var check = SourceFor(account!) == ClaudeUsageSource.Api ? Check(account!.Key) : null;
                return new SummaryEntry(account!.Key, item, check?.Result?.Usage ?? Saved(account), check?.Running == true, "week");
            }
            case "codex":
            {
                var check = Check(account!.Key);
                return new SummaryEntry(account.Key, item, check?.Result?.Usage ?? Saved(account), check?.Running == true, "week");
            }
            case "grok":
            {
                var check = Check(GrokCheckKey);
                return new SummaryEntry(GrokCheckKey, item, check?.Result?.Usage ?? SavedFor(GrokCheckKey, GrokUsage.LoadSaved),
                    check?.Running == true, "week");
            }
            default:
                return null;
        }
    }

    /// <summary>Starts every listed agent's check (each at most as often as its drawer would), so the overview fills in.</summary>
    private void StartSummaryChecks()
    {
        if (!_state.ShowUsageSummary)
            return;
        foreach (var account in CheckedApiAccounts)
        {
            StartApiCheck(account);
            if (ApiProviderIcon(account) is { } source)
                _ = _icons.LoadFromDiskAsync(source); // for the row of a key no agent is linked to
        }
        foreach (var item in _cli.Concat(_desktop).Where(i => i.IsAgentAvailable))
        {
            if (ApiAccountFor(item.Definition) is not null)
                continue; // its balance, checked above
            if (IsCursor(item.Definition))
            {
                StartCursorCheck();
                continue;
            }
            if (IsGitHubCopilot(item.Definition))
            {
                StartCopilotCheck();
                continue;
            }
            var account = UsageAccount.For(item.Definition);
            if (account is { UsesOtherProvider: true } || (!item.IsCli && account is null))
                continue;
            switch (account?.Agent ?? CommandName(item.Definition.Command))
            {
                case "claude" when SourceFor(account!) == ClaudeUsageSource.Api:
                    StartClaudeCheck(account!);
                    break;
                case "codex":
                    StartCodexCheck(account!);
                    break;
                case "grok":
                    StartGrokCheck();
                    break;
            }
        }
    }

    /// <summary>
    /// Shows the overview in the drawer (opening it if needed), when it's turned on, the list is
    /// showing, and some agent has usage or a check under way. Returns false, changing nothing, otherwise.
    /// </summary>
    private bool ShowSummary()
    {
        if (!_state.ShowUsageSummary || !AppWindow.IsVisible || _popupsOpen > 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        var entries = SummaryEntries();
        var rows = entries.Select(entry => SummaryRow(entry, now)).OfType<UIElement>().ToList();
        if (rows.Count == 0)
        {
            // Everything it showed went away (signed out, say): nothing to keep the drawer open for.
            if (_summaryShowing && _drawerOpen && !_drawerClosing)
                CloseDrawer(animate: true);
            return false;
        }

        var wasShowing = _summaryShowing && _drawerOpen && !_drawerClosing;
        SummaryRows.Children.Clear();
        foreach (var row in rows)
            SummaryRows.Children.Add(row);
        ShowSuggestion(entries, now);
        UsagePanel.Visibility = RecentSection.Visibility = Visibility.Collapsed;
        UsageOffer.IsOpen = false;
        SummaryPanel.Visibility = Visibility.Visible;
        _drawerItem = null;
        _summaryShowing = true;
        if (_drawerOpen && !_drawerClosing && !wasShowing)
            FadeInDrawerContent(); // coming back from an agent's drawer
        OpenDrawer();
        return true;
    }

    /// <summary>One active quota window, normalized for comparing agents with unlike reset periods.</summary>
    private sealed record PaceWindow(string Word, double Used, double? Elapsed, DateTimeOffset? Reset);

    /// <summary>
    /// An agent's capacity from three useful perspectives. The balanced score is the bottleneck: an
    /// agent cannot take more work than its most-used active limit allows.
    /// </summary>
    private sealed record CapacityChoice(SummaryEntry Entry, PaceWindow? Bottleneck, double Balanced,
        double QuickTask, double Sustained);

    /// <summary>
    /// The agent's limits with how far through each window it is: a session is five hours, a week
    /// seven days, a month (Cursor's billing period, Copilot's) the month before its reset.
    /// </summary>
    private static List<PaceWindow> PaceWindows(SummaryEntry entry, DateTimeOffset now)
    {
        static double? Elapsed(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now) =>
            end > start ? Math.Clamp((now - start) / (end - start) * 100, 0, 100) : null;

        var windows = new List<PaceWindow>();
        // A prepaid balance has no window: how much of the last top-up is gone, for as long as it lasts.
        if (entry.Api is not null)
        {
            // Spend with no balance set (Anthropic) has nothing to compare, so it isn't suggested.
            if (entry.Balance is { } balance && ApiUsage.HasPercent(balance))
                windows.Add(new PaceWindow("balance", ApiUsage.UsedPercent(balance), null, null));
            return windows;
        }
        if (entry.Copilot is { } copilot)
        {
            if (copilot is { Used: { } used, Unlimited: false, Entitlement: { } entitlement })
            {
                var reset = copilot.ResetsAt ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
                windows.Add(new PaceWindow("month", Math.Clamp(used * 100 / entitlement, 0, 100),
                    Elapsed(reset.AddMonths(-1), reset, now), reset));
            }
            return windows;
        }

        void Add(UsageWindow? window, string word, Func<DateTimeOffset, DateTimeOffset> startOf)
        {
            if (window is null)
                return;
            if (window.ResetsAt <= 0)
            {
                windows.Add(new PaceWindow(word, Math.Clamp(window.UsedPercentage, 0, 100), null, null));
                return;
            }
            var reset = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt);
            // Already reset: a fresh window.
            windows.Add(reset <= now
                ? new PaceWindow(word, 0, 0, reset)
                : new PaceWindow(word, Math.Clamp(window.UsedPercentage, 0, 100), Elapsed(startOf(reset), reset, now), reset));
        }
        Add(entry.Usage?.FiveHour, "session", reset => reset.AddHours(-5));
        Add(entry.Usage?.SevenDay, entry.WeekWord,
            entry.WeekWord == "month" ? reset => reset.AddMonths(-1) : reset => reset.AddDays(-7));
        return windows;
    }

    /// <summary>Scores usable windows without assuming model quality, cost or the user's task size.</summary>
    private static CapacityChoice? CapacityFor(SummaryEntry entry, List<PaceWindow> windows)
    {
        // GitHub's unlimited Copilot entitlement has no finite window to constrain it.
        if (entry.Copilot is { Unlimited: true })
            return new CapacityChoice(entry, null, 0, 0, 0);
        if (windows.Count == 0 || windows.Any(window => window.Used >= 100))
            return null;

        var bottleneck = windows.OrderByDescending(window => window.Used).First();
        var session = windows.FirstOrDefault(window => window.Word == "session");
        var longer = windows.FirstOrDefault(window => window.Word is "week" or "month" or "balance");
        return new CapacityChoice(entry, bottleneck, bottleneck.Used, session?.Used ?? bottleneck.Used,
            longer?.Used ?? bottleneck.Used);
    }

    private static bool MateriallyBetter(double current, double alternative) => current - alternative >= 15;

    /// <summary>
    /// Who the suggestion names: the agent, or for an API key that several listed agents use, the key,
    /// with them, since any of them draws on the same balance (owner, 2026-10-19).
    /// </summary>
    private string SuggestedName(SummaryEntry entry)
    {
        if (entry.Api is not { } api)
            return entry.Item!.Definition.Name;
        var linked = _cli.Concat(_desktop)
            .Where(i => i.IsAgentAvailable && ApiAccountFor(i.Definition) == api)
            .Select(i => i.Definition.Name).Distinct().ToList();
        return linked.Count > 1 ? $"{api.Name} ({string.Join(", ", linked)})" : entry.Item!.Definition.Name;
    }

    /// <summary>
    /// Suggests the least constrained agent across every active quota window. The main choice uses
    /// its tightest window (a minimax score), then names other agents only when a short task,
    /// sustained work, or a reset within three hours gives a materially better outcome.
    /// </summary>
    private void ShowSuggestion(List<SummaryEntry> entries, DateTimeOffset now)
    {
        // A key no agent is linked to can't be suggested: there's nothing to start.
        entries = entries.Where(entry => entry.Item is not null).ToList();
        var choices = entries
            .Select(entry => (Entry: entry, Windows: PaceWindows(entry, now)))
            .Select(data => CapacityFor(data.Entry, data.Windows))
            .OfType<CapacityChoice>()
            .ToList();
        // A suggestion needs something to choose between, counted before the preference below narrows it.
        var candidates = choices.Count;
        // Included usage first: API balances only once no plan has room (the setting, on by default).
        if (_state.PreferPlansOverApi && choices.Any(choice => choice.Entry.Api is null))
            choices = choices.Where(choice => choice.Entry.Api is null).ToList();
        if (candidates < 2)
        {
            SummarySuggestion.Visibility = Visibility.Collapsed;
            return;
        }

        var pick = choices.OrderBy(choice => choice.Balanced).ThenBy(choice => choice.Sustained)
            .ThenBy(choice => choice.QuickTask).First();
        SummarySuggestionTitle.Text = $"Suggested: {SuggestedName(pick.Entry)}";
        var lines = new List<string>
        {
            pick.Bottleneck is null ? "Unlimited usage." : $"Best balanced capacity: {Math.Round(pick.Balanced)}% at its tightest limit ({pick.Bottleneck.Word})."
        };
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pick.Entry.Key };

        var quick = choices.Where(choice => !shown.Contains(choice.Entry.Key))
            .Select(choice => (Choice: choice, Window: PaceWindows(choice.Entry, now)
                .FirstOrDefault(window => window.Word == "session")))
            .Where(candidate => candidate.Window is not null)
            .OrderBy(candidate => candidate.Choice.QuickTask).ThenBy(candidate => candidate.Choice.Balanced).FirstOrDefault();
        if (quick.Choice is not null && MateriallyBetter(pick.QuickTask, quick.Choice.QuickTask))
        {
            lines.Add($"For a quick task: {SuggestedName(quick.Choice.Entry)} - {Math.Round(quick.Choice.QuickTask)}% of this session used.");
            shown.Add(quick.Choice.Entry.Key);
        }

        var sustained = choices.Where(choice => !shown.Contains(choice.Entry.Key))
            .Select(choice => (Choice: choice, Window: PaceWindows(choice.Entry, now)
                .FirstOrDefault(window => window.Word is "week" or "month" or "balance")))
            .Where(candidate => candidate.Window is not null)
            .OrderBy(candidate => candidate.Choice.Sustained).ThenBy(candidate => candidate.Choice.Balanced).FirstOrDefault();
        if (sustained.Choice is not null && MateriallyBetter(pick.Sustained, sustained.Choice.Sustained))
        {
            lines.Add($"For sustained work: {SuggestedName(sustained.Choice.Entry)} - {Math.Round(sustained.Choice.Sustained)}% of its {sustained.Window!.Word} used.");
            shown.Add(sustained.Choice.Entry.Key);
        }

        const double imminentResetHours = 3;
        (CapacityChoice Choice, PaceWindow Window)? afterReset = null;
        foreach (var entry in entries)
        {
            var windows = PaceWindows(entry, now);
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                if (window.Reset is not { } reset || reset <= now || reset - now > TimeSpan.FromHours(imminentResetHours))
                    continue;
                var simulated = windows.Select((candidate, candidateIndex) => candidateIndex == index ? candidate with { Used = 0 } : candidate).ToList();
                if (CapacityFor(entry, simulated) is not { } candidate || shown.Contains(candidate.Entry.Key))
                    continue;
                if (afterReset is null || candidate.Balanced < afterReset.Value.Choice.Balanced)
                    afterReset = (candidate, window);
            }
        }
        if (afterReset is { } resetChoice && MateriallyBetter(pick.Balanced, resetChoice.Choice.Balanced))
            lines.Add($"After {ResetTime(resetChoice.Window.Reset!.Value)}: {SuggestedName(resetChoice.Choice.Entry)} - {resetChoice.Window.Word} limit resets.");

        SummarySuggestionNote.Text = string.Join('\n', lines);
        SummarySuggestion.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// One limit in an overview row: the bar's value (null = text only, e.g. "Checking…") and the
    /// caption under it, which names the window and when it resets when known.
    /// </summary>
    private readonly record struct SummaryBar(double? Percent, string Caption, string? BarName = null);

    /// <summary>
    /// A clickable line: the agent's icon and name, a bar per reset window (session and week when
    /// both exist), and a caption under each with the percent and reset time. Null when there's
    /// nothing to show (no usage, no check running).
    /// </summary>
    private Button? SummaryRow(SummaryEntry entry, DateTimeOffset now)
    {
        var bars = new List<SummaryBar>();
        if (entry.Api is not null)
        {
            if (entry.Balance is { } balance)
                bars.Add(new SummaryBar(ApiUsage.HasPercent(balance) ? ApiUsage.UsedPercent(balance) : null, ApiUsage.Summary(balance)));
            else if (entry.Checking)
                bars.Add(new SummaryBar(null, "Checking…"));
            else if (entry.Problem is { } problem)
                bars.Add(new SummaryBar(null, problem));
            else
                return null;
        }
        else if (entry.Copilot is { } copilot)
        {
            if (copilot.Used is not { } used)
                return null;
            var unit = copilot.Unit.Length > 0 ? copilot.Unit : "used";
            var reset = copilot.ResetsAt ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
            var resets = $" · resets {ResetTime(reset)}";
            if (copilot.Unlimited)
                bars.Add(new SummaryBar(null, $"Unlimited{resets}"));
            else if (copilot.Entitlement is { } entitlement)
            {
                var percent = Math.Clamp(used * 100 / entitlement, 0, 100);
                bars.Add(new SummaryBar(percent, $"{Math.Round(percent)}% of {unit}{resets}"));
            }
            else
                bars.Add(new SummaryBar(null, $"{used:N0} {unit}{resets}"));
        }
        else
        {
            void Add(UsageWindow? window, string word)
            {
                if (window is null)
                    return;
                bars.Add(SummaryBarFor(window, word, now));
            }
            Add(entry.Usage?.FiveHour, "session");
            Add(entry.Usage?.SevenDay, entry.WeekWord);
            if (bars.Count == 0)
            {
                if (!entry.Checking)
                    return null;
                bars.Add(new SummaryBar(null, "Checking…"));
            }
        }

        var item = entry.Item;
        // An API key shows its service's logo (DeepSeek's), else the linked agent's icon.
        var iconImage = (entry.Api is { } api ? _icons.Get(ApiProviderIcon(api)) : null) ?? item?.Icon;
        var icon = iconImage is { } image
            ? (UIElement)new Image { Source = image, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = item?.Glyph ?? "", FontSize = 16, Width = 20, VerticalAlignment = VerticalAlignment.Center };
        var captionStyle = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var captionBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        // Two windows: stack their bars close together, with a little gap so they don't read as one.
        for (var i = 0; i < bars.Count; i++)
        {
            var part = bars[i];
            var group = new StackPanel { Spacing = 2 };
            if (i > 0)
                group.Margin = new Thickness(0, 4, 0, 0);
            if (part.Percent is { } value)
            {
                var bar = new ProgressBar
                {
                    Maximum = 100,
                    Value = value,
                    ShowError = value >= 90,
                    ShowPaused = value is >= 75 and < 90
                };
                AutomationProperties.SetName(bar, part.BarName ?? $"{entry.Title} {part.Caption}");
                group.Children.Add(bar);
            }
            group.Children.Add(new TextBlock
            {
                Text = part.Caption,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = captionStyle,
                Foreground = captionBrush
            });
            lines.Children.Add(group);
        }
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(icon);
        Grid.SetColumn(lines, 1);
        content.Children.Add(lines);

        var summary = string.Join(" · ", bars.Select(b => b.Caption));
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            Content = content,
            Tag = item
        };
        if (item is null)
        {
            // A key no agent is linked to: settings, where an agent can be linked to it.
            AutomationProperties.SetName(button, $"{entry.Title}: {summary}");
            ToolTipService.SetToolTip(button, "Link it to an agent in settings");
            button.Click += (_, _) => OpenSettings(section: "api");
            return button;
        }
        AutomationProperties.SetName(button, $"Open {item.Definition.Name}: {summary}");
        ToolTipService.SetToolTip(button, entry.Api is null ? $"Open {item.Definition.Name}" : $"Open {item.Definition.Name} ({entry.Title})");
        button.Click += (_, _) => Launch(item);
        return button;
    }

    /// <summary>Percent and a caption like "48% session · resets 16:08", matching the drawer's wording.</summary>
    private static SummaryBar SummaryBarFor(UsageWindow window, string word, DateTimeOffset now)
    {
        if (window.ResetsAt > 0)
        {
            var resetsAt = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt);
            if (resetsAt <= now)
                return new SummaryBar(0, $"0% {word} · reset", $"{word} usage");
            var used = Math.Clamp(window.UsedPercentage, 0, 100);
            return new SummaryBar(used, $"{Math.Round(used)}% {word} · resets {ResetTime(resetsAt)}", $"{word} usage");
        }
        var percent = Math.Clamp(window.UsedPercentage, 0, 100);
        return new SummaryBar(percent, $"{Math.Round(percent)}% {word}", $"{word} usage");
    }

    private void ShowCursorCheck()
    {
        var check = _liveChecks.GetValueOrDefault(CursorCheckKey);
        if (!_savedUsage.TryGetValue(CursorCheckKey, out var saved))
            _savedUsage[CursorCheckKey] = saved = CursorUsage.LoadSaved();
        // One monthly billing period: the total, then its Auto + Composer and API pools under it.
        ShowCheckedUsage("Cursor usage", check?.Result?.Usage ?? saved, check?.Running == true, check?.Result?.Problem,
            CursorUsage.Checking, CursorUsage.NoWindows, CursorUsage.ShowingSaved, showSession: false, weekLabel: "Total");
    }

    private void ShowGrokCheck()
    {
        var check = _liveChecks.GetValueOrDefault(GrokCheckKey);
        if (!_savedUsage.TryGetValue(GrokCheckKey, out var saved))
            _savedUsage[GrokCheckKey] = saved = GrokUsage.LoadSaved();
        // Grok has a weekly pool and no 5-hour window.
        ShowCheckedUsage("Grok usage", check?.Result?.Usage ?? saved, check?.Running == true, check?.Result?.Problem,
            GrokUsage.Checking, GrokUsage.NoWindows, GrokUsage.ShowingSaved, showSession: false);
    }

    /// <summary>Whether the drawer is on an agent of <paramref name="account"/> (another copy counts).</summary>
    private static bool OnAccount(AgentItem item, UsageAccount account) =>
        UsageAccount.For(item.Definition) is { } showing && showing.Key == account.Key;

    private void ShowClaudeCheck(UsageAccount account)
    {
        var check = _liveChecks.GetValueOrDefault(account.Key);
        ShowCheckedUsage("Claude usage", check?.Result?.Usage ?? Saved(account), check?.Running == true,
            check?.Result?.Problem, ClaudeUsageApi.Checking, ClaudeUsageApi.NoWindows, ClaudeUsageApi.ShowingSaved);
    }

    private void ShowCodexCheck(UsageAccount account)
    {
        var check = _liveChecks.GetValueOrDefault(account.Key);
        ShowCheckedUsage("Codex usage", check?.Result?.Usage ?? Saved(account), check?.Running == true,
            check?.Result?.Problem, CodexUsage.Checking, CodexUsage.NoWindows, CodexUsage.ShowingSaved);
    }

    /// <summary>
    /// Usage that's checked over the network: the newest numbers available, with a note when the
    /// check failed (under saved numbers, or on its own) or is still running with nothing to show.
    /// </summary>
    private void ShowCheckedUsage(string title, PlanUsage? usage, bool checking, string? problem,
        string checkingNote, string noWindowsNote, string showingSaved, bool showSession = true, string weekLabel = "Week")
    {
        var hasData = usage is { FiveHour: not null } or { SevenDay: not null };
        string? note;
        if (checking && problem is null)
            note = hasData ? null : checkingNote; // saved numbers stay as they are
        else if (problem is not null)
            note = hasData ? $"{problem} {showingSaved}" : problem;
        else
            note = null;

        ShowUsage(title, usage, hasData ? "" : note ?? noWindowsNote, hasData ? note : null, showSession, weekLabel);
    }

    /// <summary>The folders currently offered for the hovered agent, newest first, at most five.</summary>
    private List<string> _shownRecents = [];
    /// <summary>Drawer height the recents were last fitted to, so a width change doesn't rebuild them.</summary>
    private double _recentsFitHeight;

    /// <summary>
    /// The last few folders the agent (Claude Code or Codex) was opened in, under the usage. Never
    /// more than five, and never more than fit in the drawer without scrolling. Clicking one opens it there.
    /// </summary>
    private bool ShowRecents(AgentItem item)
    {
        var id = item.Preset?.Agent ?? item.Definition.Id;
        _shownRecents = RecentLaunches.For(_state, id).Take(RecentLaunches.MaxCount).ToList();
        if (_shownRecents.Count == 0)
        {
            RecentSection.Visibility = Visibility.Collapsed;
            return false;
        }
        FillRecentRows(_shownRecents.Count);
        RecentSection.Visibility = Visibility.Visible;
        _recentsFitHeight = 0;
        DispatcherQueue.TryEnqueue(FitRecentsToDrawer);
        return true;
    }

    private void FillRecentRows(int count)
    {
        RecentRows.Children.Clear();
        foreach (var folder in _shownRecents.Take(count))
        {
            var shown = new FolderItem(folder);
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Tag = folder,
                Content = new StackPanel
                {
                    Spacing = 0,
                    Children =
                    {
                        new TextBlock { Text = shown.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 },
                        new TextBlock
                        {
                            Text = shown.Parent, FontSize = 12, Opacity = 0.7,
                            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200
                        }
                    }
                }
            };
            AutomationProperties.SetName(button, $"Open {_drawerItem?.Definition.Name ?? "the agent"} in {folder}");
            ToolTipService.SetToolTip(button, folder);
            button.Click += RecentFolder_Click;

            // Right-click, Shift+F10 or the menu key.
            var remove = new MenuFlyoutItem { Text = "Remove from recents", Icon = new FontIcon { Glyph = "" }, Tag = folder };
            remove.Click += RemoveRecent_Click;
            var menu = new MenuFlyout { Items = { remove } };
            // Shown by WinUI itself, so the hold starts as it opens.
            menu.Opening += (_, _) => HoldWhileOpen(menu);
            button.ContextFlyout = menu;
            RecentRows.Children.Add(button);
        }
    }

    /// <summary>
    /// Pop-ups open over the flyout: menus, confirmations, renaming. While any is open the drawer,
    /// the picture and the banner's colour hold.
    /// </summary>
    private int _popupsOpen;

    /// <summary>
    /// Call as <paramref name="flyout"/> is shown. A pop-up takes the pointer, so the row or drawer
    /// under it sees the pointer leave. Without this the drawer would start closing, and closing it
    /// resizes the window, which makes Windows close the pop-up (and the cursor spin meanwhile).
    /// When it closes, things settle as if the pointer had just left, unless it's still over a row
    /// or the drawer.
    /// </summary>
    private void HoldWhileOpen(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout)
    {
        _popupsOpen++;
        _drawerCloseTimer?.Stop();
        _bannerResetTimer?.Stop();

        void OnClosed(object? sender, object e)
        {
            flyout.Closed -= OnClosed;
            _popupsOpen = Math.Max(0, _popupsOpen - 1);
            // After any follow-up (Remove → its confirmation) has had the chance to open.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_popupsOpen > 0 || PointerOverRowOrDrawer())
                    return;
                _hoverItem = null;
                if (_state.ShowHoverBackgrounds && !_drawerOpen)
                    FadeBackdrop(null);
                ScheduleDrawerClose();
                ScheduleBannerReset();
            });
        }
        flyout.Closed += OnClosed;
    }

    /// <summary>Whether the mouse is over an agent row or the drawer right now (no pointer event says so after a pop-up).</summary>
    private bool PointerOverRowOrDrawer()
    {
        if (!AppWindow.IsVisible || Root.XamlRoot is null || !Win32.GetCursorPos(out var cursor) || !Win32.ScreenToClient(_hwnd, ref cursor))
            return false;
        var scale = Root.XamlRoot.RasterizationScale;
        var point = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale);
        foreach (var element in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(point, Root))
        {
            if (element == DrawerHost)
                return true;
            if (element is ListViewItem)
                return true;
        }
        return false;
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string folder } || _drawerItem is not { } item)
            return;
        var id = item.Preset?.Agent ?? item.Definition.Id;
        if (!RecentLaunches.Forget(_state, id, folder))
            return;
        _store.SaveState(_state);
        // The drawer shows what's left; with nothing left and no usage to show, it closes (or shows the overview).
        if (!ShowRecents(item) && UsagePanel.Visibility != Visibility.Visible && !UsageOffer.IsOpen && !ShowSummary())
            CloseDrawer(animate: true);
    }

    /// <summary>Drops recent rows from the bottom until the drawer doesn't have to scroll.</summary>
    private void FitRecentsToDrawer()
    {
        if (_shownRecents.Count == 0 || DrawerHost.ActualHeight <= 1)
            return;

        RecentSection.Visibility = Visibility.Visible;
        var count = _shownRecents.Count;
        FillRecentRows(count);
        while (count > 0 && !RecentsFit())
        {
            count--;
            FillRecentRows(count);
        }
        _recentsFitHeight = DrawerHost.ActualHeight;
        if (count == 0)
            RecentSection.Visibility = Visibility.Collapsed;
    }

    private bool RecentsFit()
    {
        DrawerPanel.UpdateLayout();
        if (RecentSection.ActualHeight <= 0)
            return true;
        try
        {
            var top = RecentSection.TransformToVisual(DrawerHost).TransformPoint(new Windows.Foundation.Point()).Y;
            return top + RecentSection.ActualHeight <= DrawerHost.ActualHeight + 0.5;
        }
        catch (ArgumentException)
        {
            // Not in the live tree yet; the next size change fits them.
            return true;
        }
    }

    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string folder } || _drawerItem is null)
            return;
        // The base agent, so a preset's own arguments aren't applied to a different folder.
        var id = _drawerItem.Preset?.Agent ?? _drawerItem.Definition.Id;
        var item = _agents.FirstOrDefault(a => a.Preset is null && a.Definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? _drawerItem;
        Launch(item, folder);
    }

    private async Task FetchCopilotUsageAsync()
    {
        _copilotFetching = true;
        try
        {
            _copilotUsage = await CopilotUsage.FetchAsync(); // resumes on the UI thread
        }
        finally
        {
            _copilotFetching = false;
        }
        // Still showing Copilot? Fill in what arrived.
        if (_drawerOpen && !_drawerClosing && _drawerItem is { } item && IsGitHubCopilot(item.Definition))
            ShowCopilotUsage();
        else if (_summaryShowing)
            ShowSummary();
    }

    /// <summary>
    /// Copilot's single monthly window: AI credits (or, on GitHub's legacy billing, premium
    /// requests) used against the plan's entitlement, as GitHub reports both. Without an entitlement
    /// (unlimited, or none given) it's just the count. Overage shows underneath when allowed.
    /// </summary>
    private void ShowCopilotUsage()
    {
        UsageTitle.Text = "Copilot usage";
        UsagePanel.Visibility = Visibility.Visible;
        WeekGroup.Visibility = Visibility.Collapsed;
        SessionLabel.Visibility = SessionText.Visibility = Visibility.Visible; // Grok hides them
        ShowUsageExtras(null, DateTimeOffset.UtcNow);

        var usage = _copilotUsage;
        var hasData = usage?.Used is not null;
        SessionLabel.Text = usage is { Unit.Length: > 0 } ? char.ToUpperInvariant(usage.Unit[0]) + usage.Unit[1..] : "Premium usage";
        UsageRows.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        UsageNote.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = usage?.Problem ?? "Checking GitHub…";
        UsageUpdatedText.Text = "";
        if (!hasData)
            return;

        var now = DateTimeOffset.UtcNow;
        // GitHub resets on the 1st at 00:00 UTC; its own date is used when it gives one.
        var reset = usage!.ResetsAt ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var used = usage.Used!.Value;
        var counts = System.Globalization.CultureInfo.CurrentCulture;
        if (usage.Unlimited)
        {
            SessionBar.Visibility = Visibility.Collapsed;
            SessionText.Text = $"Unlimited · {used.ToString("N0", counts)} used";
        }
        else if (usage.Entitlement is { } entitlement)
        {
            SessionBar.Visibility = Visibility.Visible;
            ShowWindow(SessionBar, SessionText,
                new UsageWindow { UsedPercentage = Math.Min(100, used * 100 / entitlement), ResetsAt = reset.ToUnixTimeSeconds() }, now);
            SessionText.Text = $"{used.ToString("N0", counts)} of {entitlement.ToString("N0", counts)} · resets {ResetTime(reset)}";
        }
        else
        {
            SessionBar.Visibility = Visibility.Collapsed;
            SessionText.Text = $"{used.ToString("N0", counts)} used · resets {ResetTime(reset)}";
        }

        var overage = usage.OveragePermitted
            ? usage.Overage > 0 ? $"Over the plan: {usage.Overage.ToString("N0", counts)} {usage.Unit}" : "Usage over the plan is allowed"
            : null;
        ExtraUsageText.Text = overage ?? "";
        ExtraUsageText.Visibility = overage is null ? Visibility.Collapsed : Visibility.Visible;
        UsageUpdatedText.Text = UpdatedAgo(usage.FetchedAt, now);
    }

    private void OfferUsage(InfoBarSeverity severity, string button, string message, bool closable)
    {
        UsagePanel.Visibility = Visibility.Collapsed;
        UsageOffer.Severity = severity;
        UsageOffer.Message = message;
        UsageOffer.IsClosable = closable;
        UsageOfferButton.Content = button;
        UsageOffer.IsOpen = true;
    }

    /// <summary>
    /// The session and week bars, or <paramref name="emptyNote"/> when there are none.
    /// <paramref name="note"/> goes under the bars (e.g. a check that failed while saved numbers show).
    /// </summary>
    private void ShowUsage(string title, PlanUsage? usage, string emptyNote, string? note = null, bool showSession = true,
        string weekLabel = "Week")
    {
        UsageTitle.Text = title;
        UsagePanel.Visibility = Visibility.Visible;
        SessionLabel.Text = "Session";
        WeekLabel.Text = weekLabel; // Cursor's is the month's total
        SessionBar.Visibility = WeekGroup.Visibility = Visibility.Visible;
        // Grok has only a weekly pool.
        SessionLabel.Visibility = SessionText.Visibility = showSession ? Visibility.Visible : Visibility.Collapsed;
        if (!showSession)
            SessionBar.Visibility = Visibility.Collapsed;
        ShowUsageExtras(null, DateTimeOffset.UtcNow);
        var hasData = usage is { FiveHour: not null } or { SevenDay: not null };
        UsageRows.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        var text = hasData ? note : emptyNote;
        UsageNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = text ?? "";
        UsageUpdatedText.Text = "";
        if (!hasData)
            return;

        var now = DateTimeOffset.UtcNow;
        ShowWindow(SessionBar, SessionText, usage!.FiveHour, now);
        ShowWindow(WeekBar, WeekText, usage.SevenDay, now);
        ShowUsageExtras(usage, now);
        // Both agents only record usage while a session runs, so say how old this is.
        UsageUpdatedText.Text = UpdatedAgo(DateTimeOffset.FromUnixTimeSeconds(usage.UpdatedAt), now);
    }

    /// <summary>
    /// What's known beyond the session and week: the plan (Codex) or last model (Claude) under the
    /// title, a bar for each per-model week (Claude), and extra usage or credits. Null clears them.
    /// </summary>
    private void ShowUsageExtras(PlanUsage? usage, DateTimeOffset now)
    {
        var plan = usage?.Plan is { Length: > 0 } name
            ? $"{char.ToUpperInvariant(name[0])}{name[1..].Replace('_', ' ')} plan"
            : null;
        var subtitle = plan ?? (usage?.Model is { Length: > 0 } model ? $"{model}, last used" : null);
        UsageSubtitle.Text = subtitle ?? "";
        UsageSubtitle.Visibility = subtitle is null ? Visibility.Collapsed : Visibility.Visible;

        ModelWeekRows.Children.Clear();
        foreach (var week in usage?.ModelWeeks ?? [])
        {
            var bar = new ProgressBar { Maximum = 100 };
            var text = new TextBlock
            {
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            var heading = week.Title ?? $"{week.Label} week";
            AutomationProperties.SetName(bar, $"{heading} usage");
            ShowWindow(bar, text, week, now);
            var group = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            group.Children.Add(new TextBlock { Text = heading });
            group.Children.Add(bar);
            group.Children.Add(text);
            ModelWeekRows.Children.Add(group);
        }

        var extra = usage?.Extra is { Enabled: true } on
            ? on.Unlimited ? "Extra usage credits: unlimited"
            : on.Balance is { Length: > 0 } balance ? $"Extra usage credits: {balance}"
            : on.Dollars && on.Used is { } dollars ? on.Limit is { } cap
                ? $"On-demand usage: {Dollars(dollars)} of {Dollars(cap)}"
                : $"On-demand usage: {Dollars(dollars)}"
            : on.Used is { } used && on.Limit is { } limit ? $"Extra usage: {used:0.##} of {limit:0.##} used"
            : on.Used is { } spent ? $"Extra usage: {spent:0.##} used"
            : "Extra usage is on"
            : null;
        ExtraUsageText.Text = extra ?? "";
        ExtraUsageText.Visibility = extra is null ? Visibility.Collapsed : Visibility.Visible;

        ShowBreakdown(usage?.Breakdown);
    }

    private static string Dollars(double amount) =>
        amount.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    /// <summary>Slice colours, in order of size: distinct in both themes, and not the warning or error colours.</summary>
    private static readonly Windows.UI.Color[] SliceColors =
    [
        Windows.UI.Color.FromArgb(255, 0x4F, 0x8E, 0xF7), // blue
        Windows.UI.Color.FromArgb(255, 0x9B, 0x7B, 0xF2), // violet
        Windows.UI.Color.FromArgb(255, 0x3F, 0xB6, 0x8B), // green
        Windows.UI.Color.FromArgb(255, 0xE8, 0xA3, 0x3D), // amber
        Windows.UI.Color.FromArgb(255, 0xE0, 0x6C, 0x9F), // pink
        Windows.UI.Color.FromArgb(255, 0x8A, 0x96, 0xA3), // grey
    ];

    /// <summary>
    /// What the usage went on, as a pie with a legend beside it (name and share). Hovering a slice
    /// names it too. Hidden when there's no breakdown.
    /// </summary>
    private void ShowBreakdown(List<UsageShare>? shares)
    {
        BreakdownPie.Children.Clear();
        BreakdownLegend.Children.Clear();
        var parts = (shares ?? []).Where(s => s.Percent > 0).OrderByDescending(s => s.Percent).ToList();
        var total = parts.Sum(p => p.Percent);
        BreakdownChart.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (total <= 0)
            return;

        const double size = 64, radius = size / 2;
        var centre = new Windows.Foundation.Point(radius, radius);
        var angle = -90.0; // start at twelve o'clock, go clockwise
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(SliceColors[Math.Min(i, SliceColors.Length - 1)]);
            var sweep = part.Percent / total * 360;
            Microsoft.UI.Xaml.Media.Geometry geometry;
            if (sweep >= 359.99)
            {
                geometry = new Microsoft.UI.Xaml.Media.EllipseGeometry { Center = centre, RadiusX = radius, RadiusY = radius };
            }
            else
            {
                var figure = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = centre, IsClosed = true, IsFilled = true };
                figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = PointOn(angle) });
                figure.Segments.Add(new Microsoft.UI.Xaml.Media.ArcSegment
                {
                    Point = PointOn(angle + sweep),
                    Size = new Windows.Foundation.Size(radius, radius),
                    SweepDirection = Microsoft.UI.Xaml.Media.SweepDirection.Clockwise,
                    IsLargeArc = sweep > 180
                });
                var path = new Microsoft.UI.Xaml.Media.PathGeometry();
                path.Figures.Add(figure);
                geometry = path;
            }
            var slice = new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = brush };
            ToolTipService.SetToolTip(slice, $"{part.Label}: {Math.Round(part.Percent)}%");
            BreakdownPie.Children.Add(slice);
            angle += sweep;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = brush, VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{part.Label} {Math.Round(part.Percent)}%",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            BreakdownLegend.Children.Add(row);
        }
        AutomationProperties.SetHelpText(BreakdownPie,
            string.Join(", ", parts.Select(p => $"{p.Label} {Math.Round(p.Percent)}%")));

        Windows.Foundation.Point PointOn(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Windows.Foundation.Point(radius + radius * Math.Cos(radians), radius + radius * Math.Sin(radians));
        }
    }

    private static void ShowWindow(ProgressBar bar, TextBlock text, UsageWindow? window, DateTimeOffset now)
    {
        bar.ShowError = bar.ShowPaused = false;
        if (window is null)
        {
            bar.Value = 0;
            text.Text = "No data yet";
            return;
        }

        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt);
        if (window.ResetsAt > 0 && resetsAt <= now)
        {
            bar.Value = 0;
            text.Text = "0% · reset";
            return;
        }

        var used = Math.Clamp(window.UsedPercentage, 0, 100);
        bar.Value = used;
        // Windows' own warning and error colours for the bar.
        bar.ShowError = used >= 90;
        bar.ShowPaused = used is >= 75 and < 90;
        text.Text = window.ResetsAt > 0 ? $"{Math.Round(used)}% · resets {ResetTime(resetsAt)}" : $"{Math.Round(used)}%";
    }

    private static string ResetTime(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var days = (local.Date - DateTime.Today).Days;
        // "t" is the short time only as a standard format on its own; inside "ddd t" it means "A"/"P".
        return days == 0 ? local.ToString("t") : days < 7 ? $"{local:ddd} {local:t}" : local.ToString("MMM d");
    }

    internal static string UpdatedAgo(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        return age.TotalMinutes < 1 ? "Updated just now"
            : age.TotalHours < 1 ? $"Updated {(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"Updated {(int)age.TotalHours} hr ago"
            : $"Updated {when.ToLocalTime():MMM d}";
    }

    /// <summary>"claude" for "claude", "claude.exe" or a quoted full path to it.</summary>
    internal static string? CommandName(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : Path.GetFileNameWithoutExtension(command.Trim().Trim('"')).ToLowerInvariant();

    /// <summary>The script or program a status line command runs, e.g. "waifuops-statusline.ps1".</summary>
    private static string DescribeCommand(string command)
    {
        var script = command.Split([' ', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(t => Path.GetExtension(t).ToLowerInvariant() is ".ps1" or ".sh" or ".js" or ".mjs" or ".py" or ".exe" or ".cmd" or ".bat");
        return script is not null ? Path.GetFileName(script.Replace('/', '\\'))
            : command.Length <= 40 ? command : command[..39] + "…";
    }

    /// <summary>Sets up (or updates) the status line for the drawer's Claude account: its own settings.json.</summary>
    private void UsageOfferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_drawerItem is null || UsageAccount.For(_drawerItem.Definition) is not { Agent: "claude" } account)
            return;
        if (ClaudeStatusLine.Install(Environment.ProcessPath!, account.ConfigDir) is { } error)
        {
            SetStatus(error);
            return;
        }
        ClearUsageCache();
        PrepareDrawer(_drawerItem);
        StatusText.Text = account.IsDefault
            ? "Claude Code now uses Agent Dock as its status line."
            : $"{_drawerItem.Definition.Name} now uses Agent Dock as its status line.";
        PlaceWindow(); // the footer message may wrap
    }

    private void UsageOffer_CloseButtonClick(InfoBar sender, object args)
    {
        if (_drawerItem is not null && UsageAccount.For(_drawerItem.Definition) is { Agent: "claude" } account)
        {
            if (account.IsDefault)
                _state.ClaudeUsageOfferDismissed = true;
            else if (!OfferDismissed(account))
                _state.ClaudeUsageOfferDismissedFor.Add(account.ConfigDir);
            _store.SaveState(_state);
        }
        UsageOffer.IsOpen = false;
        if ((_drawerItem is null || !ShowRecents(_drawerItem)) && !ShowSummary())
            CloseDrawer(animate: true); // nothing left to show in it
    }

    // ---------- Hover background ----------

    /// <summary>
    /// One agent's picture. Two layers, so moving from one agent to another cross-fades.
    /// The picture is a single image across the whole window; the window growing uncovers more of it.
    /// </summary>
    private sealed class BackdropLayer(Image image)
    {
        public Image Image { get; } = image;
        public string? Path { get; set; }
    }

    /// <summary>
    /// Where the subject of the built-in pictures stands, as a fraction of the picture's width
    /// (measured on the Amp and Claude pictures: 0.72–0.73).
    /// </summary>
    private const double BackdropSubjectX = 0.72;

    private readonly Dictionary<string, BitmapImage> _backdrops = new(StringComparer.OrdinalIgnoreCase);
    private BackdropLayer[]? _layers;
    private int _activeLayer;
    private Storyboard? _backdropFade;

    private BackdropLayer[] Layers => _layers ??=
        [new BackdropLayer(HoverImageA), new BackdropLayer(HoverImageB)];

    /// <summary>The row under the pointer, if any.</summary>
    private AgentItem? _hoverItem;

    private void AgentRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var item = ((FrameworkElement)sender).DataContext as AgentItem;
        _hoverItem = item;
        ColorBannerFor(item);
        var previous = _drawerOpen && !_drawerClosing ? _drawerItem : null;
        var fromSummary = _drawerOpen && !_drawerClosing && _summaryShowing;
        var drawer = item is not null && PrepareDrawer(item);
        // Switching an open drawer to another agent (or from the overview): fade its new content in rather than swapping it.
        if (drawer && (fromSummary || (previous is not null
            && CommandName(previous.Definition.Command) != CommandName(item!.Definition.Command))))
            FadeInDrawerContent();
        // The drawer first: the image layout depends on which side it opens.
        if (drawer)
            OpenDrawer();
        else
            ScheduleDrawerClose();
        // The settings page can turn the pictures off. The usage drawer is separate and still opens.
        if (_state.ShowHoverBackgrounds)
            FadeBackdrop(item?.BackgroundSource);
    }

    private void AgentRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverItem = null;
        ScheduleBannerReset();
        // Moving into the drawer should keep the picture. It fades when the drawer actually closes,
        // or straight away when the row had no drawer.
        if (_state.ShowHoverBackgrounds && !_drawerOpen && _popupsOpen == 0)
            FadeBackdrop(null);
        ScheduleDrawerClose();
    }

    /// <summary>
    /// Fades the given picture in behind the flyout, cross-fading from whatever picture was showing,
    /// or fades out when <paramref name="path"/> is null. Where it sits follows the drawer (LayoutBackdrop).
    /// </summary>
    private void FadeBackdrop(string? path)
    {
        var layers = Layers;
        if (path is not null)
        {
            if (!_backdrops.TryGetValue(path, out var image))
            {
                try
                {
                    _backdrops[path] = image = new BitmapImage();
                    // The layout needs the picture's proportions, which are known once it has loaded.
                    image.ImageOpened += (_, _) => LayoutLayersShowing(image);
                    if (AgentIcons.IsBundledBackground(path))
                        _ = LoadBundledBackgroundAsync(image, path);
                    else
                        image.UriSource = new Uri(path);
                }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException)
                {
                    return;
                }
            }

            // Moving back onto the agent already showing (its fade-out not finished) fades the same
            // layer back in; a different agent's picture goes on the other layer.
            if (!string.Equals(layers[_activeLayer].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _activeLayer = 1 - _activeLayer;
                var incoming = layers[_activeLayer];
                incoming.Path = path;
                incoming.Image.Source = image;
            }
            LayoutBackdrop(layers[_activeLayer]);
        }

        // Targets: the active layer to the picture's opacity (or 0), the other always to 0.
        var targets = layers.Select((layer, i) => (Layer: layer, To: path is not null && i == _activeLayer ? HoverBackdropOpacity : 0)).ToList();

        // Stopping a storyboard snaps back to the values before it ran, so carry the current ones over.
        var current = targets.Select(t => t.Layer.Image.Opacity).ToList();
        _backdropFade?.Stop();
        for (var i = 0; i < targets.Count; i++)
            targets[i].Layer.Image.Opacity = current[i];

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled || !AppWindow.IsVisible)
        {
            foreach (var (layer, to) in targets)
                layer.Image.Opacity = to;
            return;
        }

        var storyboard = new Storyboard();
        for (var i = 0; i < targets.Count; i++)
        {
            var (layer, to) = targets[i];
            // Fading in is slower than fading out (tuned with the owner).
            var duration = new Duration(TimeSpan.FromMilliseconds(to > current[i] ? 900 : 600));
            var fade = new DoubleAnimation
            {
                From = current[i], To = to, Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, layer.Image);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
        }
        _backdropFade = storyboard;
        storyboard.Begin();
    }

    private Storyboard? _drawerContentFade;

    private void FadeInDrawerContent()
    {
        _drawerContentFade?.Stop();
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            DrawerPanel.Opacity = 1;
            return;
        }
        var fade = new DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, DrawerPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _drawerContentFade = new Storyboard { Children = { fade } };
        _drawerContentFade.Begin();
    }

    /// <summary>A compiled-in background, decoded from the exe's resources.</summary>
    private async Task LoadBundledBackgroundAsync(BitmapImage image, string source)
    {
        try
        {
            using var stream = AgentIcons.OpenBundledBackground(source);
            if (stream is null)
                return;
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            LayoutLayersShowing(image);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A missing picture just means no background.
        }
    }

    private void LayoutLayersShowing(BitmapImage image)
    {
        foreach (var layer in Layers.Where(l => l.Image.Source == image))
            LayoutBackdrop(layer);
    }

    /// <summary>Lays out every layer again, e.g. when the drawer opens on a different side.</summary>
    private void LayoutBackdrop()
    {
        foreach (var layer in Layers)
            LayoutBackdrop(layer);
    }

    /// <summary>
    /// Places a picture by how far the drawer is open, whichever agent it belongs to. Closed: the
    /// subject is centred in the list. Open: the picture spans the list and the drawer. In between
    /// it slides with the drawer, so switching to an agent without a drawer doesn't leave the
    /// closing drawer empty, and the reverse doesn't jump (owner's request).
    /// </summary>
    private void LayoutBackdrop(BackdropLayer layer)
    {
        if (layer.Image.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } image)
            return;

        // Always big enough for the list and an open drawer, so sliding never needs a zoom.
        var height = Root.ActualHeight;
        var scale = Math.Max((ListWidthDip + DrawerWidthDip) / image.PixelWidth, height / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var imageHeight = image.PixelHeight * scale;

        // Both ends in list coordinates (0 = the list's left edge, which never moves on screen).
        // Closed: subject over the middle of the list (past the banner). Open: subject over the middle
        // of list and drawer together (the drawer is right of the list, or left at negative x). Each
        // only as far as the picture still covers what's on screen. Pinning the open picture's edge to
        // the window's instead left the subject well left of centre (owner's screenshot, 2026-10-19).
        var closed = Math.Clamp(BannerWidthDip + (ListWidthDip - BannerWidthDip) / 2 - width * BackdropSubjectX,
            ListWidthDip - width, 0);
        var spanLeft = _drawerSide > 0 ? 0 : -DrawerWidthDip;
        var spanRight = spanLeft + ListWidthDip + DrawerWidthDip;
        var open = Math.Clamp((spanLeft + spanRight + BannerWidthDip) / 2 - width * BackdropSubjectX,
            spanRight - width, spanLeft);
        var drawer = DrawerDip();
        var t = Math.Clamp(drawer / DrawerWidthDip, 0, 1);
        var leftInList = closed + (open - closed) * t;
        // On the left the drawer comes first in the window, so the list starts that far in.
        var listLeft = _drawerOpen && _drawerSide < 0 ? drawer : 0;

        layer.Image.Width = width;
        layer.Image.Height = imageHeight;
        layer.Image.Margin = new Thickness(listLeft + leftInList, (height - imageHeight) / 2, 0, 0);
    }

    // ---------- Drawer ----------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerCloseTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerSlideTimer;
    private readonly Stopwatch _drawerSlideWatch = new();
    private double _drawerSlideFrom;
    private double _drawerSlideTo;
    private int _drawerSlideMs;
    private Action? _drawerSlideCompleted;
    private bool _drawerOpen;
    private bool _drawerClosing;
    /// <summary>+1 when the drawer opens to the right of the list, -1 to the left.</summary>
    private int _drawerSide = 1;
    /// <summary>Screen X of the list's left edge, so growing the window doesn't slide the list.</summary>
    private int _listScreenX;

    private void Drawer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _drawerCloseTimer?.Stop();
        // The drawer belongs to the agent, so the banner keeps its colour.
        _bannerResetTimer?.Stop();
        // The row's pointer-exit may already have started fading the picture. Bring it back.
        if (_state.ShowHoverBackgrounds && _drawerItem?.BackgroundSource is { } source)
            FadeBackdrop(source);
    }

    private void Drawer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ScheduleDrawerClose();
        ScheduleBannerReset();
    }

    /// <summary>The animated width changed, so the window grows or shrinks with it.</summary>
    private void OnDrawerWidthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!_drawerOpen || !AppWindow.IsVisible)
            return;
        ApplyWindowForDrawer();
        // The pictures slide with the drawer (LayoutBackdrop), in the same step as the window.
        if (_layers is not null)
            LayoutBackdrop();
    }

    /// <summary>The column was arranged, so a picture that covers it needs laying out again.</summary>
    private void DrawerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layers is not null)
            LayoutBackdrop();
        if (Math.Abs(e.NewSize.Height - _recentsFitHeight) > 0.5)
            FitRecentsToDrawer();
    }

    /// <summary>
    /// A short grace period, so the pointer can move from the row into the drawer (to click
    /// Set up) without it closing. Then: over an agent without a drawer, the drawer closes (the
    /// window shrinks to the list); over no agent, the usage overview shows if it's on, else it
    /// closes. So this also runs with the drawer shut, to bring the overview back.
    /// </summary>
    private void ScheduleDrawerClose()
    {
        if (_popupsOpen > 0 || (!_drawerOpen && !_state.ShowUsageSummary))
            return;
        if (_drawerCloseTimer is null)
        {
            _drawerCloseTimer = DispatcherQueue.CreateTimer();
            _drawerCloseTimer.Interval = TimeSpan.FromMilliseconds(250);
            _drawerCloseTimer.IsRepeating = false;
            _drawerCloseTimer.Tick += (_, _) =>
            {
                // The pointer may have moved onto an agent without a drawer, whose picture is fading in.
                if (_state.ShowHoverBackgrounds && _hoverItem is null)
                    FadeBackdrop(null);
                // The overview is for when no agent is pointed at (owner, 2026-10-16).
                if (_hoverItem is null && ShowSummary())
                    return;
                CloseDrawer(animate: true);
            };
        }
        // Restart the grace period from now.
        _drawerCloseTimer.Stop();
        _drawerCloseTimer.Start();
    }

    private void OpenDrawer()
    {
        _drawerCloseTimer?.Stop();
        if (_drawerOpen && !_drawerClosing)
            return;

        if (!_drawerOpen)
        {
            // Right of the list if it fits on this monitor, otherwise left.
            var info = Win32.MonitorInfoAt(_anchor, out var monitor);
            var scale = info is not null ? Win32.ScaleFor(monitor) : Root.XamlRoot.RasterizationScale;
            var right = AppWindow.Position.X + AppWindow.Size.Width + (int)Math.Ceiling(DrawerWidthDip * scale);
            _drawerSide = info is { } work && right > work.rcWork.Right ? -1 : 1;
            var onRight = _drawerSide > 0;

            // Content stays against the list. The window growing uncovers the rest.
            Grid.SetColumn(ListColumn, onRight ? 0 : 1);
            Grid.SetColumn(DrawerHost, onRight ? 1 : 0);
            DrawerPanel.HorizontalAlignment = onRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            // The toolbar stays with the list, rather than riding the window's far edge.
            FooterContent.HorizontalAlignment = onRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _listScreenX = AppWindow.Position.X;
            DrawerHost.Width = 0;
            DrawerHost.Visibility = Visibility.Visible;
            _drawerOpen = true;
            LayoutBackdrop();
        }
        _drawerClosing = false;
        SlideDrawer(DrawerWidthDip, () =>
        {
            // The wider toolbar may have unwrapped the status line, so fit the height again.
            if (_drawerOpen && !_drawerClosing && AppWindow.IsVisible)
                PlaceWindow();
        });
    }

    private void CloseDrawer(bool animate)
    {
        _drawerCloseTimer?.Stop();
        if (!_drawerOpen)
            return;

        if (!animate || !AppWindow.IsVisible)
        {
            StopDrawerSlide();
            FinishDrawerClose();
            return;
        }

        _drawerClosing = true;
        // Shrinks back to the list, then closes, unless it was reopened meanwhile.
        SlideDrawer(0, () =>
        {
            if (_drawerClosing)
                FinishDrawerClose();
        });
    }

    private void FinishDrawerClose()
    {
        _drawerClosing = false;
        _drawerOpen = false;
        _summaryShowing = false;
        SummaryPanel.Visibility = Visibility.Collapsed;
        DrawerHost.Width = 0;
        DrawerHost.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ListColumn, 0);
        Grid.SetColumn(DrawerHost, 1);
        FooterContent.HorizontalAlignment = HorizontalAlignment.Left;
        // Back to the list on its own, including height: the status line may need to wrap again.
        if (AppWindow.IsVisible)
            PlaceWindow();
    }

    /// <summary>
    /// Animates the drawer's column. A storyboard with <c>EnableDependentAnimation</c> changed the
    /// width during layout and the list slid sideways while the window grew, so this steps the width
    /// from the dispatcher instead. Each step resizes the window to match.
    /// </summary>
    private void SlideDrawer(double toWidth, Action? completed)
    {
        StopDrawerSlide();
        var from = double.IsNaN(DrawerHost.Width) ? 0 : DrawerHost.Width;

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled || Math.Abs(toWidth - from) < 0.5)
        {
            DrawerHost.Width = toWidth;
            ApplyWindowForDrawer();
            completed?.Invoke();
            return;
        }

        _drawerSlideFrom = from;
        _drawerSlideTo = toWidth;
        _drawerSlideMs = toWidth > from ? 250 : 200;
        _drawerSlideCompleted = completed;
        _drawerSlideWatch.Restart();
        _drawerSlideTimer ??= CreateDrawerSlideTimer();
        _drawerSlideTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateDrawerSlideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp(_drawerSlideWatch.Elapsed.TotalMilliseconds / _drawerSlideMs, 0, 1);
            var opening = _drawerSlideTo > _drawerSlideFrom;
            // Cubic ease-out while opening, ease-in while closing. Same curve as the old storyboard.
            var eased = opening ? 1 - Math.Pow(1 - t, 3) : Math.Pow(t, 3);
            DrawerHost.Width = _drawerSlideFrom + (_drawerSlideTo - _drawerSlideFrom) * eased;
            if (t < 1)
                return;
            var completed = _drawerSlideCompleted;
            StopDrawerSlide();
            completed?.Invoke();
        };
        return timer;
    }

    /// <summary>Drops the slide in progress without running its completion.</summary>
    private void StopDrawerSlide()
    {
        _drawerSlideTimer?.Stop();
        _drawerSlideCompleted = null;
    }

    /// <summary>
    /// Sets the window to the list plus whatever width the drawer column has now. The list's left
    /// edge stays where <see cref="PlaceWindow"/> put it; opening left moves the window's left edge.
    /// </summary>
    private void ApplyWindowForDrawer()
    {
        if (!AppWindow.IsVisible || Root.XamlRoot is null)
            return;
        if (Win32.MonitorInfoAt(_anchor, out var monitor) is not { })
            return;

        var scale = Win32.ScaleFor(monitor);
        var drawerPx = (int)Math.Round(DrawerDip() * scale);
        var borderW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var w = (int)Math.Ceiling(ListWidthDip * scale) + drawerPx + borderW;
        var x = _drawerSide > 0 || drawerPx == 0 ? _listScreenX : _listScreenX - drawerPx;
        var pos = AppWindow.Position;
        if (pos.X == x && AppWindow.Size.Width == w)
            return;
        AppWindow.MoveAndResize(new RectInt32(x, pos.Y, w, AppWindow.Size.Height));
    }

    private double DrawerDip()
    {
        if (DrawerHost.Visibility != Visibility.Visible)
            return 0;
        // Width is what the animation sets. ActualWidth can lag while the window is still catching up.
        var width = double.IsNaN(DrawerHost.Width) ? DrawerHost.ActualWidth : DrawerHost.Width;
        return double.IsNaN(width) || width < 0 ? 0 : width;
    }

    // ---------- Placement and motion ----------

    /// <summary>
    /// Sizes the window to its content and places it just above (or beside) the taskbar where it
    /// was clicked, clamped to that monitor's work area. If opened some other way, it centres on
    /// the screen. Works in physical pixels so mixed-DPI setups land correctly.
    /// </summary>
    private void PlaceWindow()
    {
        if (Win32.MonitorInfoAt(_anchor, out var monitor) is not { } info)
            return;

        var scale = Win32.ScaleFor(monitor);
        var work = info.rcWork;
        var gap = (int)Math.Round(12 * scale);

        // Position the list as its own window, then add the drawer on beside it. The anchor math
        // stays about the list, so opening the drawer doesn't drag the list off the taskbar button.
        var drawerDip = DrawerDip();
        Root.Measure(new Windows.Foundation.Size(ListWidthDip + drawerDip, double.PositiveInfinity));
        var workHeightDip = (work.Height - 2 * gap) / scale;
        var fitDip = Root.DesiredSize.Height > 1 ? Root.DesiredSize.Height : AppWindow.ClientSize.Height / scale;
        // The height the user dragged to (the list scrolls if its content is taller); without one,
        // the list fits its content.
        var heightDip = _state.FlyoutHeight is { } chosen
            ? Math.Clamp(chosen, Math.Min(MinHeightDip, workHeightDip), workHeightDip)
            : Math.Min(fitDip, Math.Min(MaxHeightDip, workHeightDip));
        // The grip goes on the edge away from the taskbar; the edge by the taskbar stays put.
        _gripAtTop = !(_anchor.Y < work.Top && !work.Contains(_anchor));
        HeightGrip.VerticalAlignment = _gripAtTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // Window size includes the thin border around the client area.
        var borderW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var borderH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var listW = (int)Math.Ceiling(ListWidthDip * scale) + borderW;
        var h = (int)Math.Ceiling(heightDip * scale) + borderH;

        int x, y;
        if (work.Contains(_anchor))
        {
            x = work.Left + (work.Width - listW) / 2;
            y = work.Top + (work.Height - h) / 2;
        }
        else if (_anchor.Y >= work.Bottom) { x = _anchor.X - listW / 2; y = work.Bottom - h - gap; }
        else if (_anchor.Y < work.Top)     { x = _anchor.X - listW / 2; y = work.Top + gap; }
        else if (_anchor.X < work.Left)    { x = work.Left + gap;       y = _anchor.Y - h / 2; }
        else                               { x = work.Right - listW - gap; y = _anchor.Y - h / 2; }

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - listW - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - h - gap));

        _listScreenX = x;
        var drawerPx = (int)Math.Round(drawerDip * scale);
        var w = listW + drawerPx;
        if (drawerPx > 0 && _drawerSide < 0)
            x -= drawerPx;

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    // ---------- Resizing the height ----------

    private bool _gripAtTop = true;
    private bool _resizing;
    private int _resizeStartCursorY;
    private RectInt32 _resizeStart;

    private void HeightGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!HeightGrip.CapturePointer(e.Pointer) || !Win32.GetCursorPos(out var cursor))
            return;
        // Screen coordinates throughout: the grip moves with the window while it's dragged.
        _resizeStartCursorY = cursor.Y;
        _resizeStart = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _resizing = true;
        e.Handled = true;
    }

    private void HeightGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing || !Win32.GetCursorPos(out var cursor) || Win32.MonitorInfoAt(_anchor, out var monitor) is not { } info)
            return;

        var scale = Win32.ScaleFor(monitor);
        var work = info.rcWork;
        var gap = (int)Math.Round(12 * scale);
        var borderH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var minH = (int)Math.Ceiling(MinHeightDip * scale) + borderH;
        var dy = cursor.Y - _resizeStartCursorY;

        int y, h;
        if (_gripAtTop)
        {
            var bottom = _resizeStart.Y + _resizeStart.Height;
            h = Math.Clamp(_resizeStart.Height - dy, minH, Math.Max(minH, bottom - (work.Top + gap)));
            y = bottom - h;
        }
        else
        {
            h = Math.Clamp(_resizeStart.Height + dy, minH, Math.Max(minH, work.Bottom - gap - _resizeStart.Y));
            y = _resizeStart.Y;
        }
        if (h != AppWindow.Size.Height)
            AppWindow.MoveAndResize(new RectInt32(AppWindow.Position.X, y, AppWindow.Size.Width, h));
        e.Handled = true;
    }

    private void HeightGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        HeightGrip.ReleasePointerCapture(e.Pointer);
        EndResize();
    }

    private void HeightGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndResize();

    /// <summary>Remembers the height the list was dragged to.</summary>
    private void EndResize()
    {
        if (!_resizing)
            return;
        _resizing = false;
        if (Win32.MonitorInfoAt(_anchor, out var monitor) is null)
            return;
        _state.FlyoutHeight = Math.Round(AppWindow.ClientSize.Height / Win32.ScaleFor(monitor));
        _store.SaveState(_state);
    }

    private void HeightGrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _state.FlyoutHeight = null;
        _store.SaveState(_state);
        PlaceWindow();
        e.Handled = true;
    }

    private void PlayOpenAnimation()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;

        var fromTop = Win32.MonitorInfoAt(_anchor, out _) is { } info && _anchor.Y < info.rcWork.Top;
        var duration = new Duration(TimeSpan.FromMilliseconds(250));

        var slide = new DoubleAnimation
        {
            From = fromTop ? -20 : 20,
            To = 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, RootTranslate);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard { Children = { slide, fade } };
        storyboard.Begin();
    }
}
