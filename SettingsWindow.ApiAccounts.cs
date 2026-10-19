using System.Globalization;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentDock;

/// <summary>
/// API keys' balances, on the settings side (owner's request, 2026-10-19): the API usage section, the
/// link on an agent's page, and the checks that fill in their status lines. The flyout has its own
/// checks for the drawer and the overview (FlyoutWindow.ApiAccounts.cs); both save the same balances.
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
    private readonly Dictionary<string, TextBlock> _apiStatusTexts = new(StringComparer.OrdinalIgnoreCase);
    private TextBox? _newApiKeyBox;
    /// <summary>False while the agent page's API choice is being filled, so that isn't a change.</summary>
    private bool _agentApiReady;

    private ApiAccount? ApiAccountFor(AgentDefinition definition) =>
        definition.ApiAccount is { } id ? _settings.ApiAccounts.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) : null;

    private ApiBalanceState? BalanceOf(ApiAccount account) => _state.ApiBalances.GetValueOrDefault(account.Id);

    // ---------- Checks ----------

    /// <summary>Checks the balance unless a check is running or the last is fresh (<paramref name="force"/>: Check now).</summary>
    private void StartApiCheck(ApiAccount account, bool force = false)
    {
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
        if (result.Balance is not null)
            UpdateState(state =>
            {
                if (!state.ApiBalances.TryGetValue(account.Id, out var balance))
                    state.ApiBalances[account.Id] = balance = new ApiBalanceState();
                ApiUsage.Track(balance, result, now);
            });
        UpdateApiAccountStatus(account);
    }
    // ---------- One API account's details (chosen in the left list) ----------

    /// <summary>Shows this account's editor in the right pane.</summary>
    private void ShowApiAccount(ApiAccount account)
    {
        _editingApi = account;
        _selectedTag = TagApiPrefix + account.Id;
        HideAllPanes();
        ApiPage.Visibility = Visibility.Visible;
        SettingsTitle.Text = account.Name;
        ApiAccountHost.Children.Clear();
        _apiStatusTexts.Clear();
        _newApiKeyBox = null;
        ApiAccountHost.Children.Add(ApiAccountEditor(account));
        StartApiCheck(account);
        SyncAgentBackdrop();
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Adds an account, puts it in the left list, and opens its editor.</summary>
    private void AddApiAccount()
    {
        if (_settingsError is not null)
        {
            SelectTag(TagGeneral);
            return;
        }
        var (provider, name) = ApiUsage.Providers[0];
        var account = new ApiAccount { Id = ApiUsage.NewId(provider, _settings.ApiAccounts), Name = name, Provider = provider };
        _settings.ApiAccounts.Add(account);
        if (!SaveAgentList(SettingsChange.ApiAccounts))
            return;
        SelectTag(TagApiPrefix + account.Id);
        DispatcherQueue.TryEnqueue(() => _newApiKeyBox?.Focus(FocusState.Programmatic));
    }

    /// <summary>One account: name and service, the key (shown as it is), its balance, and the 100% amount.</summary>
    private StackPanel ApiAccountEditor(ApiAccount account)
    {
        var panel = new StackPanel { Spacing = 8 };

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
            Header = "Service", MinWidth = 120,
            ItemsSource = ApiUsage.Providers.Select(p => p.Name).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(ApiUsage.Providers, p => p.Id.Equals(account.Provider, StringComparison.OrdinalIgnoreCase)))
        };
        AutomationProperties.SetName(service, $"{account.Name} service");
        service.SelectionChanged += (_, _) =>
        {
            var provider = ApiUsage.Providers[Math.Max(0, service.SelectedIndex)].Id;
            if (provider.Equals(account.Provider, StringComparison.OrdinalIgnoreCase))
                return;
            account.Provider = provider;
            ForgetBalance(account); // another service's balance has nothing to do with this one's
            SaveAgentList(SettingsChange.ApiAccounts);
            // Its labels and hints differ by service; rebuilt after this event, not inside it.
            DispatcherQueue.TryEnqueue(() => ShowApiAccount(account));
        };
        top.Children.Add(name);
        Grid.SetColumn(service, 1);
        top.Children.Add(service);
        panel.Children.Add(top);

        var spend = ApiUsage.ReportsSpend(account.Provider);
        // The key agents use; for DeepSeek it also reads the balance.
        var key = KeyBox("API key", $"{account.Name} API key",
            spend ? "sk-ant-api…, the key Aider uses" : "sk-…, or %DEEPSEEK_API_KEY% for an environment variable",
            () => account.ApiKey, value => account.ApiKey = value, rechecks: !spend);
        _newApiKeyBox = key;
        panel.Children.Add(key);
        // Anthropic reads spending only with an Admin key, which can't run models: a second key.
        if (spend)
            panel.Children.Add(KeyBox("Admin API key", $"{account.Name} Admin API key", "sk-ant-admin…, which reads spending",
                () => account.AdminKey, value => account.AdminKey = value, rechecks: true));

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
                if (!rechecks)
                    return;
                _apiChecks.Remove(account.Id);
                StartApiCheck(account, force: true);
            };
            return box;
        }

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        _apiStatusTexts[account.Id] = status;
        panel.Children.Add(status);

        // "I paid $20": the amount that counts as 100%, until the next top-up adds to it.
        var amountRow = new Grid { ColumnSpacing = 8 };
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // A balance service needs this only to correct it; a spend service (Anthropic) needs it for a
        // percentage at all: the Console's balance now, with spending counted from today.
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
        panel.Children.Add(amountRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var check = new Button { Content = "Check now" };
        check.Click += (_, _) => StartApiCheck(account, force: true);
        var remove = new Button { Content = "Remove" };
        var confirm = new Button { Content = "Remove", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var removeFlyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12, MaxWidth = 260,
                Children = { new TextBlock { Text = $"Remove {account.Name}? Agents linked to it stop showing its balance.", TextWrapping = TextWrapping.Wrap }, confirm }
            }
        };
        confirm.Click += (_, _) =>
        {
            removeFlyout.Hide();
            RemoveApiAccount(account);
        };
        remove.Flyout = removeFlyout;
        actions.Children.Add(check);
        actions.Children.Add(remove);
        panel.Children.Add(actions);

        UpdateApiAccountStatus(account);
        return panel;
    }

    /// <summary>The line under an account's key: its balance and when it was checked, or what's wrong.</summary>
    private void UpdateApiAccountStatus(ApiAccount account)
    {
        if (!_apiStatusTexts.TryGetValue(account.Id, out var text))
            return;
        var state = BalanceOf(account);
        var check = _apiChecks.GetValueOrDefault(account.Id);
        var parts = new List<string>();
        if (check?.Running == true)
            parts.Add(ApiUsage.Checking);
        else if (check?.Problem is { } problem)
            parts.Add(problem);
        if (state is { UpdatedAt: > 0 })
            parts.Add((ApiUsage.HasPercent(state)
                          ? $"{ApiUsage.Summary(state)} of {ApiUsage.Money(state.Full, state.Currency)}. "
                          : $"{ApiUsage.Summary(state)}. Set your balance below to see a percentage. ")
                      + FlyoutWindow.UpdatedAgo(DateTimeOffset.FromUnixTimeSeconds(state.UpdatedAt), DateTimeOffset.UtcNow));
        text.Text = parts.Count > 0 ? string.Join(" ", parts) : "Not checked yet.";
    }

    private void RemoveApiAccount(ApiAccount account)
    {
        var index = _settings.ApiAccounts.IndexOf(account);
        _settings.ApiAccounts.Remove(account);
        foreach (var agent in _settings.Agents.Where(a => string.Equals(a.ApiAccount, account.Id, StringComparison.OrdinalIgnoreCase)))
            agent.ApiAccount = null;
        ForgetBalance(account);
        SaveAgentList(SettingsChange.ApiAccounts);
        var next = index >= 0 && index < _settings.ApiAccounts.Count ? _settings.ApiAccounts[index]
            : index > 0 ? _settings.ApiAccounts[index - 1] : null;
        SelectTag(next is null ? TagAddApi : TagApiPrefix + next.Id);
    }

    private void ForgetBalance(ApiAccount account)
    {
        _apiChecks.Remove(account.Id);
        if (_store.LoadState().ApiBalances.ContainsKey(account.Id))
            UpdateState(state => state.ApiBalances.Remove(account.Id));
    }

    // ---------- An agent's page: which account's balance its drawer shows ----------

    /// <summary>
    /// The choice, for command line agents while there are accounts, and an offer to link one the
    /// agent seems to use (Aider's model, OpenCode's providers).
    /// </summary>
    private void UpdateAgentApiSection(AgentDefinition definition)
    {
        var accounts = _settings.ApiAccounts;
        var show = definition.Kind == AgentKind.Cli && accounts.Count > 0;
        AgentApiSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AgentApiOffer.IsOpen = false;
        if (!show)
            return;

        _agentApiReady = false;
        AgentApiBox.ItemsSource = new List<string> { "None" }.Concat(accounts.Select(a => a.Name)).ToList();
        AgentApiBox.SelectedIndex = accounts.FindIndex(a => string.Equals(a.Id, definition.ApiAccount, StringComparison.OrdinalIgnoreCase)) + 1;
        _agentApiReady = true;
        UpdateAgentApiNote(definition);

        if (ApiAccountFor(definition) is null
            && accounts.FirstOrDefault(a => ApiUsage.AgentUses(definition, a.Provider)) is { } likely)
        {
            AgentApiOffer.Message = $"{definition.Name} uses {ApiUsage.ProviderName(likely.Provider)}. Show {likely.Name}'s balance in its drawer?";
            AgentApiOfferButton.Tag = likely;
            AgentApiOffer.IsOpen = true;
        }
    }

    /// <summary>
    /// For Aider, the link also hands it the key (<see cref="ApiUsage.EnvironmentFor"/>), so say so:
    /// which variable, or that its own variable wins, or that the account has no key for it yet.
    /// </summary>
    private void UpdateAgentApiNote(AgentDefinition definition)
    {
        var account = ApiAccountFor(definition);
        if (account is null || !ApiUsage.IsAider(definition))
        {
            AgentApiNote.Visibility = Visibility.Collapsed;
            return;
        }
        var variable = ApiUsage.KeyVariable(account.Provider);
        AgentApiNote.Text = definition.Env?.ContainsKey(variable) == true
            ? $"Aider's own {variable} variable below is used, not {account.Name}'s key."
            : string.IsNullOrWhiteSpace(account.ApiKey)
                ? $"Add {account.Name}'s API key in settings, and Agent Dock gives it to Aider as {variable}."
                : $"Agent Dock gives Aider {account.Name}'s key as {variable} when it starts it, so it needs no variable of its own.";
        AgentApiNote.Visibility = Visibility.Visible;
    }

    private void AgentApiBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_agentApiReady || _editingAgent is null || AgentApiBox.SelectedIndex < 0)
            return;
        var index = AgentApiBox.SelectedIndex;
        var id = index == 0 ? null : _settings.ApiAccounts[index - 1].Id;
        if (string.Equals(id, _editingAgent.ApiAccount, StringComparison.OrdinalIgnoreCase))
            return;
        _editingAgent.ApiAccount = id;
        SaveAgentList(SettingsChange.ApiAccounts);
        AgentApiOffer.IsOpen = false;
        UpdateAgentApiNote(_editingAgent);
    }

    private void AgentApiOffer_Click(object sender, RoutedEventArgs e)
    {
        if (_editingAgent is null || AgentApiOfferButton.Tag is not ApiAccount account)
            return;
        _editingAgent.ApiAccount = account.Id;
        SaveAgentList(SettingsChange.ApiAccounts);
        UpdateAgentApiSection(_editingAgent);
    }
}
