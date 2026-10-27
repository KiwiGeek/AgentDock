using System.Collections.ObjectModel;
using AgentDock.Interop;
using AgentDock.Models;
using AgentDock.Services;
using AgentDock.Services.Agents;
using AgentDock.Services.Launching;
using AgentDock.Services.Usage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;

namespace AgentDock.Views;

/// <summary>
/// Agent Dock's settings: a master-detail window (owner's decision, 2026-10-19). The left list is
/// General, Accounts, each agent (drag to reorder), and Add agents (owner's decision, 2026-10-20:
/// every sign-in and API key in one Accounts page, instead of a list of keys of their own); the
/// right pane shows only that item's details. One instance, owned by <see cref="App"/>: closing
/// only hides it. Every change is saved at once, and <c>onChanged</c> tells the flyout what moved.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const double DefaultWidthDip = 880;
    private const double DefaultHeightDip = 720;
    private const double MinWidthDip = 560;
    private const double MinHeightDip = 400;
    private const string TagGeneral = "general";
    private const string TagAddAgents = "add-agents";
    private const string TagAccounts = "accounts";
    private const string TagAgentPrefix = "agent:";
    private const string TagApiPrefix = "api:";

    private readonly SettingsStore _store;
    private readonly Action<SettingsChange> _onChanged;
    /// <summary>Own icon cache: images belong to the window that shows them, and the flyout's aren't shared.</summary>
    private readonly ShellIcons _icons = new();
    private readonly ObservableCollection<string> _aiderModels = [];
    private readonly ObservableCollection<AgentListRow> _agentRows = [];
    private readonly nint _hwnd;

    private DockSettings _settings = new();
    private DockState _state = new();
    /// <summary>Why agents.json couldn't be read this time, if it couldn't. Nothing that would write it is allowed.</summary>
    private string? _settingsError;
    /// <summary>Every agent in the list, resolved, for its icon and whether it's installed. Not presets.</summary>
    private List<AgentItem> _agents = [];
    /// <summary>False while a control is being set from state, so that doesn't count as a change.</summary>
    private bool _settingsToggleReady;
    /// <summary>False while a left-list selection is being set in code, so that doesn't count as a user click.</summary>
    private bool _navReady = true;
    private bool _placed;
    /// <summary>Tag of the left-list item whose details are showing.</summary>
    private string _selectedTag = TagGeneral;
    /// <summary>API account whose editor is in the right pane, if any.</summary>
    private ApiAccount? _editingApi;

    public SettingsWindow(SettingsStore store, Action<SettingsChange> onChanged)
    {
        _store = store;
        _onChanged = onChanged;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentDock.ico");
        AppWindow.SetIcon(icon);
        AppWindow.Title = "Agent Dock settings"; // the taskbar and Alt+Tab still use it

        // The content runs up under the title bar (owner's request, 2026-10-20), so the Mica is one
        // surface from top to bottom: a tall caption area, with transparent caption buttons over it.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        try { AppTitleIcon.Source = new BitmapImage(new Uri(icon)); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { }
        // Dimmed while another window is active, as a system title bar's text is.
        Activated += (_, e) => AppTitleText.Foreground = (Brush)Application.Current.Resources[
            e.WindowActivationState == WindowActivationState.Deactivated ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"];

        // An ordinary window: a title bar, in the taskbar and Alt+Tab, resizable.
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
        AppWindow.SetPresenter(presenter);

        // Closing only hides it (the flyout's rule too); quitting the app really closes it.
        AppWindow.Closing += (_, e) =>
        {
            if (App.IsExiting)
                return;
            e.Cancel = true;
            HideSettings();
        };

        AiderModelResults.ItemsSource = _aiderModels;
        AgentNavList.ItemsSource = _agentRows;
        FillLaunchChoices();
    }

    // ---------- Show / hide ----------

    /// <summary>
    /// Shows the window, reading the settings and state files again.
    /// <paramref name="section"/> is general, agents, api or about (picks a sensible left-list item).
    /// With <paramref name="agentId"/> it selects that agent; with <paramref name="addAgents"/>,
    /// Add agents (from the flyout's welcome or offer).
    /// </summary>
    public void Show(string? agentId = null, bool addAgents = false, string? section = null)
    {
        // Already open and just asked to come forward with no destination: keep whatever is open.
        if (AppWindow.IsVisible && agentId is null && !addAgents && section is null)
        {
            BringToFront();
            return;
        }

        Reload();
        PlaceFirstTime();
        AppWindow.Show();
        BringToFront();

        FillSettingsPage();
        RebuildNavMenu();

        var definition = agentId is null
            ? null
            : _settings.Agents.FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (definition is not null)
            SelectTag(TagAgentPrefix + definition.Id);
        else if (addAgents && _settingsError is null)
        {
            _addAgentsFromSetup = true;
            SelectTag(TagAddAgents);
        }
        else
            SelectTag(TagForSection(section));
        _ = LoadAgentIconsAsync();
    }

    private string TagForSection(string? section) =>
        section?.ToLowerInvariant() switch
        {
            "agents" when _settings.Agents.Count > 0 => TagAgentPrefix + _settings.Agents[0].Id,
            "agents" => TagAddAgents,
            "api" or "accounts" => TagAccounts,
            "about" => TagAbout,
            _ => TagGeneral
        };

    private void BringToFront()
    {
        Win32.SetForegroundWindow(_hwnd);
        Activate();
    }

    /// <summary>
    /// Hides the window. Anything typed but not yet committed is committed first, since a hidden
    /// page doesn't always report its text box losing focus.
    /// </summary>
    private void HideSettings()
    {
        CommitEditors();
        // Moves the focus off any box (an API key, say) so its LostFocus saves it.
        HoverBackgroundsSwitch.Focus(FocusState.Programmatic);
        StopSlideshow();
        AppWindow.Hide();
    }

    /// <summary>Settings and state, read from disk, with the agents resolved for their icons and installed state.</summary>
    private void Reload()
    {
        (_settings, _settingsError) = _store.LoadSettings();
        _state = _store.LoadState();
        // The resident process starts at sign-in; a CLI installed since would otherwise show as missing.
        PathResolver.RefreshPath();
        _agents = AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id));
        ApplyCachedIcons();
    }

    /// <summary>Sizes and centres the window on the monitor the cursor is on, once; after that it stays where the user put it.</summary>
    private void PlaceFirstTime()
    {
        if (_placed)
            return;
        _placed = true;

        Win32.GetCursorPos(out var cursor);
        if (Win32.MonitorInfoAt(cursor, out var monitor) is not { } info)
            return;
        var scale = Win32.ScaleFor(monitor);
        var work = info.rcWork;

        // Too small and the agent page's rows would clip. The presenter takes pixels, so this uses
        // the scale of the monitor it opens on.
        if (AppWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.PreferredMinimumWidth = (int)Math.Ceiling(MinWidthDip * scale);
            overlapped.PreferredMinimumHeight = (int)Math.Ceiling(MinHeightDip * scale);
        }

        var width = (int)Math.Min(Math.Ceiling(DefaultWidthDip * scale), work.Width * 0.95);
        var height = (int)Math.Min(Math.Ceiling(DefaultHeightDip * scale), work.Height * 0.9);
        AppWindow.MoveAndResize(new RectInt32(
            work.Left + (work.Width - width) / 2,
            work.Top + (work.Height - height) / 2,
            width, height));
    }

    /// <summary>Icons for the left list: compiled in, or already extracted by the flyout into the cache.</summary>
    private async Task LoadAgentIconsAsync()
    {
        var loaded = false;
        var sources = _agents.Select(a => a.IconSource)
            .Concat(_settings.ApiAccounts.Select(a => ApiUsage.ProviderIcon(a.Provider)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var source in sources)
            if (_icons.Get(source) is null && ShellIcons.IsOnDisk(source) && await _icons.LoadFromDiskAsync(source) is not null)
                loaded = true;
        ApplyCachedIcons();
        // Nav items were built before these arrived; refresh their icons.
        if (loaded)
        {
            RebuildNavMenu(_selectedTag);
            if (_selectedTag == TagAccounts)
                FillApiKeyRows();
        }
    }

    private void ApplyCachedIcons()
    {
        foreach (var agent in _agents)
            agent.Icon ??= _icons.Get(agent.IconSource);
    }

    // ---------- Saving ----------

    /// <summary>
    /// Changes the state file's contents: reads it again first, so what the flyout saved since this
    /// window loaded it (recent folders, a launch's recents) isn't written over, then applies
    /// <paramref name="change"/>, saves, and tells the flyout.
    /// </summary>
    private void UpdateState(Action<DockState> change, SettingsChange what = SettingsChange.State)
    {
        _state = _store.LoadState();
        change(_state);
        _store.SaveState(_state);
        _onChanged(what);
    }

    /// <summary>
    /// Saves the agent list (and the rest of agents.json), and tells the flyout, which shows it as
    /// saved: its list, its icons and Start menu lookups, and the jump list. False (nothing saved)
    /// when agents.json couldn't be read this time, so a hand edit with a mistake in it is never
    /// written over. After a drag reorder, pass <paramref name="refreshNav"/> false so the list
    /// that was just dragged isn't rebuilt under the pointer.
    /// </summary>
    private bool SaveAgentList(SettingsChange also = SettingsChange.None, bool refreshNav = true)
    {
        if (_settingsError is not null)
            return false;
        _store.SaveSettings(_settings);
        _agents = AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id));
        ApplyCachedIcons();
        if (!_state.AgentSetupDone)
            UpdateState(s => s.AgentSetupDone = true, SettingsChange.Agents | also);
        else
            _onChanged(SettingsChange.Agents | also);
        if (refreshNav)
            RebuildNavMenu(_selectedTag);
        _ = LoadAgentIconsAsync();
        return true;
    }

    /// <summary>Esc closes the window. A pop-up that's open takes the key itself.</summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape
            || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count > 0)
            return;
        HideSettings();
        e.Handled = true;
    }

    /// <summary>General, Accounts, Add agents or About was chosen in the left list.</summary>
    private void StaticNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_navReady || sender is not ListView list || list.SelectedItem is not ListViewItem { Tag: string tag })
            return;
        if (tag == _selectedTag)
            return;
        CommitEditors();
        ShowTag(tag);
    }

    private void AgentNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_navReady || AgentNavList.SelectedItem is not AgentListRow row)
            return;
        var tag = TagAgentPrefix + row.Definition.Id;
        if (tag == _selectedTag)
            return;
        CommitEditors();
        ShowTag(tag);
    }

    /// <summary>
    /// An agent was dragged to a new place in the left list: keep that order in agents.json and
    /// tell the flyout. Refuses while agents.json couldn't be read.
    /// </summary>
    private void AgentNav_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            return;
        if (_settingsError is not null)
        {
            RebuildNavMenu(_selectedTag); // put the list back; don't write over a broken file
            return;
        }
        _settings.Agents = _agentRows.Select(r => r.Definition).ToList();
        SaveAgentList(refreshNav: false);
    }

    /// <summary>Selects a left-list item and shows its details (also used for deep links).</summary>
    private void SelectTag(string tag)
    {
        RebuildNavMenu(tag);
        ShowTag(tag);
    }

    /// <summary>
    /// Rebuilds the left agent list. Keeps <paramref name="selectTag"/> selected when
    /// still present. Drag reorder is off while agents.json couldn't be read.
    /// </summary>
    private void RebuildNavMenu(string? selectTag = null)
    {
        selectTag ??= _selectedTag;
        _navReady = false;
        var editable = _settingsError is null;
        AgentNavList.CanReorderItems = AgentNavList.CanDragItems = AgentNavList.AllowDrop = editable;
        AddAgentsNavItem.IsEnabled = editable;

        _agentRows.Clear();
        foreach (var definition in _settings.Agents)
        {
            var resolved = _agents.FirstOrDefault(a => a.Preset is null && ReferenceEquals(a.Definition, definition));
            _agentRows.Add(new AgentListRow(definition, resolved?.Icon, resolved?.IsAvailable ?? false));
        }

        SyncNavSelection(selectTag);
        _navReady = true;
    }

    /// <summary>Highlights the left-list row for <paramref name="tag"/> and clears the others.</summary>
    private void SyncNavSelection(string tag)
    {
        // An API key's page belongs to Accounts, so Accounts stays highlighted.
        GeneralNav.SelectedItem = tag == TagGeneral ? GeneralNavItem
            : tag == TagAccounts || tag.StartsWith(TagApiPrefix, StringComparison.Ordinal) ? AccountsNavItem
            : null;
        AddAgentsNav.SelectedItem = tag == TagAddAgents ? AddAgentsNavItem : null;
        AboutNav.SelectedItem = tag == TagAbout ? AboutNavItem : null;

        if (tag.StartsWith(TagAgentPrefix, StringComparison.Ordinal))
        {
            var id = tag[TagAgentPrefix.Length..];
            AgentNavList.SelectedItem = _agentRows.FirstOrDefault(r =>
                r.Definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        else
            AgentNavList.SelectedItem = null;
    }

    /// <summary>Shows the right-pane details for a left-list tag.</summary>
    private void ShowTag(string tag)
    {
        _selectedTag = tag;
        _navReady = false;
        SyncNavSelection(tag);
        _navReady = true;

        HideAllPanes();
        if (tag == TagGeneral)
        {
            _editingAgent = null;
            _editingApi = null;
            GeneralPage.Visibility = Visibility.Visible;
            SettingsTitle.Text = "General";
            SyncAgentBackdrop();
        }
        else if (tag == TagAddAgents)
        {
            _editingApi = null;
            ShowAddAgents();
            return; // sets title, pane and backdrop itself
        }
        else if (tag == TagAccounts)
        {
            ShowAccounts();
            return;
        }
        else if (tag == TagAbout)
        {
            ShowAbout();
            return;
        }
        else if (tag.StartsWith(TagAgentPrefix, StringComparison.Ordinal)
                 && _settings.Agents.FirstOrDefault(a => a.Id.Equals(tag[TagAgentPrefix.Length..], StringComparison.OrdinalIgnoreCase)) is { } agent)
        {
            _editingApi = null;
            ShowAgentSettings(agent);
            return;
        }
        else if (tag.StartsWith(TagApiPrefix, StringComparison.Ordinal)
                 && _settings.ApiAccounts.FirstOrDefault(a => a.Id.Equals(tag[TagApiPrefix.Length..], StringComparison.OrdinalIgnoreCase)) is { } account)
        {
            _editingAgent = null;
            ShowApiAccount(account);
            return;
        }
        else
        {
            // Tag referred to something removed; fall back to General.
            _selectedTag = TagGeneral;
            _editingAgent = null;
            _navReady = false;
            SyncNavSelection(TagGeneral);
            _navReady = true;
            GeneralPage.Visibility = Visibility.Visible;
            SettingsTitle.Text = "General";
            SyncAgentBackdrop();
        }
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void HideAllPanes()
    {
        foreach (var panel in AgentSettingsPanels)
            panel.Visibility = Visibility.Collapsed;
        GeneralPage.Visibility = AgentEditor.Visibility = AddAgentsPage.Visibility = ApiPage.Visibility = AccountsPage.Visibility
            = AboutPage.Visibility = Visibility.Collapsed;
        StopSlideshow();
        // Only an API key's page has Accounts in front of its title.
        AccountsCrumb.Visibility = AccountsCrumbChevron.Visibility = Visibility.Collapsed;
    }

    // ---------- Agent page background (waifu picture) ----------

    private const double AgentBackdropOpacity = 0.3;
    private readonly Dictionary<string, BitmapImage> _agentBackdrops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the agent's hover picture behind her settings page while waifus are on; clears it
    /// otherwise. Same pictures as the flyout, at the same opacity.
    /// </summary>
    private void SyncAgentBackdrop()
    {
        var source = _state.ShowHoverBackgrounds && _editingAgent is { } definition
            ? AgentIcons.FindBackground(definition)
            : null;
        if (source is null)
        {
            AgentBackdrop.Visibility = Visibility.Collapsed;
            AgentBackdrop.Source = null;
            return;
        }

        if (!_agentBackdrops.TryGetValue(source, out var image))
        {
            image = new BitmapImage();
            _agentBackdrops[source] = image;
            if (AgentIcons.IsBundledBackground(source))
                _ = LoadAgentBackdropAsync(image, source);
            else
            {
                try { image.UriSource = new Uri(source); }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException)
                {
                    AgentBackdrop.Visibility = Visibility.Collapsed;
                    return;
                }
            }
        }

        AgentBackdrop.Source = image;
        AgentBackdrop.Opacity = AgentBackdropOpacity;
        AgentBackdrop.Visibility = Visibility.Visible;
    }

    private async Task LoadAgentBackdropAsync(BitmapImage image, string source)
    {
        try
        {
            using var stream = AgentIcons.OpenBundledBackground(source);
            if (stream is null)
                return;
            await image.SetSourceAsync(stream.AsRandomAccessStream());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A missing picture just means no background on this page.
        }
    }

    private void CommitEditors()
    {
        CommitAgentName();
        CommitAgentArguments();
        CommitAiderModel();
    }

    // ---------- Agents: each agent's page, adding ----------
    /// <summary>The agent whose page is showing (the object in <see cref="DockSettings.Agents"/>), if one is.</summary>
    private AgentDefinition? _editingAgent;
    /// <summary>Add agents was opened from the welcome, so adding goes straight back to the dock.</summary>
    private bool _addAgentsFromSetup;

    /// <summary>
    /// Settings that only some agents have, shown under the agent's page, by catalog id or command name. To give
    /// another agent some, add a panel to the settings page in the XAML (like ClaudeSettings), return
    /// it here, list it in <see cref="AgentSettingsPanels"/>, and fill it in <see cref="UpdateAgentPanel"/>.
    /// </summary>
    private FrameworkElement? AgentSettingsPanelFor(AgentDefinition definition)
    {
        if ((definition.Catalog ?? definition.Id).Equals("aider", StringComparison.OrdinalIgnoreCase)
            || KnownAgents.CommandName(definition.Command) == "aider")
            return AiderSettings;

        return KnownAgents.CommandName(definition.Command) switch
        {
            "claude" => ClaudeSettings,
            "grok" => GrokSettings,
            "cursor-agent" => CursorSettings,
            "kiro-cli" => KiroSettings,
            _ => null
        };
    }

    private IEnumerable<FrameworkElement> AgentSettingsPanels =>
        [ClaudeSettings, GrokSettings, CursorSettings, KiroSettings, CopilotSettings, AiderSettings];


    private void ShowAgentSettings(AgentDefinition definition)
    {
        _editingAgent = definition;
        _selectedTag = TagAgentPrefix + definition.Id;
        var isCli = definition.Kind == AgentKind.Cli;
        var command = KnownAgents.CommandName(definition.Command);

        _settingsToggleReady = false;
        AgentNameBox.Text = definition.Name;
        AgentArgumentsBox.Text = definition.Arguments ?? "";
        _settingsToggleReady = true;
        AgentArgumentsBox.Visibility = isCli ? Visibility.Visible : Visibility.Collapsed;
        AgentCommandText.Text = isCli
            ? LaunchDescription(definition)
            : "A desktop app, opened from the Start menu.";
        AgentEnvSection.Visibility = isCli ? Visibility.Visible : Visibility.Collapsed;
        AgentEnvNote.Text = command switch
        {
            "claude" => @"Set when this agent starts. For a second Claude account, duplicate it and set CLAUDE_CONFIG_DIR to its own folder, such as %USERPROFILE%\.claude-work. Write %NAME% to use a value from Windows, so keys stay out of agents.json.",
            "codex" => @"Set when this agent starts. For a second Codex account, duplicate it and set CODEX_HOME to its own folder, such as %USERPROFILE%\.codex-work. Write %NAME% to use a value from Windows, so keys stay out of agents.json.",
            _ => "Set when this agent starts. Write %NAME% to use a value from Windows, so keys stay out of agents.json."
        };
        FillEnvRows();
        // Folded away unless it has something in it: the account above covers API keys now.
        AgentEnvSection.IsExpanded = definition.Env is { Count: > 0 };
        EnvNameBox.Text = EnvValueBox.Text = "";
        RemoveAgentText.Text = $"Remove {definition.Name} from your agents? Presets for it stay until you remove them.";

        HideAllPanes();
        AgentEditor.Visibility = Visibility.Visible;
        UpdateAgentPanel(definition);
        SettingsTitle.Text = definition.Name;
        SyncAgentBackdrop();
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void FillEnvRows()
    {
        AgentEnvRows.Children.Clear();
        var count = _editingAgent?.Env?.Count ?? 0;
        AgentEnvSummary.Text = count switch
        {
            0 => "None set. For anything the account doesn't cover, such as a second config folder.",
            1 => "1 variable",
            _ => $"{count} variables"
        };
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
    }

    private void EditorBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        if (ReferenceEquals(sender, AgentNameBox))
            CommitAgentName();
        else if (ReferenceEquals(sender, AiderModelBox))
            CommitAiderModel();
        else
            CommitAgentArguments();
        e.Handled = true;
    }

    private void AgentNameBox_LostFocus(object sender, RoutedEventArgs e) => CommitAgentName();

    private void AgentArgumentsBox_LostFocus(object sender, RoutedEventArgs e) => CommitAgentArguments();

    private void AiderModelBox_LostFocus(object sender, RoutedEventArgs e) => CommitAiderModel();

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
        SaveAgentList(); // rebuilds the left list with the new name
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
        AgentCommandText.Text = LaunchDescription(_editingAgent);
    }

    private void CommitAiderModel()
    {
        if (!_settingsToggleReady || _editingAgent is null)
            return;
        var model = AiderModelBox.Text.Trim() is { Length: > 0 } text ? text : null;
        if (model?.Any(char.IsWhiteSpace) == true)
        {
            AiderModelBox.Text = _editingAgent.Model ?? "";
            return;
        }
        if (model == _editingAgent.Model)
            return;
        _editingAgent.Model = model;
        SaveAgentList();
        AgentCommandText.Text = LaunchDescription(_editingAgent);
        UpdateAgentApiSection(_editingAgent); // a deepseek/ model brings the offer to link DeepSeek's key
    }

    private static string LaunchDescription(AgentDefinition definition)
    {
        var model = ((definition.Catalog ?? definition.Id).Equals("aider", StringComparison.OrdinalIgnoreCase)
                     || KnownAgents.CommandName(definition.Command) == "aider") && !string.IsNullOrWhiteSpace(definition.Model)
            ? $" --model {definition.Model.Trim()}" : "";
        return $"Runs {definition.Command}{(string.IsNullOrWhiteSpace(definition.Arguments) ? "" : " " + definition.Arguments)}{model} in a terminal.";
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
        SelectTag(TagAgentPrefix + copy.Id);
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
        var index = _settings.Agents.IndexOf(removed);
        _settings.Agents.Remove(removed);
        if (HostedAgents.IsHosted(removed))
            HostedAgents.Stop(removed.Id);
        SaveAgentList();
        // Prefer the neighbour that took its place, else Add agents.
        var next = index >= 0 && index < _settings.Agents.Count ? _settings.Agents[index]
            : index > 0 ? _settings.Agents[index - 1] : null;
        SelectTag(next is null ? TagAddAgents : TagAgentPrefix + next.Id);
    }

    private void ScanAgain_Click(object sender, RoutedEventArgs e) => ShowAddAgents();

    /// <summary>The Add agents page: a scan for known agents not in the list, and a command of your own.</summary>
    private async void ShowAddAgents()
    {
        _editingAgent = null;
        _editingApi = null;
        _selectedTag = TagAddAgents;
        HideAllPanes();
        AddAgentsPage.Visibility = Visibility.Visible;
        SettingsTitle.Text = "Add agents";
        AgentListNote.Visibility = _settingsError is not null ? Visibility.Visible : Visibility.Collapsed;
        AgentListNote.Text = _settingsError is not null
            ? "agents.json couldn't be read, so your agents can't be changed here. Fix it with the pencil button, or delete it to start again."
            : "";
        CustomNote.Visibility = Visibility.Collapsed;
        FoundAgentsList.Children.Clear();
        MissingAgentsList.Children.Clear();
        MissingAgentsExpander.Visibility = Visibility.Collapsed;
        MissingAgentsExpander.IsExpanded = false;
        AddFoundButton.Visibility = Visibility.Collapsed;
        FoundAgentsNote.Text = _settingsError is not null ? "" : "Looking for agents on this PC…";
        SyncAgentBackdrop();
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (_settingsError is not null)
            return;

        var agents = await FindNewAgentsAsync();
        // Left this page (or closed the window) while the scan ran.
        if (AddAgentsPage.Visibility != Visibility.Visible || !AppWindow.IsVisible)
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

    /// <summary>Records agents the user turned down, so the flyout doesn't offer them again.</summary>
    private void DismissOffers(IEnumerable<AgentDefinition> agents)
    {
        var ids = agents.Select(a => a.Catalog ?? a.Id).ToList();
        if (ids.Count == 0)
            return;
        UpdateState(state =>
        {
            foreach (var id in ids)
                if (!state.DismissedAgentOffers.Contains(id, StringComparer.OrdinalIgnoreCase))
                    state.DismissedAgentOffers.Add(id);
        });
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

    /// <summary>
    /// Adding agents is done: from the welcome, back to the dock (the window closes); otherwise
    /// select the last agent in the list (the one just added, or the previous last).
    /// </summary>
    private void FinishAdding()
    {
        if (_addAgentsFromSetup)
        {
            _addAgentsFromSetup = false;
            HideSettings();
            return;
        }
        var last = _settings.Agents.LastOrDefault();
        SelectTag(last is null ? TagAddAgents : TagAgentPrefix + last.Id);
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
        FinishAdding();
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
        FinishAdding();
    }

    /// <summary>The Claude account whose usage setting the agent page shows.</summary>
    private UsageAccount? _settingsAccount;
    private AgentDefinition? _aiderModelsFor;

    /// <summary>
    /// The agent-specific panel under an agent's page, for its account: Claude's usage source is per
    /// account, and doesn't apply at all to an agent using an API key or another endpoint. Run again
    /// when its environment variables change, since they decide the account.
    /// </summary>
    private void UpdateAgentPanel(AgentDefinition definition)
    {
        // Claude desktop is Claude Code's default account (UsageAccount.For), so it gets Claude's choice too.
        var account = UsageAccount.For(definition);
        // Linked to an Anthropic key, Claude Code runs on that key, not its plan.
        var onKey = ApiUsage.AccountFor(definition, _settings.ApiAccounts) is { } key
                    && ApiUsage.KeyVariableFor(definition, key.Provider) is not null && ApiUsage.LeavesPlan(definition, key.Provider);
        var panel = KnownAgents.IsCursor(definition) ? CursorSettings
            : KnownAgents.IsKiro(definition) ? KiroSettings
            : KnownAgents.IsGitHubCopilot(definition) ? CopilotSettings
            : account is { UsesOtherProvider: true } || onKey ? null
            : account is { Agent: "claude" } ? ClaudeSettings
            : definition.Kind == AgentKind.Cli ? AgentSettingsPanelFor(definition)
            : null;
        foreach (var other in AgentSettingsPanels)
            other.Visibility = other == panel ? Visibility.Visible : Visibility.Collapsed;
        UpdateAgentApiSection(definition);

        if (panel == CopilotSettings)
        {
            // The desktop app opens in a folder only through Copilot CLI, so say whether that's there.
            var desktop = KnownAgents.OpensFolderThroughCli(definition);
            CopilotFolderSection.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
            if (desktop)
                CopilotFolderNote.Text = KnownAgents.CanOpenFolder(definition)
                    ? "GitHub Copilot opens a new session in the working folder, through GitHub Copilot CLI's copilot app command."
                    : "Opening in the working folder needs GitHub Copilot CLI (the copilot command), which isn't installed, so GitHub Copilot opens without a folder.";
        }

        if (panel == AiderSettings)
        {
            _settingsToggleReady = false;
            AiderModelBox.Text = definition.Model ?? "";
            _settingsToggleReady = true;
            if (!ReferenceEquals(_aiderModelsFor, definition))
            {
                _aiderModelsFor = definition;
                _aiderModels.Clear();
                AiderModelResults.Visibility = Visibility.Collapsed;
                AiderModelSearchNote.Text = "";
                AiderModelQueryBox.Text = "";
            }
        }

        _settingsAccount = account is { Agent: "claude", UsesOtherProvider: false } && !onKey ? account : null;
        if (_settingsAccount is null)
            return;
        _settingsToggleReady = false;
        ClaudeUsageSourceButtons.SelectedIndex = SourceFor(_settingsAccount) == ClaudeUsageSource.Api ? 1 : 0;
        _settingsToggleReady = true;
        ClaudeUsageIntro.Text = _settingsAccount.IsDefault
            ? "How Agent Dock finds out how much of your plan's session and weekly limits you've used, for the drawer beside Claude Code."
            : $"How Agent Dock finds out how much of this account's session and weekly limits you've used (the sign-in in {_settingsAccount.ConfigDir}).";
    }

    private async void FindAiderModels_Click(object sender, RoutedEventArgs e) => await FindAiderModelsAsync();

    private async void AiderModelQueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        await FindAiderModelsAsync();
        e.Handled = true;
    }

    private async Task FindAiderModelsAsync()
    {
        if (_editingAgent is not { } agent)
            return;
        var search = AiderModelQueryBox.Text.Trim();
        if (search.Length == 0)
        {
            AiderModelSearchNote.Text = "Enter part of a provider or model name first.";
            AiderModelQueryBox.Focus(FocusState.Programmatic);
            return;
        }

        FindAiderModelsButton.IsEnabled = false;
        AiderModelProgress.Visibility = Visibility.Visible;
        AiderModelProgress.IsActive = true;
        AiderModelSearchNote.Text = "Searching Aider's model catalogue…";
        _aiderModels.Clear();
        AiderModelResults.Visibility = Visibility.Collapsed;
        var result = await AiderModels.FindAsync(agent, search);
        AiderModelProgress.IsActive = false;
        AiderModelProgress.Visibility = Visibility.Collapsed;
        FindAiderModelsButton.IsEnabled = true;
        if (!ReferenceEquals(_editingAgent, agent) || AiderSettings.Visibility != Visibility.Visible)
            return;

        foreach (var model in result.Models)
            _aiderModels.Add(model);
        AiderModelResults.Visibility = _aiderModels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiderModelSearchNote.Text = result.Message ?? $"{_aiderModels.Count} model{(_aiderModels.Count == 1 ? "" : "s")} found. Select one to use its name.";
    }

    private void AiderModelResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiderModelResults.SelectedItem is not string model)
            return;
        AiderModelBox.Text = model;
        CommitAiderModel();
    }

    private void ClaudeUsageSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var source = ClaudeUsageSourceButtons.SelectedIndex == 1 ? ClaudeUsageSource.Api : ClaudeUsageSource.StatusLine;
        if (!_settingsToggleReady || ClaudeUsageSourceButtons.SelectedIndex < 0 || _settingsAccount is not { } account
            || SourceFor(account) == source)
            return;
        // The flyout drops that account's last answer, so its drawer follows the new choice at once.
        UpdateState(state => SetSource(state, account, source), SettingsChange.State | SettingsChange.ClaudeUsageSource);
    }

    /// <summary>Where this Claude account's usage comes from (the choice on the agent's page).</summary>
    private ClaudeUsageSource SourceFor(UsageAccount account)
    {
        if (account.IsDefault)
            return _state.ClaudeUsageSource;
        foreach (var (dir, source) in _state.ClaudeUsageSources)
            if (string.Equals(dir, account.ConfigDir, StringComparison.OrdinalIgnoreCase))
                return source;
        return ClaudeUsageSource.StatusLine;
    }

    private static void SetSource(DockState state, UsageAccount account, ClaudeUsageSource source)
    {
        if (account.IsDefault)
        {
            state.ClaudeUsageSource = source;
            return;
        }
        foreach (var dir in state.ClaudeUsageSources.Keys.Where(k => string.Equals(k, account.ConfigDir, StringComparison.OrdinalIgnoreCase)).ToList())
            state.ClaudeUsageSources.Remove(dir);
        state.ClaudeUsageSources[account.ConfigDir] = source;
    }

    /// <summary>
    /// Sets the general settings' controls from the loaded settings and state. Called each time the
    /// window is shown, before the page it opens at.
    /// </summary>
    private void FillSettingsPage()
    {
        _settingsToggleReady = false;
        HoverBackgroundsSwitch.IsOn = _state.ShowHoverBackgrounds;
        CloseTerminalOnExitSwitch.IsOn = _settings.CloseTerminalWhenAgentExits;
        RecentFoldersSwitch.IsOn = _state.RememberRecentLaunches;
        UsageSummarySwitch.IsOn = _state.ShowUsageSummary;
        PreferPlansSwitch.IsOn = _state.PreferPlansOverApi;
        HideUnavailableSwitch.IsOn = _settings.HideUnavailable;
        StartWithWindowsSwitch.IsOn = _settings.StartWithWindows;
        TerminalBox.SelectedIndex = Math.Max(0, Array.FindIndex(TerminalChoices, c => c.Id.Equals(_settings.Terminal, StringComparison.OrdinalIgnoreCase)));
        ShellBox.SelectedIndex = Math.Max(0, Array.FindIndex(ShellChoices, c => c.Id.Equals(_settings.Shell, StringComparison.OrdinalIgnoreCase)));
        // These live in agents.json, which isn't written while it can't be read.
        HideUnavailableSwitch.IsEnabled = StartWithWindowsSwitch.IsEnabled = CloseTerminalOnExitSwitch.IsEnabled
            = TerminalBox.IsEnabled = ShellBox.IsEnabled = _settingsError is null;
        HideUnavailableNote.Text = _settingsError is null
            ? "Hide agents in your list that aren't installed any more, instead of showing them dimmed."
            : "Fix agents.json to change this here.";
        ClearRecentsButton.IsEnabled = RecentLaunches.Any(_state);
        BuildTaskbarIconPicker();
        if (_taskbarIconPending)
            ShowTaskbarIconBar(null);
        else
            TaskbarIconRestartBar.IsOpen = false;
        _settingsToggleReady = true;
    }

    private async void HoverBackgroundsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _waifuPromptOpen || _state.ShowHoverBackgrounds == HoverBackgroundsSwitch.IsOn)
            return;
        var on = HoverBackgroundsSwitch.IsOn;
        // Turning them on asks a second time (SettingsWindow.Waifu.cs); backing out puts the switch back.
        if (on && !await ConfirmWaifusAsync())
        {
            _settingsToggleReady = false;
            HoverBackgroundsSwitch.IsOn = false;
            _settingsToggleReady = true;
            return;
        }
        UpdateState(state => state.ShowHoverBackgrounds = on, SettingsChange.State | SettingsChange.Waifu);
        // Terminal's pictures follow now; icon/colour profiles stay either way.
        TerminalProfiles.Prepare(_settings.Agents, includeBackgrounds: on);
        TaskbarIconFollowWaifus();
        SyncAgentBackdrop(); // the agent page's own picture, if one is open
    }

    private void CloseTerminalOnExitSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _settings.CloseTerminalWhenAgentExits == CloseTerminalOnExitSwitch.IsOn)
            return;
        _settings.CloseTerminalWhenAgentExits = CloseTerminalOnExitSwitch.IsOn;
        if (SaveAgentList())
            return;
        _settings.CloseTerminalWhenAgentExits = !CloseTerminalOnExitSwitch.IsOn;
        _settingsToggleReady = false;
        CloseTerminalOnExitSwitch.IsOn = _settings.CloseTerminalWhenAgentExits;
        _settingsToggleReady = true;
    }

    // ---------- Launching (agents.json's terminal, shell and startWithWindows) ----------

    private static readonly (string Id, string Name)[] TerminalChoices =
        [("auto", "Windows Terminal if installed"), ("wt", "Windows Terminal"), ("console", "Console window")];

    private static readonly (string Id, string Name)[] ShellChoices =
        [("auto", "PowerShell 7 if installed"), ("pwsh", "PowerShell 7"), ("powershell", "Windows PowerShell"), ("cmd", "Command Prompt")];

    private void FillLaunchChoices()
    {
        TerminalBox.ItemsSource = TerminalChoices.Select(c => c.Name).ToList();
        ShellBox.ItemsSource = ShellChoices.Select(c => c.Name).ToList();
    }

    private void TerminalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsToggleReady || TerminalBox.SelectedIndex < 0
            || TerminalChoices[TerminalBox.SelectedIndex].Id.Equals(_settings.Terminal, StringComparison.OrdinalIgnoreCase))
            return;
        _settings.Terminal = TerminalChoices[TerminalBox.SelectedIndex].Id;
        SaveAgentList();
    }

    private void ShellBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsToggleReady || ShellBox.SelectedIndex < 0
            || ShellChoices[ShellBox.SelectedIndex].Id.Equals(_settings.Shell, StringComparison.OrdinalIgnoreCase))
            return;
        _settings.Shell = ShellChoices[ShellBox.SelectedIndex].Id;
        SaveAgentList();
    }

    private void StartWithWindowsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _settings.StartWithWindows == StartWithWindowsSwitch.IsOn)
            return;
        _settings.StartWithWindows = StartWithWindowsSwitch.IsOn;
        if (SaveAgentList())
        {
            Autostart.Apply(_settings.StartWithWindows); // the flyout does too when it opens; now, in case it doesn't
            return;
        }
        _settings.StartWithWindows = !StartWithWindowsSwitch.IsOn;
        _settingsToggleReady = false;
        StartWithWindowsSwitch.IsOn = _settings.StartWithWindows;
        _settingsToggleReady = true;
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
        var on = RecentFoldersSwitch.IsOn;
        UpdateState(state => state.RememberRecentLaunches = on, SettingsChange.State | SettingsChange.Recents);
    }

    private void UsageSummarySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _state.ShowUsageSummary == UsageSummarySwitch.IsOn)
            return;
        var on = UsageSummarySwitch.IsOn;
        UpdateState(state => state.ShowUsageSummary = on);
    }

    private void PreferPlansSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsToggleReady || _state.PreferPlansOverApi == PreferPlansSwitch.IsOn)
            return;
        var on = PreferPlansSwitch.IsOn;
        UpdateState(state => state.PreferPlansOverApi = on);
    }

    // ---------- Taskbar icon ----------

    private readonly Dictionary<string, BitmapImage> _taskbarIconPreviews = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>False while the picker is being filled, so setting its selection doesn't count as a choice.</summary>
    private bool _taskbarIconPickerReady;
    /// <summary>An icon was set but Explorer hasn't restarted since, so the restart offer stays.</summary>
    private bool _taskbarIconPending;

    private string CurrentTaskbarIcon => _state.TaskbarIcon ?? TaskbarIcon.DefaultId;

    /// <summary>Fills the picker for the current waifu setting, with the current icon selected.</summary>
    private void BuildTaskbarIconPicker()
    {
        _taskbarIconPickerReady = false;
        var choices = TaskbarIcon.Choices(_state.ShowHoverBackgrounds)
            .Select(c => new TaskbarIconChoice(c.Id, c.Name, TaskbarIconPreview(c.Id)))
            .ToList();
        TaskbarIconPicker.ItemsSource = choices;
        TaskbarIconPicker.SelectedItem = choices.FirstOrDefault(c => c.Id.Equals(CurrentTaskbarIcon, StringComparison.OrdinalIgnoreCase));
        var pinned = Environment.ProcessPath is { } exe && TaskbarPins.IsPinned(exe);
        TaskbarIconPicker.IsEnabled = pinned;
        TaskbarIconNote.Text = pinned
            ? "The picture on Agent Dock's taskbar button."
            : "Pin Agent Dock to the taskbar to choose its icon.";
        _taskbarIconPickerReady = true;
    }

    /// <summary>The 256 px frame, decoded small; kept for the life of the process like the agent icons.</summary>
    private BitmapImage? TaskbarIconPreview(string id)
    {
        if (_taskbarIconPreviews.TryGetValue(id, out var cached))
            return cached;
        if (TaskbarIcon.Preview(id) is not { } png)
            return null;
        var image = new BitmapImage { DecodePixelWidth = 96 };
        _taskbarIconPreviews[id] = image;
        _ = LoadTaskbarIconPreviewAsync(image, png);
        return image;
    }

    private static async Task LoadTaskbarIconPreviewAsync(BitmapImage image, byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png).AsRandomAccessStream();
            await image.SetSourceAsync(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A blank tile; the icon itself still works.
        }
    }

    private void TaskbarIconPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_taskbarIconPickerReady || TaskbarIconPicker.SelectedItem is not TaskbarIconChoice choice
            || choice.Id.Equals(CurrentTaskbarIcon, StringComparison.OrdinalIgnoreCase))
            return;
        // Chosen by hand, so there's no earlier waifu icon to bring back any more.
        if (!SetTaskbarIcon(choice.Id, forgetWaifuIcon: true))
            BuildTaskbarIconPicker(); // back to the icon that's really there
    }

    /// <summary>
    /// Waifu backgrounds was switched: off takes a waifu icon off the taskbar (Agent Dock's own icon
    /// instead, remembering the waifu one), and on brings that one back if nothing was chosen since.
    /// </summary>
    private void TaskbarIconFollowWaifus()
    {
        if (!_state.ShowHoverBackgrounds && TaskbarIcon.IsWaifu(_state.TaskbarIcon))
            SetTaskbarIcon(TaskbarIcon.DefaultId, rememberWaifuIcon: _state.TaskbarIcon);
        else if (_state.ShowHoverBackgrounds && _state.TaskbarIcon is null && _state.TaskbarWaifuIcon is { } earlier)
            SetTaskbarIcon(earlier, forgetWaifuIcon: true);
        BuildTaskbarIconPicker();
    }

    /// <summary>
    /// Sets the pinned icon and saves the choice. On failure, says why and changes nothing.
    /// <paramref name="forgetWaifuIcon"/> drops the waifu icon kept for when waifus come back;
    /// <paramref name="rememberWaifuIcon"/> keeps one.
    /// </summary>
    private bool SetTaskbarIcon(string id, bool forgetWaifuIcon = false, string? rememberWaifuIcon = null)
    {
        if (TaskbarIcon.Apply(id) is { } error)
        {
            ShowTaskbarIconBar(error);
            return false;
        }
        // The flyout redoes the jump list, whose Quit entry uses this icon.
        UpdateState(state =>
        {
            state.TaskbarIcon = id == TaskbarIcon.DefaultId ? null : id;
            if (forgetWaifuIcon)
                state.TaskbarWaifuIcon = null;
            else if (rememberWaifuIcon is not null)
                state.TaskbarWaifuIcon = rememberWaifuIcon;
        }, SettingsChange.State | SettingsChange.TaskbarIcon);
        _taskbarIconPending = true;
        ShowTaskbarIconBar(null);
        return true;
    }

    /// <summary>The restart offer, or (with <paramref name="error"/>) why the icon couldn't be set.</summary>
    private void ShowTaskbarIconBar(string? error)
    {
        TaskbarIconRestartBar.Severity = error is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        TaskbarIconRestartBar.Title = error is null ? "Restart File Explorer to see it" : "";
        TaskbarIconRestartBar.Message = error
            ?? "Windows shows the new icon after File Explorer restarts, or the next time you sign in. Restarting closes any open File Explorer windows.";
        TaskbarIconRestartButton.Visibility = error is null ? Visibility.Visible : Visibility.Collapsed;
        TaskbarIconRestartBar.IsOpen = true;
    }

    private async void TaskbarIconRestart_Click(object sender, RoutedEventArgs e)
    {
        TaskbarIconRestartButton.IsEnabled = false;
        var cameBack = await Task.Run(TaskbarIcon.RestartExplorer);
        TaskbarIconRestartButton.IsEnabled = true;
        _taskbarIconPending = false;
        TaskbarIconRestartBar.IsOpen = false;
        if (!cameBack)
            ShowTaskbarIconBar("File Explorer didn't start again by itself, so Agent Dock started it.");
    }

    private void ClearRecents_Click(object sender, RoutedEventArgs e)
    {
        ClearRecentsFlyout.Hide();
        UpdateState(state => RecentLaunches.Clear(state), SettingsChange.State | SettingsChange.Recents);
        ClearRecentsButton.IsEnabled = false;
        // The button just went away under the focus; keep it on the page.
        RecentFoldersSwitch.Focus(FocusState.Programmatic);
    }
}
