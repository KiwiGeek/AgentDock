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
    private const double ListWidthDip = 380;
    /// <summary>How far the window grows when the drawer is open.</summary>
    private const double DrawerWidthDip = 240;
    private const double HoverBackdropOpacity = 0.3;
    private const double MaxHeightDip = 640;

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
        // Don't reopen with the last hover's image, the drawer, or the settings page showing.
        FadeBackdrop(null, spansDrawer: false);
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

    // ---------- Data ----------

    private void ReloadData()
    {
        (_settings, var settingsError) = _store.LoadSettings();
        _state = _store.LoadState();
        (_presetList, _presetsError) = _store.LoadPresets();
        StatusText.Text = settingsError ?? _presetsError ?? "";
        Autostart.Apply(_settings.StartWithWindows);

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

    private void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowMainPage();

    /// <summary>
    /// The list and the settings page share this window. Settings is a short page, so the flyout
    /// resizes to it; hiding always returns to the list, which is what the next click is for.
    /// </summary>
    private void ShowSettingsPage()
    {
        CloseDrawer(animate: false);
        FadeBackdrop(null, spansDrawer: false);
        _settingsToggleReady = false;
        HoverBackgroundsSwitch.IsOn = _state.ShowHoverBackgrounds;
        RecentFoldersSwitch.IsOn = _state.RememberRecentLaunches;
        _settingsToggleReady = true;

        MainPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        PlaceWindow();
        DispatcherQueue.TryEnqueue(() => HoverBackgroundsSwitch.Focus(FocusState.Programmatic));
    }

    private void ShowMainPage()
    {
        if (MainPage.Visibility == Visibility.Visible)
            return;
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
        if (!_state.ShowHoverBackgrounds)
            FadeBackdrop(null, spansDrawer: false);
    }

    private void RecentFoldersSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _state.RememberRecentLaunches == RecentFoldersSwitch.IsOn)
            return;
        _state.RememberRecentLaunches = RecentFoldersSwitch.IsOn;
        _store.SaveState(_state);
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (SettingsPage.Visibility == Visibility.Visible)
                ShowMainPage();
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
    /// <summary>The row whose content the drawer is showing.</summary>
    private AgentItem? _drawerItem;

    private void ClearUsageCache()
    {
        _claudeSetup = null;
        _claudeUsage = _codexUsage = null;
        _codexUsageLoaded = false;
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
    /// Claude Code's saved usage, or the offer to set Agent Dock up as its status line (the only
    /// supported way to get those numbers).
    /// </summary>
    private bool PrepareClaudeDrawer(AgentItem item)
    {
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
            RecentRows.Children.Add(button);
        }
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

    private void ShowUsage(string title, PlanUsage? usage, string emptyNote)
    {
        UsageTitle.Text = title;
        UsagePanel.Visibility = Visibility.Visible;
        SessionLabel.Text = "Session";
        SessionBar.Visibility = WeekGroup.Visibility = Visibility.Visible;
        var hasData = usage is { FiveHour: not null } or { SevenDay: not null };
        UsageRows.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        UsageNote.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = emptyNote;
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
        /// <summary>Sized for the list and the open drawer together.</summary>
        public bool SpansDrawer { get; set; }
    }

    private readonly Dictionary<string, BitmapImage> _backdrops = new(StringComparer.OrdinalIgnoreCase);
    private BackdropLayer[]? _layers;
    private int _activeLayer;
    private Storyboard? _backdropFade;

    private BackdropLayer[] Layers => _layers ??=
        [new BackdropLayer(HoverImageA), new BackdropLayer(HoverImageB)];

    private void AgentRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var item = ((FrameworkElement)sender).DataContext as AgentItem;
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
            FadeBackdrop(item?.BackgroundSource, spansDrawer: drawer);
    }

    private void AgentRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Moving into the drawer should keep the picture. It fades when the drawer actually closes,
        // or straight away when the row had no drawer.
        if (_state.ShowHoverBackgrounds && !_drawerOpen)
            FadeBackdrop(null, spansDrawer: false);
        ScheduleDrawerClose();
    }

    /// <summary>
    /// Fades the given picture in behind the flyout (and the drawer, when <paramref name="spansDrawer"/>),
    /// cross-fading from whatever picture was showing, or fades out when <paramref name="path"/> is null.
    /// </summary>
    private void FadeBackdrop(string? path, bool spansDrawer)
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
            layers[_activeLayer].SpansDrawer = spansDrawer;
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
    /// Sizes a picture to cover the list, or the list and the open drawer, keeping the part over the
    /// list still while the window grows and uncovers the rest.
    /// </summary>
    private void LayoutBackdrop(BackdropLayer layer)
    {
        if (layer.Image.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } image)
            return;

        var height = Root.ActualHeight;
        var canvasWidth = ListWidthDip + (layer.SpansDrawer ? DrawerWidthDip : 0);
        var scale = Math.Max(canvasWidth / image.PixelWidth, height / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var imageHeight = image.PixelHeight * scale;
        // Right side: the window's left edge is the canvas left, so this margin stays put as the
        // window grows right. Left side: keep the picture's right edge on the list's right edge,
        // which stays put while the window grows left.
        var left = layer.SpansDrawer && _drawerSide < 0
            ? ListWidthDip + DrawerDip() - width
            : canvasWidth - width;

        layer.Image.Width = width;
        layer.Image.Height = imageHeight;
        layer.Image.Margin = new Thickness(left, (height - imageHeight) / 2, 0, 0);
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
        // The row's pointer-exit may already have started fading the picture. Bring it back.
        if (_state.ShowHoverBackgrounds && _drawerItem?.BackgroundSource is { } source)
            FadeBackdrop(source, spansDrawer: true);
    }

    private void Drawer_PointerExited(object sender, PointerRoutedEventArgs e) => ScheduleDrawerClose();

    /// <summary>The animated width changed, so the window grows or shrinks with it.</summary>
    private void OnDrawerWidthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!_drawerOpen || !AppWindow.IsVisible)
            return;
        ApplyWindowForDrawer();
        // On the left the picture is pinned to the list's right edge, so it has to move with the width.
        if (_drawerSide < 0 && _layers is not null)
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
        if (!_drawerOpen || _drawerClosing)
            return;
        if (_drawerCloseTimer is null)
        {
            _drawerCloseTimer = DispatcherQueue.CreateTimer();
            _drawerCloseTimer.Interval = TimeSpan.FromMilliseconds(250);
            _drawerCloseTimer.IsRepeating = false;
            _drawerCloseTimer.Tick += (_, _) =>
            {
                if (_state.ShowHoverBackgrounds)
                    FadeBackdrop(null, spansDrawer: false);
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
        var maxHeightDip = Math.Min(MaxHeightDip, (work.Height - 2 * gap) / scale);
        var heightDip = Root.DesiredSize.Height > 1
            ? Math.Min(Root.DesiredSize.Height, maxHeightDip)
            : Math.Min(AppWindow.ClientSize.Height / scale, maxHeightDip);

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
