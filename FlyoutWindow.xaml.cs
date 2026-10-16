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
    /// <summary>False while the settings switch is being set from state, so that doesn't count as a change.</summary>
    private bool _settingsToggleReady;
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

        // A desktop agent in the list that wasn't found may have been installed since: look every time.
        if (!_sinceShellRefresh.IsRunning || _sinceShellRefresh.Elapsed > ShellRefreshInterval
            || AllItems.Any(a => a.Icon is null && a.IconSource is not null)
            || _agents.Any(a => !a.IsCli && !a.IsAgentAvailable))
            _ = RefreshFromShellAsync(_generation);
    }

    private void HideFlyout(bool byDeactivation = false)
    {
        if (!AppWindow.IsVisible)
            return;
        _hiddenByDeactivation = byDeactivation;
        _sinceHide.Restart();
        AppWindow.Hide();
        SyncHearts();
        // Don't reopen with the last hover's image, the drawer, or the settings page showing.
        FadeBackdrop(null);
        CloseDrawer(animate: false);
        ShowMainPage();
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

    /// <summary>The agent's picture when waifus are on (and it has one), otherwise its logo.</summary>
    private string? ThemeSourceFor(AgentItem item) =>
        _state.ShowHoverBackgrounds && item.BackgroundSource is { } background ? background : item.IconSource;

    /// <summary>
    /// The agent's "color" setting, else the colour of its picture (or logo, with waifus off): hand-picked
    /// for built-in pictures, worked out from the image otherwise.
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
            _floatTimer = DispatcherQueue.CreateTimer();
            _floatTimer.Interval = TimeSpan.FromSeconds(HeartbeatSeconds);
            _floatTimer.IsRepeating = true;
            _floatTimer.Tick += (_, _) => FloatHeart();
        }
        _floatTimer.Start();
    }

    private void StopHearts()
    {
        _heartbeat?.Stop();
        _floatTimer?.Stop();
        foreach (var floater in BannerCanvas.Children.OfType<TextBlock>().Where(t => t.Tag is "float").ToList())
            BannerCanvas.Children.Remove(floater);
    }

    /// <summary>A small sideways heart that drifts up the banner from the big one and fades away.</summary>
    private void FloatHeart()
    {
        var heartTop = Canvas.GetTop(BannerHeart) + BannerHeart.DesiredSize.Height / 2 - BannerHeart.DesiredSize.Width / 2;
        var room = heartTop - 8;
        if (room < 30)
            return;

        var transform = new Microsoft.UI.Xaml.Media.CompositeTransform { Rotation = -90 };
        var heart = new TextBlock
        {
            Text = BannerHeart.Text, FontSize = 8 + _random.Next(5), Tag = "float", Opacity = 0,
            IsHitTestVisible = false, RenderTransform = transform
        };
        heart.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = heart.DesiredSize;
        transform.CenterX = size.Width / 2;
        transform.CenterY = size.Height / 2;
        Canvas.SetLeft(heart, BannerWidthDip / 2 - size.Width / 2);
        Canvas.SetTop(heart, heartTop - size.Height / 2);
        BannerCanvas.Children.Add(heart);

        var duration = TimeSpan.FromSeconds(2.2);
        var storyboard = new Storyboard();
        var rise = new DoubleAnimation
        {
            To = -Math.Min(room, 70 + _random.Next(60)), Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "TranslateY");
        var sway = new DoubleAnimation
        {
            From = -3, To = 3, Duration = TimeSpan.FromSeconds(0.55), AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2), BeginTime = TimeSpan.FromSeconds(_random.NextDouble() * 0.3)
        };
        Storyboard.SetTarget(sway, transform);
        Storyboard.SetTargetProperty(sway, "TranslateX");
        var fade = new DoubleAnimationUsingKeyFrames();
        foreach (var (seconds, opacity) in new[] { (0.0, 0.0), (0.3, 0.9), (1.2, 0.7), (duration.TotalSeconds, 0.0) })
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), Value = opacity });
        Storyboard.SetTarget(fade, heart);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(rise);
        storyboard.Children.Add(sway);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => BannerCanvas.Children.Remove(heart);
        storyboard.Begin();
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

                if (_state.AgentSetupDone && MainPage.Visibility == Visibility.Visible)
                {
                    var wasOpen = NewAgentOffer.IsOpen;
                    OfferNewAgents(newlyFound);
                    if (AppWindow.IsVisible && wasOpen != NewAgentOffer.IsOpen)
                        PlaceWindow();
                }
            }

            JumpList.Update(_presets, _cli.Concat(_desktop), CurrentFolder);
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

        var shortcut = 1;
        foreach (var agent in AllItems)
            agent.Shortcut = agent.IsAvailable && shortcut <= 9 ? (shortcut++).ToString() : null;

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

    private void FindAgents_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsPage();
        _addAgentsFromSetup = true;
        ShowAddAgents();
    }

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

    /// <summary>
    /// Known agents not in the list yet, resolved, so <see cref="AgentItem.IsAgentAvailable"/> says
    /// whether each is installed. The Start menu is read on an STA thread, so this can take a moment.
    /// If reading the Start menu fails, desktop apps come back not installed, so they can still be added.
    /// </summary>
    private async Task<List<AgentItem>> FindNewAgentsAsync()
    {
        var known = KnownAgents.All().Where(k => !KnownAgents.IsInList(k, _settings.Agents)).ToList();
        try
        {
            return await StartMenuApps.RunOnStaThread(() =>
                AgentCatalog.Resolve(new DockSettings { Agents = known }, AgentCatalog.FromStartMenu(StartMenuApps.Load())));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AgentCatalog.Resolve(new DockSettings { Agents = known }, _ => null);
        }
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
        ShowSettingsPage();
        ShowAddAgents();
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
        JumpList.Update(_presets, _cli.Concat(_desktop), path);
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
        if (Launcher.Launch(item, dir, _settings) is { } error)
            SetStatus(error);
        else
        {
            RememberLaunch(item, dir);
            HideFlyout();
        }
    }

    /// <summary>Claude Code only, so far. A folder already in the list moves to the front.</summary>
    public void RememberLaunch(string? command, string agentId, string folder)
    {
        if (RecentLaunches.Remember(_state, command, agentId, folder))
            _store.SaveState(_state);
    }

    private void RememberLaunch(AgentItem item, string folder) =>
        RememberLaunch(item.Definition.Command, item.Preset?.Agent ?? item.Definition.Id, folder);

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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPage.Visibility == Visibility.Visible)
            ShowMainPage();
        else
            ShowSettingsPage();
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e) => SettingsBack();

    /// <summary>Up one level: from an agent's page or Add agents to the general settings, from there to the list.</summary>
    private void SettingsBack()
    {
        if (_editingAgent is not null || AddAgentsPage.Visibility == Visibility.Visible)
            ShowSettingsGeneral(focusRowFor: _editingAgent);
        else
            ShowMainPage();
    }

    // ---------- Agents: the list in settings, each agent's page, adding ----------

    /// <summary>The agent whose page is showing (the object in <see cref="DockSettings.Agents"/>), if one is.</summary>
    private AgentDefinition? _editingAgent;
    /// <summary>Add agents was opened from the welcome, so adding goes straight back to the list.</summary>
    private bool _addAgentsFromSetup;

    /// <summary>
    /// Settings that only some agents have, shown under the agent's page, by command name. To give
    /// another agent some, add a panel to the settings page in the XAML (like ClaudeSettings), return
    /// it here, list it in <see cref="AgentSettingsPanels"/>, and fill it in <see cref="ShowSettingsPage"/>.
    /// </summary>
    private FrameworkElement? AgentSettingsPanelFor(string? command) => command switch
    {
        "claude" => ClaudeSettings,
        "grok" => GrokSettings,
        "cursor-agent" => CursorSettings,
        _ => null
    };

    private IEnumerable<FrameworkElement> AgentSettingsPanels => [ClaudeSettings, GrokSettings, CursorSettings];

    /// <summary>
    /// Cursor CLI or the Cursor desktop app (one account, one usage), by catalog id or CLI command.
    /// The CLI's command can also be plain "agent", so the catalog id matters.
    /// </summary>
    private static bool IsCursor(AgentDefinition definition) =>
        (definition.Catalog ?? definition.Id).ToLowerInvariant() is "cursor" or "cursor-cli"
        || (definition.Kind == AgentKind.Cli && CommandName(definition.Command) == "cursor-agent");

    /// <summary>The settings page's list of agents, in order. Dragging reorders it.</summary>
    private readonly ObservableCollection<AgentListRow> _agentRows = [];

    /// <summary>A row for each agent in the list, in order, leading to its page.</summary>
    private void BuildAgentSettingsRows()
    {
        AgentSettingsList.ItemsSource ??= _agentRows;
        _agentRows.Clear();
        foreach (var definition in _settings.Agents)
        {
            var item = _agents.FirstOrDefault(a => a.Preset is null && ReferenceEquals(a.Definition, definition));
            _agentRows.Add(new AgentListRow(definition, item?.Icon, item is not { IsAgentAvailable: false }));
        }
        // Agents.json that couldn't be read isn't really empty; changing the list would overwrite it.
        var editable = _settingsError is null;
        AgentSettingsList.CanReorderItems = AgentSettingsList.CanDragItems = editable;
        AddAgentsButtons.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        AgentListNote.Visibility = !editable || _settings.Agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AgentListNote.Text = !editable
            ? "agents.json couldn't be read, so your agents can't be changed here. Fix it with the pencil button, or delete it to start again."
            : "No agents yet.";
    }

    private void AgentSettingsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AgentListRow row)
            ShowAgentSettings(row.Definition);
    }

    /// <summary>An agent was dragged to a new place: save the list in that order.</summary>
    private void AgentSettingsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            return;
        var order = _agentRows.Select(r => r.Definition).ToList();
        if (order.SequenceEqual(_settings.Agents))
            return;
        _settings.Agents = order;
        SaveAgentList();
    }

    private void ShowAgentSettings(AgentDefinition definition)
    {
        _editingAgent = definition;
        var isCli = definition.Kind == AgentKind.Cli;
        var command = CommandName(definition.Command);

        _settingsToggleReady = false;
        AgentNameBox.Text = definition.Name;
        AgentArgumentsBox.Text = definition.Arguments ?? "";
        _settingsToggleReady = true;
        AgentArgumentsBox.Visibility = isCli ? Visibility.Visible : Visibility.Collapsed;
        AgentCommandText.Text = isCli
            ? $"Runs {definition.Command}{(string.IsNullOrWhiteSpace(definition.Arguments) ? "" : " " + definition.Arguments)} in a terminal."
            : "A desktop app, opened from the Start menu.";
        AgentEnvSection.Visibility = isCli ? Visibility.Visible : Visibility.Collapsed;
        AgentEnvNote.Text = command switch
        {
            "claude" => @"Set when this agent starts. For a second Claude account, duplicate it and set CLAUDE_CONFIG_DIR to its own folder, such as %USERPROFILE%\.claude-work. Write %NAME% to use a value from Windows, so keys stay out of agents.json.",
            "codex" => @"Set when this agent starts. For a second Codex account, duplicate it and set CODEX_HOME to its own folder, such as %USERPROFILE%\.codex-work. Write %NAME% to use a value from Windows, so keys stay out of agents.json.",
            _ => "Set when this agent starts. Write %NAME% to use a value from Windows, so keys stay out of agents.json."
        };
        FillEnvRows();
        EnvNameBox.Text = EnvValueBox.Text = "";
        RemoveAgentText.Text = $"Remove {definition.Name} from your agents? Presets for it stay until you remove them.";

        SettingsGeneral.Visibility = AddAgentsPage.Visibility = Visibility.Collapsed;
        AgentEditor.Visibility = Visibility.Visible;
        UpdateAgentPanel(definition);
        SettingsTitle.Text = definition.Name;
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
        PlaceWindow();
        DispatcherQueue.TryEnqueue(() => SettingsBackButton.Focus(FocusState.Programmatic));
    }

    private void FillEnvRows()
    {
        AgentEnvRows.Children.Clear();
        foreach (var (name, value) in (_editingAgent?.Env ?? []).OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " = " + value });
            ToolTipService.SetToolTip(label, $"{name} = {value}");
            var remove = new Button { Width = 32, Height = 28, Padding = new Thickness(0), Content = new FontIcon { Glyph = "", FontSize = 12 } };
            ToolTipService.SetToolTip(remove, "Remove");
            AutomationProperties.SetName(remove, $"Remove {name}");
            remove.Click += (_, _) =>
            {
                if (_editingAgent?.Env is not { } env || !env.Remove(name))
                    return;
                if (env.Count == 0)
                    _editingAgent.Env = null;
                SaveAgentList();
                FillEnvRows();
                UpdateAgentPanel(_editingAgent);
                PlaceWindow();
            };
            Grid.SetColumn(remove, 1);
            AgentEnvRows.Children.Add(new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                Children = { label, remove }
            });
        }
    }

    private void AddEnv_Click(object sender, RoutedEventArgs e) => AddEnv();

    private void EnvBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        AddEnv();
        e.Handled = true;
    }

    private void AddEnv()
    {
        if (_editingAgent is null)
            return;
        var name = EnvNameBox.Text.Trim();
        // Letters, digits and underscores, not starting with a digit: what both shells accept.
        if (name.Length == 0 || char.IsDigit(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            EnvNameBox.Focus(FocusState.Programmatic);
            EnvNameBox.SelectAll();
            return;
        }
        _editingAgent.Env ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _editingAgent.Env[name] = EnvValueBox.Text;
        SaveAgentList();
        FillEnvRows();
        UpdateAgentPanel(_editingAgent);
        EnvNameBox.Text = EnvValueBox.Text = "";
        EnvNameBox.Focus(FocusState.Programmatic);
        PlaceWindow();
    }

    private void EditorBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        if (ReferenceEquals(sender, AgentNameBox))
            CommitAgentName();
        else
            CommitAgentArguments();
        e.Handled = true;
    }

    private void AgentNameBox_LostFocus(object sender, RoutedEventArgs e) => CommitAgentName();

    private void AgentArgumentsBox_LostFocus(object sender, RoutedEventArgs e) => CommitAgentArguments();

    private void CommitAgentName()
    {
        if (!_settingsToggleReady || _editingAgent is null)
            return;
        var name = AgentNameBox.Text.Trim();
        if (name.Length == 0)
        {
            AgentNameBox.Text = _editingAgent.Name; // a name is required; put it back
            return;
        }
        if (name == _editingAgent.Name)
            return;
        _editingAgent.Name = name;
        SettingsTitle.Text = name;
        SaveAgentList();
    }

    private void CommitAgentArguments()
    {
        if (!_settingsToggleReady || _editingAgent is null)
            return;
        var arguments = AgentArgumentsBox.Text.Trim() is { Length: > 0 } text ? text : null;
        if (arguments == _editingAgent.Arguments)
            return;
        _editingAgent.Arguments = arguments;
        SaveAgentList();
        AgentCommandText.Text = $"Runs {_editingAgent.Command}{(arguments is null ? "" : " " + arguments)} in a terminal.";
    }

    /// <summary>
    /// A second copy, right after the original, with its own id and name, e.g. for another account.
    /// It keeps the original's catalog id, so it has the same icon and picture. Opens the copy.
    /// </summary>
    private void DuplicateAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_editingAgent is not { } original)
            return;
        var copy = original.Clone();
        copy.Id = KnownAgents.UniqueId(original.Catalog ?? original.Id, _settings.Agents);
        copy.Catalog ??= original.Catalog;
        copy.Name = $"{original.Name} (copy)";
        _settings.Agents.Insert(_settings.Agents.IndexOf(original) + 1, copy);
        if (!SaveAgentList())
            return;
        ShowAgentSettings(copy);
        DispatcherQueue.TryEnqueue(() =>
        {
            AgentNameBox.Focus(FocusState.Programmatic);
            AgentNameBox.SelectAll();
        });
    }

    private void RemoveAgent_Click(object sender, RoutedEventArgs e)
    {
        RemoveAgentFlyout.Hide();
        if (_editingAgent is not { } removed)
            return;
        _settings.Agents.Remove(removed);
        SaveAgentList();
        ShowSettingsGeneral();
    }

    private void AddAgents_Click(object sender, RoutedEventArgs e)
    {
        _addAgentsFromSetup = false;
        ShowAddAgents();
    }

    private void ScanAgain_Click(object sender, RoutedEventArgs e) => ShowAddAgents();

    /// <summary>The same page, scrolled to the form for a command of your own.</summary>
    private void AddCustomAgent_Click(object sender, RoutedEventArgs e)
    {
        _addAgentsFromSetup = false;
        ShowAddAgents();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            CustomNameBox.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
            CustomNameBox.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>The Add agents page: a scan for known agents not in the list, and a command of your own.</summary>
    private async void ShowAddAgents()
    {
        _editingAgent = null;
        SettingsGeneral.Visibility = AgentEditor.Visibility = Visibility.Collapsed;
        foreach (var panel in AgentSettingsPanels)
            panel.Visibility = Visibility.Collapsed;
        AddAgentsPage.Visibility = Visibility.Visible;
        SettingsTitle.Text = "Add agents";
        CustomNote.Visibility = Visibility.Collapsed;
        FoundAgentsList.Children.Clear();
        MissingAgentsList.Children.Clear();
        MissingAgentsExpander.Visibility = Visibility.Collapsed;
        MissingAgentsExpander.IsExpanded = false;
        AddFoundButton.Visibility = Visibility.Collapsed;
        FoundAgentsNote.Text = "Looking for agents on this PC…";
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
        PlaceWindow();
        DispatcherQueue.TryEnqueue(() => SettingsBackButton.Focus(FocusState.Programmatic));

        var agents = await FindNewAgentsAsync();
        if (AddAgentsPage.Visibility != Visibility.Visible || SettingsPage.Visibility != Visibility.Visible)
            return;
        var found = agents.Where(a => a.IsAgentAvailable).ToList();
        var missing = agents.Where(a => !a.IsAgentAvailable).ToList();
        foreach (var agent in found)
            FoundAgentsList.Children.Add(await AgentCheckBoxAsync(agent, isChecked: true));
        foreach (var agent in missing)
            MissingAgentsList.Children.Add(await AgentCheckBoxAsync(agent, isChecked: false));
        FoundAgentsNote.Text = found.Count > 0
            ? "These are installed and not in your list yet. Untick any you don't want."
            : missing.Count > 0
                ? "Nothing else Agent Dock knows about is installed. You can still add one, or your own command below."
                : "Nothing else Agent Dock knows about is installed. You can add your own command below.";
        MissingAgentsTitle.Text = $"Not found on this PC ({missing.Count})";
        MissingAgentsExpander.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MissingAgentsExpander.IsExpanded = found.Count == 0 && missing.Count > 0;
        AddFoundButton.Visibility = agents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceWindow();
    }

    /// <summary>A row on the Add agents page: icon, name, and its command (or "Desktop app").</summary>
    private async Task<CheckBox> AgentCheckBoxAsync(AgentItem agent, bool isChecked)
    {
        agent.Icon ??= _icons.Get(agent.IconSource)
                       ?? (agent.IconSource is { } source && ShellIcons.IsOnDisk(source) ? await _icons.LoadFromDiskAsync(source) : null);
        var icon = agent.Icon is { } image
            ? (UIElement)new Image { Source = image, Width = 24, Height = 24 }
            : new FontIcon { Glyph = agent.Glyph, FontSize = 18, Width = 24 };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        label.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = agent.Definition.Name });
        text.Children.Add(new TextBlock
        {
            Text = agent.IsCli ? agent.Definition.Command : "Desktop app",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        label.Children.Add(text);
        var box = new CheckBox { IsChecked = isChecked, Content = label, Tag = agent.Definition, Padding = new Thickness(8, 4, 0, 4) };
        AutomationProperties.SetName(box, agent.Definition.Name);
        return box;
    }

    private void AddFound_Click(object sender, RoutedEventArgs e)
    {
        var found = FoundAgentsList.Children.OfType<CheckBox>().ToList();
        var chosen = found.Concat(MissingAgentsList.Children.OfType<CheckBox>())
            .Where(b => b.IsChecked == true).Select(b => (AgentDefinition)b.Tag).ToList();
        // Unticked installed ones aren't offered again; they can still be added from here. Missing
        // ones aren't dismissed, so installing one later still brings the offer.
        DismissOffers(found.Where(b => b.IsChecked != true).Select(b => (AgentDefinition)b.Tag));
        AddAgents(chosen);
        if (_addAgentsFromSetup)
            ShowMainPage();
        else
            ShowSettingsGeneral();
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        var name = CustomNameBox.Text.Trim();
        var command = CustomCommandBox.Text.Trim();
        if (name.Length == 0 || command.Length == 0)
        {
            CustomNote.Text = "Give it a name and a command.";
            CustomNote.Visibility = Visibility.Visible;
            (name.Length == 0 ? CustomNameBox : CustomCommandBox).Focus(FocusState.Programmatic);
            return;
        }
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        _settings.Agents.Add(new AgentDefinition
        {
            Id = KnownAgents.UniqueId(slug.Length > 0 ? slug : "agent", _settings.Agents),
            Name = name,
            Command = command,
            Arguments = CustomArgumentsBox.Text.Trim() is { Length: > 0 } arguments ? arguments : null
        });
        if (!SaveAgentList())
            return;
        CustomNameBox.Text = CustomCommandBox.Text = CustomArgumentsBox.Text = "";
        if (_addAgentsFromSetup)
            ShowMainPage();
        else
            ShowSettingsGeneral();
    }

    /// <summary>The first level of settings. Coming back from an agent's page focuses its row.</summary>
    private void ShowSettingsGeneral(AgentDefinition? focusRowFor = null)
    {
        _editingAgent = null;
        foreach (var panel in AgentSettingsPanels)
            panel.Visibility = Visibility.Collapsed;
        AgentEditor.Visibility = AddAgentsPage.Visibility = Visibility.Collapsed;
        SettingsGeneral.Visibility = Visibility.Visible;
        SettingsTitle.Text = "Settings";
        if (!AppWindow.IsVisible || SettingsPage.Visibility != Visibility.Visible)
            return;
        BuildAgentSettingsRows();
        PlaceWindow();
        var row = _agentRows.FirstOrDefault(r => ReferenceEquals(r.Definition, focusRowFor));
        if (row is not null)
            DispatcherQueue.TryEnqueue(() => (AgentSettingsList.ContainerFromItem(row) as Control)?.Focus(FocusState.Programmatic));
        else
            SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>The Claude account whose usage setting the agent page shows.</summary>
    private UsageAccount? _settingsAccount;

    /// <summary>
    /// The agent-specific panel under an agent's page, for its account: Claude's usage source is per
    /// account, and doesn't apply at all to an agent using an API key or another endpoint. Run again
    /// when its environment variables change, since they decide the account.
    /// </summary>
    private void UpdateAgentPanel(AgentDefinition definition)
    {
        var account = definition.Kind == AgentKind.Cli ? UsageAccount.For(definition) : null;
        var panel = IsCursor(definition) ? CursorSettings
            : definition.Kind == AgentKind.Cli && account is not { UsesOtherProvider: true }
                ? AgentSettingsPanelFor(CommandName(definition.Command))
                : null;
        foreach (var other in AgentSettingsPanels)
            other.Visibility = other == panel ? Visibility.Visible : Visibility.Collapsed;

        _settingsAccount = account is { Agent: "claude", UsesOtherProvider: false } ? account : null;
        if (_settingsAccount is null)
            return;
        _settingsToggleReady = false;
        ClaudeUsageSourceButtons.SelectedIndex = SourceFor(_settingsAccount) == ClaudeUsageSource.Api ? 1 : 0;
        _settingsToggleReady = true;
        ClaudeUsageIntro.Text = _settingsAccount.IsDefault
            ? "How Agent Dock finds out how much of your plan's session and weekly limits you've used, for the drawer beside Claude Code."
            : $"How Agent Dock finds out how much of this account's session and weekly limits you've used (the sign-in in {_settingsAccount.ConfigDir}).";
    }

    private void ClaudeUsageSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var source = ClaudeUsageSourceButtons.SelectedIndex == 1 ? ClaudeUsageSource.Api : ClaudeUsageSource.StatusLine;
        if (!_settingsToggleReady || ClaudeUsageSourceButtons.SelectedIndex < 0 || _settingsAccount is not { } account
            || SourceFor(account) == source)
            return;
        SetSource(account, source);
        _store.SaveState(_state);
        ClearUsageCache(sourceChangedFor: account.Key);
        // A drawer open on Claude follows the new choice. (The settings page closes the drawer, so
        // this only matters if that ever changes.)
        if (_drawerOpen && !_drawerClosing && _drawerItem is { } item && CommandName(item.Definition.Command) == "claude")
            PrepareDrawer(item);
    }

    /// <summary>
    /// The list and the settings page share this window. Settings is a short page, so the flyout
    /// resizes to it; hiding always returns to the list, which is what the next click is for.
    /// </summary>
    private void ShowSettingsPage()
    {
        CloseDrawer(animate: false);
        FadeBackdrop(null);
        _settingsToggleReady = false;
        HoverBackgroundsSwitch.IsOn = _state.ShowHoverBackgrounds;
        RecentFoldersSwitch.IsOn = _state.RememberRecentLaunches;
        HideUnavailableSwitch.IsOn = _settings.HideUnavailable;
        // Lives in agents.json, which isn't written while it can't be read.
        HideUnavailableSwitch.IsEnabled = _settingsError is null;
        HideUnavailableNote.Text = _settingsError is null
            ? "Hide agents in your list that aren't installed any more, instead of showing them dimmed."
            : "Fix agents.json to change this here.";
        ClearRecentsButton.IsEnabled = RecentLaunches.Any(_state);
        _settingsToggleReady = true;

        MainPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        ShowSettingsGeneral();
        BuildAgentSettingsRows();
        PlaceWindow();
        DispatcherQueue.TryEnqueue(() => HoverBackgroundsSwitch.Focus(FocusState.Programmatic));
    }

    private void ShowMainPage()
    {
        if (MainPage.Visibility == Visibility.Visible)
            return;
        // Next time, settings opens at the first level.
        ShowSettingsGeneral();
        SettingsPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        if (!AppWindow.IsVisible)
            return;
        PlaceWindow();
        DispatcherQueue.TryEnqueue(FocusFirstAgent);
    }

    private void HoverBackgroundsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _state.ShowHoverBackgrounds == HoverBackgroundsSwitch.IsOn)
            return;
        _state.ShowHoverBackgrounds = HoverBackgroundsSwitch.IsOn;
        _store.SaveState(_state);
        UpdateBannerHeart();
        if (!_state.ShowHoverBackgrounds)
            FadeBackdrop(null);
    }

    private void HideUnavailableSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _settings.HideUnavailable == HideUnavailableSwitch.IsOn)
            return;
        _settings.HideUnavailable = HideUnavailableSwitch.IsOn;
        if (SaveAgentList())
            return;
        _settings.HideUnavailable = !HideUnavailableSwitch.IsOn;
        _settingsToggleReady = false;
        HideUnavailableSwitch.IsOn = _settings.HideUnavailable;
        _settingsToggleReady = true;
    }

    private void RecentFoldersSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _state.RememberRecentLaunches == RecentFoldersSwitch.IsOn)
            return;
        _state.RememberRecentLaunches = RecentFoldersSwitch.IsOn;
        _store.SaveState(_state);
    }

    private void ClearRecents_Click(object sender, RoutedEventArgs e)
    {
        ClearRecentsFlyout.Hide();
        RecentLaunches.Clear(_state);
        _store.SaveState(_state);
        ClearRecentsButton.IsEnabled = false;
        // The button just went away under the focus; keep it on the page.
        RecentFoldersSwitch.Focus(FocusState.Programmatic);
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (SettingsPage.Visibility == Visibility.Visible)
                SettingsBack();
            else
                HideFlyout();
            e.Handled = true;
            return;
        }

        if (SettingsPage.Visibility == Visibility.Visible || FolderBox.IsDropDownOpen)
            return;

        var digit = e.Key switch
        {
            >= VirtualKey.Number1 and <= VirtualKey.Number9 => e.Key - VirtualKey.Number1 + 1,
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 => e.Key - VirtualKey.NumberPad1 + 1,
            _ => 0
        };
        if (digit == 0)
            return;

        var target = AllItems.FirstOrDefault(a => a.Shortcut == digit.ToString());
        if (target is not null)
        {
            Launch(target);
            e.Handled = true;
        }
    }

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
            // Desktop apps don't open in a folder, so they can't be presets.
            if (item.IsCli)
            {
                var folder = CurrentFolder;
                menu.Items.Add(MenuItem($"Save as preset for {new FolderItem(folder).Name}", "\uE718", () => SavePreset(item, folder)));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(MenuItem("Settings", "\uE713", () =>
            {
                ShowSettingsPage();
                ShowAgentSettings(item.Definition);
            }));
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
        JumpList.Update(_presets, _cli.Concat(_desktop), CurrentFolder);
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
        RecentSection.Visibility = Visibility.Collapsed;
        if (!item.IsAgentAvailable)
            return false;
        // The CLI and the desktop app share one account, so both show its usage.
        if (IsCursor(item.Definition))
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartLiveCheck(CursorCheckKey, CursorUsage.MinInterval, CursorUsage.FetchAsync, CursorUsage.Unreachable,
                shown => IsCursor(shown.Definition), ShowCursorCheck);
            ShowCursorCheck();
            ShowRecents(item);
            return true;
        }
        if (!item.IsCli)
            return false;

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

        switch (CommandName(item.Definition.Command))
        {
            case "claude":
                return PrepareClaudeDrawer(item, account!);
            case "codex":
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                // Live from Codex, as this account, at most every few minutes; what's known shows meanwhile.
                StartLiveCheck(account!.Key, CodexUsage.MinInterval, () => CodexUsage.FetchAsync(account), CodexUsage.Unreachable,
                    shown => OnAccount(shown, account), () => ShowCodexCheck(account));
                ShowCodexCheck(account!);
                ShowRecents(item);
                return true;
            case "grok":
                // From grok.com, with Grok Build's own sign-in.
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                StartLiveCheck(GrokCheckKey, GrokUsage.MinInterval, GrokUsage.FetchAsync, GrokUsage.Unreachable,
                    shown => CommandName(shown.Definition.Command) == "grok", ShowGrokCheck);
                ShowGrokCheck();
                ShowRecents(item);
                return true;
            case "copilot":
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                if (!_copilotFetching && (_copilotUsage is null || DateTimeOffset.UtcNow - _copilotUsage.FetchedAt > CopilotUsageMaxAge))
                    _ = FetchCopilotUsageAsync();
                ShowCopilotUsage(item);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Where this Claude account's usage comes from (settings → the agent's page).</summary>
    private ClaudeUsageSource SourceFor(UsageAccount account)
    {
        if (account.IsDefault)
            return _state.ClaudeUsageSource;
        foreach (var (dir, source) in _state.ClaudeUsageSources)
            if (string.Equals(dir, account.ConfigDir, StringComparison.OrdinalIgnoreCase))
                return source;
        return ClaudeUsageSource.StatusLine;
    }

    private void SetSource(UsageAccount account, ClaudeUsageSource source)
    {
        if (account.IsDefault)
        {
            _state.ClaudeUsageSource = source;
            return;
        }
        foreach (var dir in _state.ClaudeUsageSources.Keys.Where(k => string.Equals(k, account.ConfigDir, StringComparison.OrdinalIgnoreCase)).ToList())
            _state.ClaudeUsageSources.Remove(dir);
        _state.ClaudeUsageSources[account.ConfigDir] = source;
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
            StartLiveCheck(account.Key, ClaudeUsageApi.MinInterval, () => ClaudeUsageApi.FetchAsync(account), ClaudeUsageApi.Unreachable,
                shown => OnAccount(shown, account) && SourceFor(account) == ClaudeUsageSource.Api, () => ShowClaudeCheck(account));
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

        if (shown == _generation && _drawerOpen && !_drawerClosing && _drawerItem is { } item && showing(item))
            show();
    }

    /// <summary>Grok and Cursor have one account each, their CLI's sign-in.</summary>
    private const string GrokCheckKey = "grok";
    private const string CursorCheckKey = "cursor";

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
        // The drawer shows what's left; with nothing left and no usage to show, it closes.
        if (!ShowRecents(item) && UsagePanel.Visibility != Visibility.Visible && !UsageOffer.IsOpen)
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
        if (_drawerOpen && !_drawerClosing && _drawerItem is { } item && CommandName(item.Definition.Command) == "copilot")
            ShowCopilotUsage(item);
    }

    /// <summary>
    /// Copilot's single monthly window: premium requests used, as a bar when the plan's allowance is
    /// known ("monthlyAllowance"), otherwise just the count. It resets at 00:00 UTC on the 1st.
    /// </summary>
    private void ShowCopilotUsage(AgentItem item)
    {
        UsageTitle.Text = "Copilot usage";
        UsagePanel.Visibility = Visibility.Visible;
        WeekGroup.Visibility = Visibility.Collapsed;
        SessionLabel.Visibility = SessionText.Visibility = Visibility.Visible; // Grok hides them
        ShowUsageExtras(null, DateTimeOffset.UtcNow);
        SessionLabel.Text = "Premium requests";

        var usage = _copilotUsage;
        var hasData = usage?.Used is not null;
        UsageRows.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        UsageNote.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = usage?.Problem ?? "Checking GitHub…";
        UsageUpdatedText.Text = "";
        if (!hasData)
            return;

        var now = DateTimeOffset.UtcNow;
        var reset = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var used = usage!.Used!.Value;
        if (item.Definition.MonthlyAllowance is > 0 and var allowance)
        {
            SessionBar.Visibility = Visibility.Visible;
            ShowWindow(SessionBar, SessionText,
                new UsageWindow { UsedPercentage = used * 100 / allowance, ResetsAt = reset.ToUnixTimeSeconds() }, now);
            SessionText.Text = $"{used:0} of {allowance} · resets {ResetTime(reset)}";
        }
        else
        {
            SessionBar.Visibility = Visibility.Collapsed;
            SessionText.Text = $"{used:0} used · resets {ResetTime(reset)}";
        }
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

    private static string UpdatedAgo(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        return age.TotalMinutes < 1 ? "Updated just now"
            : age.TotalHours < 1 ? $"Updated {(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"Updated {(int)age.TotalHours} hr ago"
            : $"Updated {when.ToLocalTime():MMM d}";
    }

    /// <summary>"claude" for "claude", "claude.exe" or a quoted full path to it.</summary>
    private static string? CommandName(string? command) =>
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
        if (_drawerItem is null || !ShowRecents(_drawerItem))
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
        var drawer = item is not null && PrepareDrawer(item);
        // Switching an open drawer to another agent: fade its new content in rather than swapping it.
        if (drawer && previous is not null
            && CommandName(previous.Definition.Command) != CommandName(item!.Definition.Command))
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
        // Closed: subject over the middle of the list (past the banner), as far as the picture still
        // covers the list. Open on the right: right edge on the drawer's far edge. Open on the left:
        // right edge on the list's right edge, so it reaches back over the drawer.
        var closed = Math.Clamp(BannerWidthDip + (ListWidthDip - BannerWidthDip) / 2 - width * BackdropSubjectX,
            ListWidthDip - width, 0);
        var open = _drawerSide > 0 ? ListWidthDip + DrawerWidthDip - width : ListWidthDip - width;
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
    /// Set up) without it closing.
    /// </summary>
    private void ScheduleDrawerClose()
    {
        if (!_drawerOpen || _drawerClosing || _popupsOpen > 0)
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
                CloseDrawer(animate: true);
            };
        }
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
        // The height the user dragged to, for the list and settings alike (both scroll if their
        // content is taller). Without one, the list fits its content and settings matches the list.
        var onList = MainPage.Visibility == Visibility.Visible;
        var heightDip = _state.FlyoutHeight is { } chosen
            ? Math.Clamp(chosen, Math.Min(MinHeightDip, workHeightDip), workHeightDip)
            : !onList && _listHeightDip is { } listHeight
                ? Math.Min(listHeight, workHeightDip)
                : Math.Min(fitDip, Math.Min(MaxHeightDip, workHeightDip));
        if (onList)
            _listHeightDip = heightDip;
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
    /// <summary>The list's height last time it was placed, which settings matches when no height was dragged.</summary>
    private double? _listHeightDip;
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
