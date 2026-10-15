using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AgentDock.Interop;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
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
    private const double WidthDip = 380;
    private const double MaxHeightDip = 640;

    /// <summary>A click on the taskbar icon hides the open flyout on mouse-down and relaunches on mouse-up; ignore that relaunch.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ShellRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _store;
    private readonly ShellIcons _icons = new();
    private readonly ObservableCollection<AgentItem> _cli = [];
    private readonly ObservableCollection<AgentItem> _desktop = [];
    private readonly ObservableCollection<FolderItem> _folders = [];
    private readonly Stopwatch _sinceHide = new();
    private readonly Stopwatch _sinceShellRefresh = new();
    private readonly TaskCompletionSource _rootLoaded = new();
    private readonly nint _hwnd;

    private DockSettings _settings = new();
    private DockState _state = new();
    private POINT _anchor;
    private int _generation;
    private bool _prepared;
    private bool _pendingShow;
    private string? _pendingStatus;
    private bool _keepOpen;
    private bool _hiddenByDeactivation;
    private bool _loadingFolders;

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
    private IEnumerable<AgentItem> AllItems => _cli.Concat(_desktop);

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
        var first = (CliList.Items.Count > 0 ? CliList : DesktopList).ContainerFromIndex(0) as ListViewItem;
        first?.Focus(FocusState.Programmatic);
    }

    // ---------- Data ----------

    private void ReloadData()
    {
        (_settings, var settingsError) = _store.LoadSettings();
        _state = _store.LoadState();
        StatusText.Text = settingsError ?? "";
        Autostart.Apply(_settings.StartWithWindows);

        LoadFolders();
        // Fast pass: PATH lookups plus whatever desktop apps were found last time.
        ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
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

            JumpList.Update(AllItems, CurrentFolder);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine(ex);
        }
    }

    private bool IsShown(AgentItem item) => item.IsAvailable || !_settings.HideUnavailable;

    private void ShowAgents(IEnumerable<AgentItem> agents)
    {
        _cli.Clear();
        _desktop.Clear();
        foreach (var agent in agents.Where(IsShown))
        {
            agent.Icon ??= _icons.Get(agent.IconSource);
            (agent.IsCli ? _cli : _desktop).Add(agent);
        }

        var shortcut = 1;
        foreach (var agent in AllItems)
            agent.Shortcut = agent.IsAvailable && shortcut <= 9 ? (shortcut++).ToString() : null;

        CliHeader.Visibility = _cli.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CliList.Visibility = CliHeader.Visibility;
        DesktopHeader.Visibility = _desktop.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DesktopList.Visibility = DesktopHeader.Visibility;
        EmptyText.Visibility = _cli.Count + _desktop.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        JumpList.Update(AllItems, path);
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

    private void Launch(AgentItem item)
    {
        if (Launcher.Launch(item, CurrentFolder, _settings) is { } error)
            SetStatus(error);
        else
            HideFlyout();
    }

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

    /// <summary>Lets Up/Down arrows cross between the two lists.</summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListView list || FocusManager.GetFocusedElement(Root.XamlRoot) is not ListViewItem focused)
            return;

        var index = list.IndexFromContainer(focused);
        var target = e.Key switch
        {
            VirtualKey.Down when list == CliList && index == list.Items.Count - 1 => DesktopList,
            VirtualKey.Up when list == DesktopList && index == 0 => CliList,
            _ => null
        };
        if (target is null || target.Items.Count == 0)
            return;

        var targetIndex = target == DesktopList ? 0 : target.Items.Count - 1;
        if (target.ContainerFromIndex(targetIndex) is ListViewItem container)
        {
            container.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
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

        Root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var maxHeightDip = Math.Min(MaxHeightDip, (work.Height - 2 * gap) / scale);
        var heightDip = Root.DesiredSize.Height > 1
            ? Math.Min(Root.DesiredSize.Height, maxHeightDip)
            : Math.Min(AppWindow.ClientSize.Height / scale, maxHeightDip);

        // Window size includes the thin border around the client area.
        var borderW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var borderH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var w = (int)Math.Ceiling(WidthDip * scale) + borderW;
        var h = (int)Math.Ceiling(heightDip * scale) + borderH;

        int x, y;
        if (work.Contains(_anchor))
        {
            x = work.Left + (work.Width - w) / 2;
            y = work.Top + (work.Height - h) / 2;
        }
        else if (_anchor.Y >= work.Bottom) { x = _anchor.X - w / 2; y = work.Bottom - h - gap; }
        else if (_anchor.Y < work.Top)     { x = _anchor.X - w / 2; y = work.Top + gap; }
        else if (_anchor.X < work.Left)    { x = work.Left + gap;   y = _anchor.Y - h / 2; }
        else                               { x = work.Right - w - gap; y = _anchor.Y - h / 2; }

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - w - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - h - gap));

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
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
