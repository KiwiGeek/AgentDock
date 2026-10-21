using System.Globalization;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentDock;

/// <summary>
/// Settings → Accounts (owner's decision, 2026-10-20, replacing a left-list section of API keys):
/// every plan sign-in and API key in one page, each key's own page with the agents it's handed to,
/// and the Account choice on an agent's page. The flyout has its own balance checks for the drawer
/// and the overview (FlyoutWindow.ApiAccounts.cs); both save the same balances.
/// </summary>
public sealed partial class SettingsWindow
{
    private sealed class ApiCheck
    {
        public bool Running;
        public DateTimeOffset At;
        public string? Problem;
    }

    private readonly Dictionary<string, ApiCheck> _apiChecks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The status line under the key on its own page.</summary>
    private readonly Dictionary<string, TextBlock> _apiStatusTexts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The status line of each key's row on the Accounts page.</summary>
    private readonly Dictionary<string, TextBlock> _apiRowStatusTexts = new(StringComparer.OrdinalIgnoreCase);
    private TextBox? _newApiKeyBox;
    /// <summary>The agents list on the key page that's showing, rebuilt when a switch changes.</summary>
    private StackPanel? _apiAgentsHost;
    /// <summary>False while the agent page's account choice is being filled, so that isn't a change.</summary>
    private bool _agentApiReady;
    /// <summary>The accounts behind the agent page's choices, after "None".</summary>
    private List<ApiAccount> _agentApiChoices = [];

    private ApiAccount? ApiAccountFor(AgentDefinition definition) => ApiUsage.AccountFor(definition, _settings.ApiAccounts);

