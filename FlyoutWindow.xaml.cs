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

        if (!_sinceShellRefresh.IsRunning || _sinceShellRefresh.Elapsed > ShellRefreshInterval
            || AllItems.Any(a => a.Icon is null && a.IconSource is not null))
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
        // A recent folder's menu took the pointer; the drawer (and its agent's colour) stays.
        if (_drawerMenuOpen)
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
        // Fast pass: PATH lookups plus whatever desktop apps were found last time.
        ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
        ClearUsageCache();
    }

    /// <summary>Slow pass: read the Start menu and extract missing icons on a background STA thread.</summary>
    private async Task RefreshFromShellAsync(int generation)
    {
        _sinceShellRefresh.Restart();
        var settings = _settings;
        var inMemory = AllItems.Select(a => a.IconSource).Where(s => _icons.Has(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var (agents, extracted) = await StartMenuApps.RunOnStaThread(() =>
            {
                var resolved = AgentCatalog.Resolve(settings, AgentCatalog.FromStartMenu(StartMenuApps.Load()));
                var pixels = resolved
                    .Select(a => a.IconSource)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(s => !inMemory.Contains(s) && !ShellIcons.IsOnDisk(s))
                    .Select(s => (Source: s, Pixels: ShellIcons.Extract(s)))
                    .Where(x => x.Pixels is not null)
                    .ToList();
                return (resolved, pixels);
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
        EmptyText.Visibility = _presets.Count + _cli.Count + _desktop.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Up one level: from an agent's page to the general settings, from there to the list.</summary>
    private void SettingsBack()
    {
        if (_settingsAgent is not null)
            ShowSettingsGeneral(focusRowFor: _settingsAgent);
        else
            ShowMainPage();
    }

    // ---------- Per-agent settings ----------

    /// <summary>The agent whose own settings page is showing, if one is.</summary>
    private AgentItem? _settingsAgent;

    /// <summary>
    /// An agent's own settings page, by command name. To give another agent settings, add a panel to
    /// the settings page in the XAML (like ClaudeSettings), return it here, and fill it in
    /// <see cref="ShowSettingsPage"/>.
    /// </summary>
    private FrameworkElement? AgentSettingsPanelFor(string? command) => command switch
    {
        "claude" => ClaudeSettings,
        _ => null
    };

    private IEnumerable<FrameworkElement> AgentSettingsPanels => [ClaudeSettings];

    /// <summary>A row for each installed agent with a settings page of its own (one per page).</summary>
    private void BuildAgentSettingsRows()
    {
        AgentSettingsRows.Children.Clear();
        var seen = new HashSet<FrameworkElement>();
        foreach (var agent in _agents.Where(a => a.Preset is null && a.IsAvailable))
        {
            if (AgentSettingsPanelFor(CommandName(agent.Definition.Command)) is not { } panel || !seen.Add(panel))
                continue;

            var icon = agent.Icon is { } source
                ? (UIElement)new Image { Source = source, Width = 24, Height = 24 }
                : new FontIcon { Glyph = agent.Glyph, FontSize = 18 };
            var chevron = new FontIcon { Glyph = "", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            Grid.SetColumn(chevron, 2);
            var name = new TextBlock { Text = agent.Definition.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            var row = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 6),
                Tag = agent,
                Content = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Children = { icon, name, chevron }
                }
            };
            AutomationProperties.SetName(row, $"{agent.Definition.Name} settings");
            row.Click += (_, _) => ShowAgentSettings(agent);
            AgentSettingsRows.Children.Add(row);
        }
        AgentSettingsHeader.Visibility = AgentSettingsRows.Visibility =
            AgentSettingsRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAgentSettings(AgentItem agent)
    {
        if (AgentSettingsPanelFor(CommandName(agent.Definition.Command)) is not { } panel)
            return;
        _settingsAgent = agent;
        SettingsGeneral.Visibility = Visibility.Collapsed;
        foreach (var other in AgentSettingsPanels)
            other.Visibility = other == panel ? Visibility.Visible : Visibility.Collapsed;
        SettingsTitle.Text = agent.Definition.Name;
        PlaceWindow();
        DispatcherQueue.TryEnqueue(() => SettingsBackButton.Focus(FocusState.Programmatic));
    }

    /// <summary>The first level of settings. Coming back from an agent's page focuses its row.</summary>
    private void ShowSettingsGeneral(AgentItem? focusRowFor = null)
    {
        _settingsAgent = null;
        foreach (var panel in AgentSettingsPanels)
            panel.Visibility = Visibility.Collapsed;
        SettingsGeneral.Visibility = Visibility.Visible;
        SettingsTitle.Text = "Settings";
        if (!AppWindow.IsVisible || SettingsPage.Visibility != Visibility.Visible)
            return;
        PlaceWindow();
        var row = AgentSettingsRows.Children.OfType<Button>().FirstOrDefault(b => b.Tag == focusRowFor);
        DispatcherQueue.TryEnqueue(() => ((Control?)row ?? HoverBackgroundsSwitch).Focus(FocusState.Programmatic));
    }

    private void ClaudeUsageSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var source = ClaudeUsageSourceButtons.SelectedIndex == 1 ? ClaudeUsageSource.Api : ClaudeUsageSource.StatusLine;
        if (!_settingsToggleReady || ClaudeUsageSourceButtons.SelectedIndex < 0 || _state.ClaudeUsageSource == source)
            return;
        _state.ClaudeUsageSource = source;
        _store.SaveState(_state);
        ClearUsageCache(sourceChanged: true);
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
        // Lives in agents.json, which is only changed when it reads cleanly (TrySetHideUnavailable).
        HideUnavailableSwitch.IsEnabled = _settingsError is null;
        HideUnavailableNote.Text = _settingsError is null
            ? "Hide agents that aren't installed, instead of showing them dimmed."
            : "Fix agents.json to change this here.";
        ClearRecentsButton.IsEnabled = RecentLaunches.Any(_state);
        // Claude Code's page.
        ClaudeUsageSourceButtons.SelectedIndex = _state.ClaudeUsageSource == ClaudeUsageSource.Api ? 1 : 0;
        _settingsToggleReady = true;

        BuildAgentSettingsRows();
        ShowSettingsGeneral();
        MainPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        HeightGrip.Visibility = Visibility.Collapsed;
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
        HeightGrip.Visibility = Visibility.Visible;
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
        if (!_store.TrySetHideUnavailable(HideUnavailableSwitch.IsOn))
        {
            _settingsToggleReady = false;
            HideUnavailableSwitch.IsOn = _settings.HideUnavailable;
            _settingsToggleReady = true;
            HideUnavailableNote.Text = "agents.json couldn't be changed. Edit hideUnavailable there instead.";
            return;
        }
        _settings.HideUnavailable = HideUnavailableSwitch.IsOn;
        // The list behind the settings page; it's sized again on the way back.
        ShowAgents(_agents.ToList());
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
        else if (item.IsCli)
        {
            var folder = CurrentFolder;
            menu.Items.Add(MenuItem($"Save as preset for {new FolderItem(folder).Name}", "", () => SavePreset(item, folder)));
        }
        else
        {
            return; // Desktop apps don't open in a folder, so there's nothing to offer.
        }

        if (e.TryGetPosition(anchor, out var point))
            menu.ShowAt(anchor, point);
        else
            menu.ShowAt(anchor);
        e.Handled = true;
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

    // Loaded on the first hover after each show (ReloadData clears them), so opening stays fast.
    private ClaudeStatusLine.Setup? _claudeSetup;
    private PlanUsage? _claudeUsage;
    private PlanUsage? _codexUsage;
    private bool _codexUsageLoaded;
    // Copilot's comes over the network, so it's fetched in the background and kept across shows.
    private static readonly TimeSpan CopilotUsageMaxAge = TimeSpan.FromMinutes(5);
    private CopilotUsageResult? _copilotUsage;
    private bool _copilotFetching;
    // "Ask Claude directly": also over the network, so also kept across shows.
    private ClaudeUsageFetch? _claudeCheck;
    private DateTimeOffset _claudeCheckedAt;
    private bool _claudeChecking;
    /// <summary>Changes when the source setting does, so a check started before that is ignored.</summary>
    private int _claudeCheckEpoch;
    /// <summary>The row whose content the drawer is showing.</summary>
    private AgentItem? _drawerItem;

    /// <summary>
    /// Drops what was loaded from files, so the next hover reads them again. Network results survive
    /// a show; <paramref name="sourceChanged"/> (the Claude usage setting changed) drops those too.
    /// </summary>
    private void ClearUsageCache(bool sourceChanged = false)
    {
        _claudeSetup = null;
        _claudeUsage = _codexUsage = null;
        _codexUsageLoaded = false;
        if (!sourceChanged)
            return;
        _claudeCheck = null;
        _claudeChecking = false;
        _claudeCheckEpoch++;
    }

    /// <summary>
    /// Fills the drawer for <paramref name="item"/>. Returns false, leaving the drawer as it is, if
    /// that agent has nothing to show: so far Claude Code, Codex and GitHub Copilot (and their presets).
    /// </summary>
    private bool PrepareDrawer(AgentItem item)
    {
        RecentSection.Visibility = Visibility.Collapsed;
        if (!item.IsCli || !item.IsAgentAvailable)
            return false;

        switch (CommandName(item.Definition.Command))
        {
            case "claude":
                return PrepareClaudeDrawer(item);
            case "codex":
                if (!_codexUsageLoaded)
                {
                    _codexUsage = CodexUsage.Load();
                    _codexUsageLoaded = true;
                }
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                // Codex writes its usage to its session logs only while a session runs.
                ShowUsage("Codex usage", _codexUsage, "Your limits appear here after your next Codex reply.");
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

    /// <summary>
    /// Claude Code's usage, from the source chosen on the settings page. Status line: the saved
    /// usage, or the offer to set Agent Dock up as Claude Code's status line. Ask Claude directly:
    /// see <see cref="PrepareClaudeCheckDrawer"/>.
    /// </summary>
    private bool PrepareClaudeDrawer(AgentItem item)
    {
        if (_state.ClaudeUsageSource == ClaudeUsageSource.Api)
            return PrepareClaudeCheckDrawer(item);

        _claudeSetup ??= ClaudeStatusLine.CheckSetup(Environment.ProcessPath!);
        var setup = _claudeSetup.Value;
        switch (setup.State)
        {
            case ClaudeStatusLine.SetupState.Installed:
                _claudeUsage ??= ClaudeStatusLine.Load();
                UsageOffer.IsOpen = false;
                // Claude Code only refreshes its status line while a session is running.
                ShowUsage("Claude usage", _claudeUsage, "Your limits appear here after your next Claude Code reply.");
                break;
            case ClaudeStatusLine.SetupState.OtherCopy:
                // Not dismissable: usage here has silently stopped updating.
                OfferUsage(InfoBarSeverity.Warning, "Update",
                    "Claude Code's status line runs a different copy of Agent Dock, so usage here won't update.", closable: false);
                break;
            case ClaudeStatusLine.SetupState.NotInstalled when !_state.ClaudeUsageOfferDismissed:
                OfferUsage(InfoBarSeverity.Informational, "Set up", setup.CurrentCommand is null
                    ? "See your plan's usage limits here. Agent Dock becomes Claude Code's status line to read them."
                    : $"See your plan's usage limits here. Agent Dock replaces Claude Code's status line ({DescribeCommand(setup.CurrentCommand)}). Its settings are backed up first.",
                    closable: true);
                break;
            default:
                UsagePanel.Visibility = Visibility.Collapsed;
                UsageOffer.IsOpen = false;
                if (!ShowClaudeRecents(item))
                    return false;
                _drawerItem = item;
                return true;
        }
        ShowClaudeRecents(item);
        _drawerItem = item;
        return true;
    }

    /// <summary>
    /// "Ask Claude directly": no status line offer or other-copy warning. Shows what's known straight
    /// away (the last check, else the saved file) and checks again in the background if that's
    /// missing or over 3 minutes old. Only ever from here: nothing checks while the flyout is hidden.
    /// </summary>
    private bool PrepareClaudeCheckDrawer(AgentItem item)
    {
        UsageOffer.IsOpen = false;
        if (!_claudeChecking && (_claudeCheck is null || DateTimeOffset.UtcNow - _claudeCheckedAt > ClaudeUsageApi.MinInterval))
            _ = CheckClaudeUsageAsync();
        ShowClaudeCheck();
        ShowClaudeRecents(item);
        _drawerItem = item;
        return true;
    }

    private async Task CheckClaudeUsageAsync()
    {
        var epoch = _claudeCheckEpoch;
        var shown = _generation;
        _claudeChecking = true;
        ClaudeUsageFetch result;
        try
        {
            result = await ClaudeUsageApi.FetchAsync(); // resumes on the UI thread; never throws
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new ClaudeUsageFetch(null, ClaudeUsageApi.Unreachable);
        }
        // The setting changed while this ran: it isn't wanted any more.
        if (epoch != _claudeCheckEpoch)
            return;
        _claudeChecking = false;
        _claudeCheck = result;
        _claudeCheckedAt = DateTimeOffset.UtcNow;
        if (result.Usage is not null)
            _claudeUsage = null; // the file was just replaced; read it again for the status line mode

        // Fill in the drawer only if it's still showing Claude, in the same show.
        if (shown == _generation && _drawerOpen && !_drawerClosing && _drawerItem is { } item
            && CommandName(item.Definition.Command) == "claude" && _state.ClaudeUsageSource == ClaudeUsageSource.Api)
            ShowClaudeCheck();
    }

    /// <summary>The newest numbers available, with a note when the check failed or is still running.</summary>
    private void ShowClaudeCheck()
    {
        var check = _claudeCheck;
        var usage = check?.Usage ?? (_claudeUsage ??= ClaudeStatusLine.Load());
        var hasData = usage is { FiveHour: not null } or { SevenDay: not null };

        string? note;
        if (_claudeChecking && check?.Problem is null)
            note = hasData ? null : ClaudeUsageApi.Checking; // saved numbers stay as they are
        else if (check?.Problem is { } problem)
            note = hasData ? $"{problem} {ClaudeUsageApi.ShowingSaved}" : problem;
        else
            note = null;

        ShowUsage("Claude usage", usage, hasData ? "" : note ?? ClaudeUsageApi.NoWindows, hasData ? note : null);
    }

    /// <summary>The folders currently offered for the hovered agent, newest first, at most five.</summary>
    private List<string> _shownRecents = [];
    /// <summary>Drawer height the recents were last fitted to, so a width change doesn't rebuild them.</summary>
    private double _recentsFitHeight;

    /// <summary>
    /// The last few folders Claude Code was opened in, under the usage. Never more than five, and
    /// never more than fit in the drawer without scrolling. Clicking one launches Claude Code there.
    /// </summary>
    private bool ShowClaudeRecents(AgentItem item)
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
            AutomationProperties.SetName(button, $"Open Claude Code in {folder}");
            ToolTipService.SetToolTip(button, folder);
            button.Click += RecentFolder_Click;

            // Right-click, Shift+F10 or the menu key.
            var remove = new MenuFlyoutItem { Text = "Remove from recents", Icon = new FontIcon { Glyph = "" }, Tag = folder };
            remove.Click += RemoveRecent_Click;
            var menu = new MenuFlyout { Items = { remove } };
            // The menu is a popup, so the pointer "leaves" the drawer while it's open. Keep it open.
            menu.Opened += (_, _) =>
            {
                _drawerMenuOpen = true;
                _drawerCloseTimer?.Stop();
            };
            menu.Closed += (_, _) => _drawerMenuOpen = false;
            button.ContextFlyout = menu;
            RecentRows.Children.Add(button);
        }
    }

    /// <summary>A recent folder's menu is open; the drawer mustn't close under it.</summary>
    private bool _drawerMenuOpen;

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string folder } || _drawerItem is not { } item)
            return;
        var id = item.Preset?.Agent ?? item.Definition.Id;
        if (!RecentLaunches.Forget(_state, id, folder))
            return;
        _store.SaveState(_state);
        // The drawer shows what's left; with nothing left and no usage to show, it closes.
        if (!ShowClaudeRecents(item) && UsagePanel.Visibility != Visibility.Visible && !UsageOffer.IsOpen)
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
    private void ShowUsage(string title, PlanUsage? usage, string emptyNote, string? note = null)
    {
        UsageTitle.Text = title;
        UsagePanel.Visibility = Visibility.Visible;
        SessionLabel.Text = "Session";
        SessionBar.Visibility = WeekGroup.Visibility = Visibility.Visible;
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
        // Both agents only record usage while a session runs, so say how old this is.
        UsageUpdatedText.Text = UpdatedAgo(DateTimeOffset.FromUnixTimeSeconds(usage.UpdatedAt), now);
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

    private void UsageOfferButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClaudeStatusLine.Install(Environment.ProcessPath!) is { } error)
        {
            SetStatus(error);
            return;
        }
        ClearUsageCache();
        if (_drawerItem is not null)
            PrepareDrawer(_drawerItem);
        StatusText.Text = "Claude Code now uses Agent Dock as its status line.";
        PlaceWindow(); // the footer message may wrap
    }

    private void UsageOffer_CloseButtonClick(InfoBar sender, object args)
    {
        _state.ClaudeUsageOfferDismissed = true;
        _store.SaveState(_state);
        UsageOffer.IsOpen = false;
        if (_drawerItem is null || !ShowClaudeRecents(_drawerItem))
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
        if (_state.ShowHoverBackgrounds && !_drawerOpen)
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
        if (!_drawerOpen || _drawerClosing || _drawerMenuOpen)
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
        // The list keeps the height the user dragged it to (it scrolls if the content is taller).
        // The settings page always fits its content.
        var heightDip = MainPage.Visibility == Visibility.Visible && _state.FlyoutHeight is { } chosen
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
