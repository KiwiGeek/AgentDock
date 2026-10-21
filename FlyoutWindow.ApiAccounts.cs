using System.Globalization;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentDock;

/// <summary>
/// API keys' balances (owner's request, 2026-10-19), the flyout's side: the checks and the drawer.
/// The overview's rows are with the others, in SummaryEntries. The settings section and the link
/// on an agent's page are in <c>SettingsWindow.ApiAccounts.cs</c>, which keeps its own checks.
/// </summary>
public sealed partial class FlyoutWindow
{
    private sealed class ApiCheck
    {
        public bool Running;
        public DateTimeOffset At;
        public string? Problem;
    }

    // Over the network, so kept across shows, like the other live checks.
    private readonly Dictionary<string, ApiCheck> _apiChecks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The account whose balance this agent's drawer shows. A key Agent Dock can't read a balance for
    /// (OpenAI, OpenRouter…) is only handed to the agent, so the drawer shows the agent's own usage.
    /// </summary>
    private ApiAccount? ApiAccountFor(AgentDefinition definition) =>
        ApiUsage.AccountFor(definition, _settings.ApiAccounts) is { } account && ApiUsage.ChecksBalance(account.Provider) ? account : null;

    /// <summary>Accounts with a balance to show and check.</summary>
    private IEnumerable<ApiAccount> CheckedApiAccounts => _settings.ApiAccounts.Where(a => ApiUsage.ChecksBalance(a.Provider));

    private ApiBalanceState? BalanceOf(ApiAccount account) => _state.ApiBalances.GetValueOrDefault(account.Id);

    /// <summary>The service's compiled-in logo, for the overview row.</summary>
    private static string? ApiProviderIcon(ApiAccount account) => ApiUsage.ProviderIcon(account.Provider);

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
        var result = await ApiUsage.FetchAsync(account, BalanceOf(account)?.Since ?? 0); // resumes on the UI thread; never throws
        check.Running = false;
        // Removed, or its key or service changed, while this ran.
        if (!_apiChecks.TryGetValue(account.Id, out var current) || current != check || !_settings.ApiAccounts.Contains(account))
            return;
        var now = DateTimeOffset.UtcNow;
        check.At = now;
        check.Problem = result.Problem;
        // Anthropic reports spend, not a balance; both are saved.
        if (result.Balance is not null || result.Spent is not null)
        {
            if (!_state.ApiBalances.TryGetValue(account.Id, out var state))
                _state.ApiBalances[account.Id] = state = new ApiBalanceState();
            ApiUsage.Track(state, result, now);
            _store.SaveState(_state);
        }
        RefreshApiViews(account);
    }

    private void RefreshApiViews(ApiAccount account)
    {
        if (_drawerOpen && !_drawerClosing && _drawerItem is { } item && ApiAccountFor(item.Definition) == account)
            ShowApiUsage(account);
        else if (_summaryShowing)
            ShowSummary();
    }

    // ---------- Drawer ----------

    /// <summary>A linked agent's drawer: one bar for the balance, "$5.80 left of $10.00" under it.</summary>
    private void ShowApiUsage(ApiAccount account)
    {
        var now = DateTimeOffset.UtcNow;
        UsageTitle.Text = $"{account.Name} usage";
        UsagePanel.Visibility = Visibility.Visible;
        WeekGroup.Visibility = Visibility.Collapsed;
        SessionLabel.Visibility = SessionText.Visibility = SessionBar.Visibility = Visibility.Visible;
        SessionLabel.Text = "Balance";
        ShowUsageExtras(null, now);

        var state = BalanceOf(account);
        var check = _apiChecks.GetValueOrDefault(account.Id);
        var hasData = state is { UpdatedAt: > 0 };
        UsageRows.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        var note = check?.Problem is { } problem
            ? hasData ? $"{problem} {ApiUsage.ShowingSaved}" : problem
            : hasData ? null : ApiUsage.Checking;
        UsageNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = note ?? "";
        UsageUpdatedText.Text = "";
        if (!hasData)
            return;

        var known = state!;
        if (ApiUsage.HasPercent(known))
        {
            var used = ApiUsage.UsedPercent(known);
            ShowWindow(SessionBar, SessionText, new UsageWindow { UsedPercentage = used }, now);
            SessionText.Text = $"{Math.Round(used)}% · {ApiUsage.Money(known.Balance, known.Currency)} left of {ApiUsage.Money(known.Full, known.Currency)}";
        }
        else
        {
            // Spend only (Anthropic, before its balance is set): no 100% to draw a bar against.
            SessionBar.Visibility = Visibility.Collapsed;
            SessionText.Text = ApiUsage.Summary(known);
        }
        UsageUpdatedText.Text = UpdatedAgo(DateTimeOffset.FromUnixTimeSeconds(known.UpdatedAt), now);
    }
}