    private ApiBalanceState? BalanceOf(ApiAccount account) => _state.ApiBalances.GetValueOrDefault(account.Id);

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock Note(string text = "") => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = ThemeBrush("TextFillColorSecondaryBrush")
    };

    private static TextBlock SectionHeader(string text, double top = 16) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        Margin = new Thickness(0, top, 0, 8)
    };

    /// <summary>The agent's icon (compiled in or from the cache), or its glyph.</summary>
    private UIElement AgentIcon(AgentDefinition definition, double size)
    {
        var resolved = _agents.FirstOrDefault(a => a.Preset is null && ReferenceEquals(a.Definition, definition));
        return resolved?.Icon is { } image
            ? new Image { Source = image, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = resolved?.Glyph ?? "", FontSize = size, Width = size, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Show a provider's mark where available; use a key glyph for others.</summary>
    private UIElement ApiIcon(ApiAccount account)
    {
        var image = _icons.Get(ApiUsage.ProviderIcon(account.Provider));
        return image is not null
            ? new Image { Source = image, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = "\uE8D7", FontSize = 20, Width = 24, VerticalAlignment = VerticalAlignment.Center };
    }

    // ---------- Checks ----------

    /// <summary>Checks the balance unless a check is running or the last is fresh (<paramref name="force"/>: Check now).</summary>
    private void StartApiCheck(ApiAccount account, bool force = false)
    {
        if (!ApiUsage.ChecksBalance(account.Provider))
            return;
        if (!_apiChecks.TryGetValue(account.Id, out var check))
            _apiChecks[account.Id] = check = new ApiCheck();
        if (check.Running || (!force && check.At != default && DateTimeOffset.UtcNow - check.At <= ApiUsage.MinInterval))
            return;
        _ = RunApiCheckAsync(account, check);
    }

    private async Task RunApiCheckAsync(ApiAccount account, ApiCheck check)
    {
        check.Running = true;
        UpdateApiAccountStatus(account);
        var result = await ApiUsage.FetchAsync(account, BalanceOf(account)?.Since ?? 0); // resumes on the UI thread; never throws
        check.Running = false;
        // Removed, or its key or service changed, while this ran.
        if (!_apiChecks.TryGetValue(account.Id, out var current) || current != check || !_settings.ApiAccounts.Contains(account))
            return;
        var now = DateTimeOffset.UtcNow;
        check.At = now;
        check.Problem = result.Problem;
        if (result.Balance is not null || result.Spent is not null)
            UpdateState(state =>
            {
                if (!state.ApiBalances.TryGetValue(account.Id, out var balance))
                    state.ApiBalances[account.Id] = balance = new ApiBalanceState();
                ApiUsage.Track(balance, result, now);
            });
        UpdateApiAccountStatus(account);
    }

    // ---------- The Accounts page ----------

    /// <summary>A sign-in some of the user's agents share (Claude Code and Claude desktop, say), for the Plans list.</summary>
    private sealed record PlanGroup(string Key, string Title, string Detail, List<AgentDefinition> Agents);

    private void AccountsCrumb_Click(object sender, RoutedEventArgs e)
    {
        CommitEditors();
        SelectTag(TagAccounts);
    }

    private void AddApiKey_Click(object sender, RoutedEventArgs e) => AddApiAccount();

    /// <summary>Plans (the sign-ins Agent Dock reads usage through) and API keys, each with the agents that use it.</summary>
    private void ShowAccounts()
    {
        _editingAgent = null;
        _editingApi = null;
        _selectedTag = TagAccounts;
        HideAllPanes();
        AccountsPage.Visibility = Visibility.Visible;
        SettingsTitle.Text = "Accounts";
        AddApiKeyButton.IsEnabled = _settingsError is null;
        FillPlanRows();
        FillApiKeyRows();
        _ = LoadAgentIconsAsync();
        foreach (var account in _settings.ApiAccounts)
            StartApiCheck(account);
        SyncAgentBackdrop();
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>
    /// The user's agents grouped by the sign-in their usage comes from, the way the flyout's drawers
    /// find it: Claude and Codex per config folder (<see cref="UsageAccount"/>), and one each for
    /// Grok Build, Cursor, Kiro and GitHub. Agents on an API key or another endpoint have no plan here.
    /// </summary>
    private List<PlanGroup> PlanGroups()
    {
        var groups = new List<PlanGroup>();
        PlanGroup Group(string key, string title, string detail)
        {
            var group = groups.FirstOrDefault(g => g.Key == key);
            if (group is null)
                groups.Add(group = new PlanGroup(key, title, detail, []));
            return group;
        }

        foreach (var definition in _settings.Agents)
        {
            if (ApiAccountFor(definition) is { } key && ApiUsage.KeyVariableFor(definition, key.Provider) is not null
                && ApiUsage.LeavesPlan(definition, key.Provider))
                continue; // Claude Code on an Anthropic key
            PlanGroup? group = null;
            if (IsCursor(definition))
                group = Group("cursor", "Cursor", "Cursor CLI's sign-in, for both the CLI and the desktop app");
            else if (IsKiro(definition))
                group = Group("kiro", "Kiro", "Kiro CLI's sign-in (kiro-cli login)");
            else if (IsGitHubCopilot(definition))
                group = Group("copilot", "GitHub", "GitHub CLI's sign-in (gh), for Copilot CLI and the desktop app");
            else if (UsageAccount.For(definition) is { } account)
            {
                if (account.UsesOtherProvider)
                    continue;
                var folder = Path.GetFileName(account.ConfigDir);
                group = account.Agent == "claude"
                    ? Group(account.Key, account.IsDefault ? "Claude" : $"Claude · {folder}",
                        (account.IsDefault ? "Claude Code's sign-in" : $"Signed in under {account.ConfigDir}")
                        + (SourceFor(account) == ClaudeUsageSource.Api ? " · usage asked of Claude directly" : " · usage from the status line"))
                    : Group(account.Key, account.IsDefault ? "ChatGPT" : $"ChatGPT · {folder}",
                        account.IsDefault ? "Codex's sign-in" : $"Signed in under {account.ConfigDir}");
            }
            else if (definition.Kind == AgentKind.Cli && CommandName(definition.Command) == "grok")
                group = Group("grok", "Grok", "Grok Build's sign-in");
            group?.Agents.Add(definition);
        }
        return groups;
    }

    private void FillPlanRows()
    {
        PlansHost.Children.Clear();
        var groups = PlanGroups();
        PlansNote.Text = groups.Count > 0
            ? "Usage comes from each agent's own sign-in, so nothing is stored here. Choose one for its usage settings."
            : "None of your agents has a plan whose usage Agent Dock reads (Claude Code, Codex, Grok Build, Cursor, Kiro or GitHub Copilot).";
        foreach (var group in groups)
        {
            var first = group.Agents[0];
            var row = AccountRow(AgentIcon(first, 24), group.Title, group.Detail,
                "Used by " + string.Join(", ", group.Agents.Select(a => a.Name)), null);
            AutomationProperties.SetName(row, $"{group.Title}, used by {string.Join(", ", group.Agents.Select(a => a.Name))}");
            ToolTipService.SetToolTip(row, $"Open {first.Name}'s settings");
            row.Click += (_, _) => SelectTag(TagAgentPrefix + first.Id);
            PlansHost.Children.Add(row);
        }
    }

    private void FillApiKeyRows()
    {
        ApiKeysHost.Children.Clear();
        _apiRowStatusTexts.Clear();
        ApiKeysNote.Visibility = _settings.ApiAccounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var account in _settings.ApiAccounts)
        {
            var users = _settings.Agents.Where(a => string.Equals(a.ApiAccount, account.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var status = Note();
            _apiRowStatusTexts[account.Id] = status;
            var icon = ApiIcon(account);
            var row = AccountRow(icon, account.Name, $"{ApiUsage.ProviderName(account.Provider)} · {KeySummary(account)}",
                users.Count > 0 ? "Used by " + string.Join(", ", users.Select(a => a.Name)) : "Not linked to an agent yet", status);
            AutomationProperties.SetName(row, $"{account.Name}, {ApiUsage.ProviderName(account.Provider)} API key");
            row.Click += (_, _) => SelectTag(TagApiPrefix + account.Id);
            ApiKeysHost.Children.Add(row);
            UpdateApiAccountStatus(account);
        }
    }

    /// <summary>"sk-ant-…7Qx", or where it's read from, or that there's none yet. Shown short in the list only.</summary>
    private static string KeySummary(ApiAccount account)
    {
        if (account.ApiKey?.Trim() is not { Length: > 0 } key)
            return account.Provider.Equals("hyper", StringComparison.OrdinalIgnoreCase) ? "Crush sign-in" : "no key yet";
        if (key.StartsWith('%') && key.EndsWith('%'))
            return $"read from {key}";
        return key.Length > 12 ? $"{key[..Math.Min(7, key.Length - 4)]}…{key[^3..]}" : key;
    }

    /// <summary>A card-like row: icon, title, detail, who uses it, a status line, and a chevron.</summary>
    private static Button AccountRow(UIElement icon, string title, string detail, string usedBy, TextBlock? status)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Note(detail));
        text.Children.Add(Note(usedBy));
        if (status is not null)
            text.Children.Add(status);
        var chevron = new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(chevron, 2);
        return new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 12, 16, 12),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Content = new Grid
            {
                ColumnSpacing = 16,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(24) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children = { icon, text, chevron }
            }
        };
    }

    // ---------- One API key's page (opened from Accounts) ----------

    /// <summary>Shows this account's page in the right pane, with Accounts in front of its title.</summary>
    private void ShowApiAccount(ApiAccount account)
    {
        _editingApi = account;
        _selectedTag = TagApiPrefix + account.Id;
        HideAllPanes();
        ApiPage.Visibility = Visibility.Visible;
        AccountsCrumb.Visibility = AccountsCrumbChevron.Visibility = Visibility.Visible;
        SettingsTitle.Text = account.Name;
        ApiAccountHost.Children.Clear();
        _apiStatusTexts.Clear();
        _newApiKeyBox = null;
        ApiAccountHost.Children.Add(ApiAccountEditor(account));
        StartApiCheck(account);
        SyncAgentBackdrop();
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Adds an account and opens its page with the key box focused.</summary>
    private void AddApiAccount()
    {
        if (_settingsError is not null)
        {
            SelectTag(TagGeneral);
            return;
        }
        var provider = ApiUsage.Providers[0];
        var account = new ApiAccount { Id = ApiUsage.NewId(provider.Id, _settings.ApiAccounts), Name = provider.Name, Provider = provider.Id };
        _settings.ApiAccounts.Add(account);
        if (!SaveAgentList(SettingsChange.ApiAccounts))
            return;
        SelectTag(TagApiPrefix + account.Id);
        DispatcherQueue.TryEnqueue(() => _newApiKeyBox?.Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// One account: the key (name, service, the key shown as it is, Anthropic's Admin key), the
    /// agents it's handed to, its balance when the service has one, and Remove.
    /// </summary>
    private StackPanel ApiAccountEditor(ApiAccount account)
    {
        var panel = new StackPanel { Spacing = 8 };
        var spend = ApiUsage.ReportsSpend(account.Provider);
        var variable = ApiUsage.KeyVariable(account.Provider);

        panel.Children.Add(SectionHeader("Key", 0));
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox { Header = "Name", Text = account.Name };
        name.LostFocus += (_, _) =>
        {
            var text = name.Text.Trim();
            if (text.Length == 0)
                name.Text = account.Name; // a name is required; put it back
            else if (text != account.Name)
            {
                account.Name = text;
                SettingsTitle.Text = text;
                SaveAgentList(SettingsChange.ApiAccounts);
            }
        };
        var service = new ComboBox
        {
            Header = "Service", MinWidth = 160,
            ItemsSource = ApiUsage.Providers.Select(p => p.Name).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(ApiUsage.Providers, p => p.Id.Equals(account.Provider, StringComparison.OrdinalIgnoreCase)))
        };
        AutomationProperties.SetName(service, $"{account.Name} service");
        service.SelectionChanged += (_, _) =>
        {
            var provider = ApiUsage.Providers[Math.Max(0, service.SelectedIndex)].Id;
            if (provider.Equals(account.Provider, StringComparison.OrdinalIgnoreCase))
                return;
            if (account.Name.Equals(ApiUsage.ProviderName(account.Provider), StringComparison.Ordinal))
                account.Name = ApiUsage.ProviderName(provider);
            account.Provider = provider;
            ForgetBalance(account); // another service's balance has nothing to do with this one's
            SaveAgentList(SettingsChange.ApiAccounts);
            // Its labels, agents and balance differ by service; rebuilt after this event, not inside it.
            DispatcherQueue.TryEnqueue(() => ShowApiAccount(account));
        };
        top.Children.Add(name);
        Grid.SetColumn(service, 1);
        top.Children.Add(service);
        panel.Children.Add(top);

        // The key agents use; for DeepSeek it also reads the balance.
        var key = KeyBox("API key", $"{account.Name} API key", $"Or %{variable}% to read it from Windows",
            () => account.ApiKey, value => account.ApiKey = value, rechecks: !spend);
        _newApiKeyBox = key;
        panel.Children.Add(key);
        if (account.Provider.Equals("hyper", StringComparison.OrdinalIgnoreCase))
            panel.Children.Add(Note("Leave the key blank to read Crush's current Hyper sign-in for balance checks. Agent Dock never refreshes or saves its token. An expired sign-in needs Crush to renew it."));
        // Anthropic reads spending only with an Admin key, which can't run models: a second key.
        if (spend)
        {
            panel.Children.Add(KeyBox("Admin API key (optional)", $"{account.Name} Admin API key", "sk-ant-admin…",
                () => account.AdminKey, value => account.AdminKey = value, rechecks: true));
            panel.Children.Add(Note("Reads this month's spending. Only an organization's Console can create one, and it can't run models."));
        }

        TextBox KeyBox(string header, string automationName, string placeholder, Func<string?> get, Action<string?> set, bool rechecks)
        {
            var box = new TextBox { Header = header, Text = get() ?? "", PlaceholderText = placeholder };
            AutomationProperties.SetName(box, automationName);
            box.LostFocus += (_, _) =>
            {
                var text = box.Text.Trim() is { Length: > 0 } value ? value : null;
                if (text == get())
                    return;
                set(text);
                SaveAgentList(SettingsChange.ApiAccounts);
                FillApiAgentRows(account); // "add a key" notes there change
                if (!rechecks)
                    return;
                _apiChecks.Remove(account.Id);
                StartApiCheck(account, force: true);
            };
            return box;
        }

        panel.Children.Add(SectionHeader("Agents that use this key"));
        panel.Children.Add(Note(account.Provider.Equals("hyper", StringComparison.OrdinalIgnoreCase)
            ? "With a key, linked agents receive HYPER_API_KEY. With no key, Crush keeps its own sign-in and this account shows its Hyper balance."
            : "Each agent switched on here starts with this key in the variable it reads. A variable set on the agent's own page wins."));
        _apiAgentsHost = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(_apiAgentsHost);
        FillApiAgentRows(account);

        if (ApiUsage.ChecksBalance(account.Provider))
        {
            panel.Children.Add(SectionHeader("Balance"));
            var status = Note();
            _apiStatusTexts[account.Id] = status;
            panel.Children.Add(status);
            if (!account.Provider.Equals("hyper", StringComparison.OrdinalIgnoreCase))
                panel.Children.Add(BalanceRow(account, spend));
            var check = new Button { Content = "Check now" };
            check.Click += (_, _) => StartApiCheck(account, force: true);
            panel.Children.Add(check);
        }
        else
        {
            var keyOnly = Note($"Agent Dock can't read {ApiUsage.ProviderName(account.Provider)}'s balance, so this key is only handed to the agents above.");
            keyOnly.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(keyOnly);
        }

        var remove = new Button { Content = "Remove account", Margin = new Thickness(0, 16, 0, 0) };
        var confirm = new Button { Content = "Remove", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var removeFlyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12, MaxWidth = 260,
                Children = { new TextBlock { Text = $"Remove {account.Name}? Agents using it start without its key.", TextWrapping = TextWrapping.Wrap }, confirm }
            }
        };
        confirm.Click += (_, _) =>
        {
            removeFlyout.Hide();
            RemoveApiAccount(account);
        };
        remove.Flyout = removeFlyout;
        panel.Children.Add(remove);

        UpdateApiAccountStatus(account);
        return panel;
    }

    /// <summary>
    /// "I paid $20": the amount that counts as 100%, until the next top-up adds to it. A balance
    /// service needs this only to correct it; a spend service (Anthropic) needs it for a percentage at
    /// all: the Console's balance now, with spending counted from today.
    /// </summary>
    private Grid BalanceRow(ApiAccount account, bool spend)
    {
        var amountRow = new Grid { ColumnSpacing = 8 };
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var amount = new TextBox
        {
            Header = spend ? "Your balance now, from the Console" : "Balance after your last top-up",
            PlaceholderText = spend ? "Spending is counted from today" : "Worked out from the balances it sees"
        };
        AutomationProperties.SetName(amount, spend ? $"{account.Name} balance now" : $"{account.Name} balance after the last top-up");
        var set = new Button { Content = "Set", VerticalAlignment = VerticalAlignment.Bottom };
        void Apply()
        {
            if (!double.TryParse(amount.Text.Trim().TrimStart('$', '¥'), NumberStyles.Float, CultureInfo.CurrentCulture, out var full)
                && !double.TryParse(amount.Text.Trim().TrimStart('$', '¥'), NumberStyles.Float, CultureInfo.InvariantCulture, out full))
                return;
            if (full <= 0)
                return;
            amount.Text = "";
            UpdateState(current =>
            {
                if (!current.ApiBalances.TryGetValue(account.Id, out var state))
                    current.ApiBalances[account.Id] = state = new ApiBalanceState();
                if (spend)
                {
                    // From now on: today's spending is counted from here (the report is by day).
                    state.Full = full;
                    state.Balance = full;
                    state.Spent = 0;
                    state.Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                else
                    state.Full = Math.Max(full, state.Balance);
            });
            UpdateApiAccountStatus(account);
            if (spend)
                StartApiCheck(account, force: true);
        }
        set.Click += (_, _) => Apply();
        amount.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Apply();
                e.Handled = true;
            }
        };
        amountRow.Children.Add(amount);
        Grid.SetColumn(set, 1);
        amountRow.Children.Add(set);
        return amountRow;
    }

    /// <summary>
    /// Every agent in the list that can take this service's key, plus any linked to it that can't
    /// (an older link, which shows the balance only), each with a switch. One account per agent, so
    /// switching one on here moves it from another key.
    /// </summary>
    private void FillApiAgentRows(ApiAccount account)
    {
        if (_apiAgentsHost is not { } host || _editingApi != account)
            return;
        host.Children.Clear();
        var agents = _settings.Agents
            .Where(a => ApiUsage.KeyVariableFor(a, account.Provider) is not null
                        || string.Equals(a.ApiAccount, account.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (agents.Count == 0)
        {
            host.Children.Add(Note($"None of your agents can take a {ApiUsage.ProviderName(account.Provider)} key. Aider, OpenCode, DeepSeek Harness and commands of your own can take most."));
            return;
        }

        foreach (var agent in agents)
        {
            var linked = string.Equals(agent.ApiAccount, account.Id, StringComparison.OrdinalIgnoreCase);
            var variable = ApiUsage.KeyVariableFor(agent, account.Provider);
            var notes = new List<string>
            {
                variable is null ? "Can't take this key; its drawer shows the balance only."
                : agent.Env?.ContainsKey(variable) == true ? $"Its own {variable} variable is used instead."
                : $"Starts with {variable}."
            };
            if (variable is not null && ApiUsage.LeavesPlan(agent, account.Provider))
                notes.Add("This replaces its Claude plan sign-in: its usage is billed to this key.");
            if (!linked && ApiAccountFor(agent) is { } other)
                notes.Add($"Uses {other.Name} now; switching this on moves it here.");

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = agent.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(Note(string.Join(" ", notes)));
            var toggle = new ToggleSwitch { IsOn = linked, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, $"Use {account.Name} for {agent.Name}");
            toggle.Toggled += (_, _) =>
            {
                var id = toggle.IsOn ? account.Id : null;
                if (string.Equals(agent.ApiAccount, id, StringComparison.OrdinalIgnoreCase))
                    return;
                var before = agent.ApiAccount;
                agent.ApiAccount = id;
                if (!SaveAgentList(SettingsChange.ApiAccounts))
                    agent.ApiAccount = before;
                // Rebuilt after this event, not inside it.
                DispatcherQueue.TryEnqueue(() => FillApiAgentRows(account));
            };
            var icon = AgentIcon(agent, 20);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(toggle, 2);
            host.Children.Add(new Grid
            {
                ColumnSpacing = 12,
                Padding = new Thickness(0, 6, 0, 6),
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(24) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children = { icon, text, toggle }
            });
        }
    }

    /// <summary>The key page's balance line and the key's row on Accounts: its balance and when, or what's wrong.</summary>
    private void UpdateApiAccountStatus(ApiAccount account)
    {
        var state = BalanceOf(account);
        var check = _apiChecks.GetValueOrDefault(account.Id);
        var hasData = state is { UpdatedAt: > 0 };

        if (_apiStatusTexts.TryGetValue(account.Id, out var text))
        {
            var parts = new List<string>();
            if (check?.Running == true)
                parts.Add(ApiUsage.Checking);
            else if (check?.Problem is { } problem)
                parts.Add(problem);
            if (hasData)
                parts.Add((state!.Currency.Equals("HYPER", StringComparison.OrdinalIgnoreCase)
                              ? $"{ApiUsage.Summary(state)}. "
                              : ApiUsage.HasPercent(state)
                              ? $"{ApiUsage.Summary(state!)} of {ApiUsage.Money(state!.Full, state.Currency)}. "
                              : $"{ApiUsage.Summary(state!)}. Set your balance below to see a percentage. ")
                          + FlyoutWindow.UpdatedAgo(DateTimeOffset.FromUnixTimeSeconds(state!.UpdatedAt), DateTimeOffset.UtcNow));
            text.Text = parts.Count > 0 ? string.Join(" ", parts) : "Not checked yet.";
        }

        if (_apiRowStatusTexts.TryGetValue(account.Id, out var row))
            row.Text = !ApiUsage.ChecksBalance(account.Provider) ? "No balance check for this service"
                : hasData ? ApiUsage.Summary(state!)
                : check?.Running == true ? ApiUsage.Checking
                : check?.Problem ?? "Not checked yet";
    }

    private void RemoveApiAccount(ApiAccount account)
    {
        _settings.ApiAccounts.Remove(account);
        foreach (var agent in _settings.Agents.Where(a => string.Equals(a.ApiAccount, account.Id, StringComparison.OrdinalIgnoreCase)))
            agent.ApiAccount = null;
        ForgetBalance(account);
        SaveAgentList(SettingsChange.ApiAccounts);
        SelectTag(TagAccounts);
    }

    private void ForgetBalance(ApiAccount account)
    {
        _apiChecks.Remove(account.Id);
        if (_store.LoadState().ApiBalances.ContainsKey(account.Id))
            UpdateState(state => state.ApiBalances.Remove(account.Id));
    }

    // ---------- An agent's page: which account it runs on ----------

    /// <summary>
    /// The Account choice, for agents that can take a key (or are linked to one), listing the keys it
    /// can use, and an offer to use one it seems to use already (Aider's model, OpenCode's providers).
    /// </summary>
    private void UpdateAgentApiSection(AgentDefinition definition)
    {
        var linked = ApiAccountFor(definition);
        var takesKeys = ApiUsage.Providers.Any(p => ApiUsage.KeyVariableFor(definition, p.Id) is not null);
        // Claude Code has a plan: a key is an option once there is one, never a nudge to add one.
        var hasPlan = ApiUsage.Providers.Any(p => ApiUsage.LeavesPlan(definition, p.Id));
        _agentApiChoices = _settings.ApiAccounts
            .Where(a => a == linked || ApiUsage.KeyVariableFor(definition, a.Provider) is not null)
            .ToList();
        AgentApiSection.Visibility = _agentApiChoices.Count > 0 || (takesKeys && !hasPlan) ? Visibility.Visible : Visibility.Collapsed;
        AgentApiOffer.IsOpen = false;
        if (AgentApiSection.Visibility != Visibility.Visible)
            return;

        _agentApiReady = false;
        AgentApiChoices.Items.Clear();
        AgentApiChoices.Items.Add(hasPlan
            ? ChoiceContent("Its plan", "Its own sign-in, with your plan's usage.")
            : ChoiceContent("None", "No key from Agent Dock; it uses its own configuration."));
        foreach (var account in _agentApiChoices)
            AgentApiChoices.Items.Add(ChoiceContent(account.Name,
                account.Provider.Equals("hyper", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(account.ApiKey)
                    ? "Hyper · reads Crush's sign-in for balance only"
                    : ApiUsage.KeyVariableFor(definition, account.Provider) is { } variable
                    ? $"{ApiUsage.ProviderName(account.Provider)} API key · sets {variable}"
                    : $"{ApiUsage.ProviderName(account.Provider)} · shows its balance only"));
        AgentApiChoices.SelectedIndex = linked is null ? 0 : _agentApiChoices.IndexOf(linked) + 1;
        _agentApiReady = true;
        UpdateAgentApiNote(definition);

        if (linked is null
            && _agentApiChoices.FirstOrDefault(a => ApiUsage.AgentUses(definition, a.Provider)) is { } likely)
        {
            AgentApiOffer.Message = $"{definition.Name} uses {ApiUsage.ProviderName(likely.Provider)}. Use {likely.Name}'s key?";
            AgentApiOfferButton.Tag = likely;
            AgentApiOffer.IsOpen = true;
        }

        static RadioButton ChoiceContent(string title, string detail)
        {
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock { Text = title });
            content.Children.Add(Note(detail));
            var choice = new RadioButton { Content = content };
            AutomationProperties.SetName(choice, title);
            AutomationProperties.SetHelpText(choice, detail);
            return choice;
        }
    }

    /// <summary>What the choice does: which variable it starts with, or why not, or that there are no keys yet.</summary>
    private void UpdateAgentApiNote(AgentDefinition definition)
    {
        var account = ApiAccountFor(definition);
        string? note;
        if (account is null)
            note = _agentApiChoices.Count == 0 ? $"No API keys {definition.Name} can use yet. Add one under Accounts." : null;
        else if (ApiUsage.KeyVariableFor(definition, account.Provider) is not { } variable)
            note = $"{definition.Name} can't take {account.Name}'s key, so its drawer only shows the balance.";
        else if (definition.Env?.ContainsKey(variable) == true)
            note = $"{definition.Name}'s own {variable} variable below is used, not {account.Name}'s key.";
        else if (account.Provider.Equals("hyper", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(account.ApiKey))
            note = "Crush keeps its own Hyper sign-in. Agent Dock reads its current token only to show the Hypercredit balance.";
        else if (string.IsNullOrWhiteSpace(account.ApiKey))
            note = $"Add {account.Name}'s API key under Accounts, and Agent Dock gives it to {definition.Name} as {variable}.";
        else
            note = $"{definition.Name} starts with {account.Name}'s key as {variable}."
                   + (ApiUsage.LeavesPlan(definition, account.Provider) ? " Its usage is billed to this key, not your Claude plan." : "")
                   + (ApiUsage.ChecksBalance(account.Provider) ? " Its drawer shows the key's balance." : "");
        AgentApiNote.Text = note ?? "";
        AgentApiNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AgentApiChoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_agentApiReady || _editingAgent is null || AgentApiChoices.SelectedIndex < 0)
            return;
        var index = AgentApiChoices.SelectedIndex;
        var id = index == 0 ? null : _agentApiChoices[index - 1].Id;
        if (string.Equals(id, _editingAgent.ApiAccount, StringComparison.OrdinalIgnoreCase))
            return;
        _editingAgent.ApiAccount = id;
        SaveAgentList(SettingsChange.ApiAccounts);
        AgentApiOffer.IsOpen = false;
        UpdateAgentApiNote(_editingAgent);
        // Claude Code on a key has no plan usage to choose a source for.
        var editing = _editingAgent;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_editingAgent, editing))
                UpdateAgentPanel(editing);
        });
    }

    private void AgentApiOffer_Click(object sender, RoutedEventArgs e)
    {
        if (_editingAgent is null || AgentApiOfferButton.Tag is not ApiAccount account)
            return;
        _editingAgent.ApiAccount = account.Id;
        SaveAgentList(SettingsChange.ApiAccounts);
        UpdateAgentPanel(_editingAgent);
    }
}
