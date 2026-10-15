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
    /// <summary>The drawer's width; matches DrawerContent in the XAML.</summary>
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
        // Don't reopen with the last hover's image or drawer showing.
        FadeBackdrop(null, spansDrawer: false);
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
        UpdateUsage();
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

    private void Launch(AgentItem item)
    {
        if (Launcher.Launch(item, item.Preset?.Folder ?? CurrentFolder, _settings) is { } error)
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

    // ---------- Claude usage ----------

    /// <summary>
    /// Shows the saved Claude plan usage, or offers to set Agent Dock up as Claude Code's status
    /// line (the only supported way to get those numbers). Only when Claude Code is installed.
    /// </summary>
    private void UpdateUsage()
    {
        UsagePanel.Visibility = Visibility.Collapsed;
        UsageOffer.IsOpen = false;
        if (!_agents.Any(a => a.IsCli && a.IsAvailable && IsClaudeCode(a.Definition.Command)))
            return;

        var setup = ClaudeStatusLine.CheckSetup(Environment.ProcessPath!);
        switch (setup.State)
        {
            case ClaudeStatusLine.SetupState.Installed:
                ShowUsage(ClaudeStatusLine.Load());
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
        }
    }

    private void OfferUsage(InfoBarSeverity severity, string button, string message, bool closable)
    {
        UsageOffer.Severity = severity;
        UsageOffer.Message = message;
        UsageOffer.IsClosable = closable;
        UsageOfferButton.Content = button;
        UsageOffer.IsOpen = true;
    }

    private void ShowUsage(ClaudeUsage? usage)
    {
        UsagePanel.Visibility = Visibility.Visible;
        var hasData = usage is { FiveHour: not null } or { SevenDay: not null };
        UsageRows.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        UsageNote.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = "Your limits appear here after your next Claude Code reply.";
        UsageUpdatedText.Text = "";
        if (!hasData)
            return;

        var now = DateTimeOffset.UtcNow;
        ShowWindow(SessionBar, SessionText, usage!.FiveHour, now);
        ShowWindow(WeekBar, WeekText, usage.SevenDay, now);
        // Claude Code only refreshes its status line while a session is running, so say how old this is.
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

    private static bool IsClaudeCode(string? command) =>
        !string.IsNullOrWhiteSpace(command)
        && Path.GetFileNameWithoutExtension(command.Trim().Trim('"')).Equals("claude", StringComparison.OrdinalIgnoreCase);

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
        UpdateUsage();
        StatusText.Text = "Claude Code now uses Agent Dock as its status line.";
        PlaceWindow(); // the footer message may wrap
    }

    private void UsageOffer_CloseButtonClick(InfoBar sender, object args)
    {
        _state.ClaudeUsageOfferDismissed = true;
        _store.SaveState(_state);
        CloseDrawer(animate: true); // nothing left to show in it
    }

    // ---------- Hover background ----------

    private readonly Dictionary<string, BitmapImage> _backdrops = new(StringComparer.OrdinalIgnoreCase);
    private Storyboard? _backdropFade;
    /// <summary>The current hover image is laid out across the flyout and the drawer together.</summary>
    private bool _backdropSpansDrawer;

    private void AgentRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var item = ((FrameworkElement)sender).DataContext as AgentItem;
        var drawer = item is not null && HasDrawer(item);
        // The drawer first: the image layout depends on which side it opens.
        if (drawer)
            OpenDrawer();
        else
            ScheduleDrawerClose();
        FadeBackdrop(item?.BackgroundPath, spansDrawer: drawer);
    }

    private void AgentRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        FadeBackdrop(null, spansDrawer: false);
        ScheduleDrawerClose();
    }

    /// <summary>
    /// Fades the given image in behind the flyout (and the drawer, when <paramref name="spansDrawer"/>),
    /// or fades out when <paramref name="path"/> is null.
    /// </summary>
    private void FadeBackdrop(string? path, bool spansDrawer)
    {
        if (path is not null)
        {
            if (!_backdrops.TryGetValue(path, out var image))
            {
                try
                {
                    _backdrops[path] = image = new BitmapImage(new Uri(path));
                    // The layout needs the image's proportions, which are known once it has loaded.
                    image.ImageOpened += (_, _) => LayoutBackdrop();
                }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException)
                {
                    return;
                }
            }
            HoverImage.Source = DrawerImage.Source = image;
            _backdropSpansDrawer = spansDrawer;
            LayoutBackdrop();
        }

        var target = path is null ? 0 : HoverBackdropOpacity;
        // Stopping a storyboard snaps back to the value before it ran, so carry the current one over.
        var current = HoverBackdrop.Opacity;
        _backdropFade?.Stop();
        HoverBackdrop.Opacity = DrawerImage.Opacity = current;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled || !AppWindow.IsVisible)
        {
            HoverBackdrop.Opacity = DrawerImage.Opacity = target;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(path is null ? 600 : 900));
        var storyboard = new Storyboard();
        foreach (var element in new UIElement[] { HoverBackdrop, DrawerImage })
        {
            var fade = new DoubleAnimation
            {
                From = current, To = target, Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
        }
        _backdropFade = storyboard;
        storyboard.Begin();
    }

    /// <summary>
    /// Sizes the hover image to fill the flyout (or, with the drawer, the flyout and drawer side by
    /// side), keeping its right edge in view, then gives each panel its own slice so they line up.
    /// </summary>
    private void LayoutBackdrop()
    {
        if (HoverImage.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } image)
            return;

        var flyoutWidth = Root.ActualWidth;
        var height = Root.ActualHeight;
        var canvasWidth = flyoutWidth + (_backdropSpansDrawer ? DrawerWidthDip : 0);
        // Where each panel sits on that shared canvas.
        var flyoutLeft = _backdropSpansDrawer && _drawerSide < 0 ? DrawerWidthDip : 0;
        var drawerLeft = _drawerSide > 0 ? flyoutWidth : 0;

        var scale = Math.Max(canvasWidth / image.PixelWidth, height / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var imageHeight = image.PixelHeight * scale;
        var left = canvasWidth - width;          // right-aligned
        var top = (height - imageHeight) / 2;    // vertically centred

        Size(HoverImage);
        HoverImage.Margin = new Thickness(left - flyoutLeft, top, 0, 0);

        // The drawer's slice is anchored to the edge touching the flyout, which doesn't move while
        // the drawer grows: its left edge on the right side, its right edge on the left side.
        Size(DrawerImage);
        DrawerImage.Margin = _drawerSide > 0
            ? new Thickness(left - drawerLeft, top, 0, 0)
            : new Thickness(0, top, DrawerWidthDip - (left + width), 0);

        void Size(Image element)
        {
            element.Width = width;
            element.Height = imageHeight;
        }
    }

    // ---------- Drawer ----------

    /// <summary>Width the drawer starts from and closes to; zero would leave the popup with no window.</summary>
    private const double DrawerClosedWidthDip = 1;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerCloseTimer;
    private Storyboard? _drawerSlide;
    private bool _drawerClosing;
    /// <summary>+1 when the drawer opens to the right of the flyout, -1 to the left.</summary>
    private int _drawerSide = 1;

    /// <summary>Only Claude Code (and its presets) has a drawer so far: its plan usage, or the setup offer.</summary>
    private bool HasDrawer(AgentItem item) =>
        item.IsCli && IsClaudeCode(item.Definition.Command)
        && (UsagePanel.Visibility == Visibility.Visible || UsageOffer.IsOpen);

    private void Drawer_PointerEntered(object sender, PointerRoutedEventArgs e) => _drawerCloseTimer?.Stop();

    private void Drawer_PointerExited(object sender, PointerRoutedEventArgs e) => ScheduleDrawerClose();

    /// <summary>On the left, the drawer grows leftwards: keep its right edge on the flyout's left edge.</summary>
    private void DrawerContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_drawerSide < 0)
            Drawer.HorizontalOffset = -e.NewSize.Width;
    }

    /// <summary>
    /// A short grace period, so the pointer can move from the row into the drawer (to click
    /// Set up) without it closing.
    /// </summary>
    private void ScheduleDrawerClose()
    {
        if (!Drawer.IsOpen || _drawerClosing)
            return;
        if (_drawerCloseTimer is null)
        {
            _drawerCloseTimer = DispatcherQueue.CreateTimer();
            _drawerCloseTimer.Interval = TimeSpan.FromMilliseconds(250);
            _drawerCloseTimer.IsRepeating = false;
            _drawerCloseTimer.Tick += (_, _) => CloseDrawer(animate: true);
        }
        _drawerCloseTimer.Start();
    }

    private void OpenDrawer()
    {
        _drawerCloseTimer?.Stop();
        if (Drawer.IsOpen && !_drawerClosing)
            return;

        if (!Drawer.IsOpen)
        {
            // Right of the flyout if it fits on this monitor, otherwise left.
            var scale = Root.XamlRoot.RasterizationScale;
            var right = AppWindow.Position.X + AppWindow.Size.Width + (int)Math.Ceiling(DrawerWidthDip * scale);
            _drawerSide = Win32.MonitorInfoAt(_anchor, out _) is { } info && right > info.rcWork.Right ? -1 : 1;
            var onRight = _drawerSide > 0;

            // Flush against the flyout, with no rounded corners or border on the side that touches it,
            // so the two read as one panel.
            DrawerContent.CornerRadius = onRight ? new CornerRadius(0, 8, 8, 0) : new CornerRadius(8, 0, 0, 8);
            DrawerContent.BorderThickness = onRight ? new Thickness(0, 1, 1, 1) : new Thickness(1, 1, 0, 1);
            DrawerContent.Height = Root.ActualHeight;
            // The image stays with the flyout; the content travels with the drawer's outer edge.
            DrawerImage.HorizontalAlignment = onRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            DrawerPanel.HorizontalAlignment = onRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            LayoutBackdrop();

            DrawerContent.Width = DrawerClosedWidthDip;
            Drawer.HorizontalOffset = onRight ? Root.ActualWidth : -DrawerClosedWidthDip;
            Drawer.VerticalOffset = 0;
            Drawer.IsOpen = true;
        }
        _drawerClosing = false;
        SlideDrawer(DrawerWidthDip, null);
    }

    private void CloseDrawer(bool animate)
    {
        _drawerCloseTimer?.Stop();
        if (!Drawer.IsOpen)
            return;

        if (!animate || !AppWindow.IsVisible)
        {
            _drawerSlide?.Stop();
            _drawerClosing = false;
            Drawer.IsOpen = false;
            return;
        }

        _drawerClosing = true;
        // Shrinks back into the flyout's edge, then closes, unless it was reopened meanwhile.
        SlideDrawer(DrawerClosedWidthDip, () =>
        {
            if (_drawerClosing)
            {
                _drawerClosing = false;
                Drawer.IsOpen = false;
            }
        });
    }

    /// <summary>Animates the drawer's width, which grows or shrinks the popup window itself.</summary>
    private void SlideDrawer(double toWidth, Action? completed)
    {
        // As with the backdrop fade: stopping snaps back, so carry the current width over.
        var from = DrawerContent.ActualWidth > 0 ? DrawerContent.ActualWidth : DrawerContent.Width;
        _drawerSlide?.Stop();
        DrawerContent.Width = from;

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            DrawerContent.Width = toWidth;
            completed?.Invoke();
            return;
        }

        var opening = toWidth > from;
        var slide = new DoubleAnimation
        {
            From = from,
            To = toWidth,
            Duration = new Duration(TimeSpan.FromMilliseconds(opening ? 250 : 200)),
            EasingFunction = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn },
            // Width affects layout, so WinUI only animates it when asked to explicitly.
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(slide, DrawerContent);
        Storyboard.SetTargetProperty(slide, "Width");
        var storyboard = new Storyboard { Children = { slide } };
        if (completed is not null)
            storyboard.Completed += (_, _) =>
            {
                if (_drawerSlide == storyboard)
                    completed();
            };
        _drawerSlide = storyboard;
        storyboard.Begin();
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

        var widthDip = ListWidthDip;
        Root.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));
        var maxHeightDip = Math.Min(MaxHeightDip, (work.Height - 2 * gap) / scale);
        var heightDip = Root.DesiredSize.Height > 1
            ? Math.Min(Root.DesiredSize.Height, maxHeightDip)
            : Math.Min(AppWindow.ClientSize.Height / scale, maxHeightDip);

        // Window size includes the thin border around the client area.
        var borderW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var borderH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var w = (int)Math.Ceiling(widthDip * scale) + borderW;
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
