using AgentDock.Models;
using AgentDock.Services.Agents;
using AgentDock.Services.Usage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentDock.Views;

/// <summary>
/// The usage overview (the drawer while no agent is pointed at): one row per account, and the suggestion.
/// </summary>
public sealed partial class FlyoutWindow
{
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

        if (KnownAgents.IsCursor(item.Definition))
        {
            var check = Check(CursorCheckKey);
            return new SummaryEntry(CursorCheckKey, item, check?.Result?.Usage ?? SavedFor(CursorCheckKey, CursorUsage.LoadSaved),
                check?.Running == true, "month");
        }
        if (KnownAgents.IsKiro(item.Definition))
        {
            var check = Check(KiroCheckKey);
            return new SummaryEntry(KiroCheckKey, item, check?.Result?.Usage ?? SavedFor(KiroCheckKey, KiroUsage.LoadSaved),
                check?.Running == true, "month");
        }
        if (KnownAgents.IsFreebuff(item.Definition))
        {
            var key = FreebuffCheckKey(item.Definition);
            var check = Check(key);
            return new SummaryEntry(key, item,
                check?.Result?.Usage ?? SavedFor(key, () => FreebuffUsage.LoadSaved(FreebuffUsage.ConfigDirFor(item.Definition))),
                check?.Running == true, "day");
        }
        if (KnownAgents.IsGitHubCopilot(item.Definition))
            return new SummaryEntry("copilot", item, null, _copilotFetching && _copilotUsage is null, "month", _copilotUsage);
        // Claude desktop has Claude Code's account, so it shares that row (or has it, if alone).
        var account = UsageAccount.For(item.Definition);
        if (account is { UsesOtherProvider: true } || (!item.IsCli && account is null))
            return null;
        switch (account?.Agent ?? KnownAgents.CommandName(item.Definition.Command))
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
            if (KnownAgents.IsCursor(item.Definition))
            {
                StartCursorCheck();
                continue;
            }
            if (KnownAgents.IsKiro(item.Definition))
            {
                StartKiroCheck();
                continue;
            }
            if (KnownAgents.IsFreebuff(item.Definition))
            {
                StartFreebuffCheck(item.Definition);
                continue;
            }
            if (KnownAgents.IsGitHubCopilot(item.Definition))
            {
                StartCopilotCheck();
                continue;
            }
            var account = UsageAccount.For(item.Definition);
            if (account is { UsesOtherProvider: true } || (!item.IsCli && account is null))
                continue;
            switch (account?.Agent ?? KnownAgents.CommandName(item.Definition.Command))
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
    /// Under the overview's rows: which agent to use, and the alternatives worth naming
    /// (<see cref="UsageSuggestion.Suggest"/> decides; this words it).
    /// </summary>
    private void ShowSuggestion(List<SummaryEntry> entries, DateTimeOffset now)
    {
        // A key no agent is linked to can't be suggested: there's nothing to start.
        // In the rows' order, which settles ties.
        var startable = entries.Where(entry => entry.Item is not null).ToList();
        var byKey = startable.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var candidates = startable.Select(entry => new SuggestionCandidate(entry.Key, entry.Api is not null,
            entry.Copilot is { Unlimited: true }, PaceWindows(entry, now))).ToList();
        if (UsageSuggestion.Suggest(candidates, _state.PreferPlansOverApi, now) is not { } suggestion)
        {
            SummarySuggestion.Visibility = Visibility.Collapsed;
            return;
        }

        string Name(CapacityChoice choice) => SuggestedName(byKey[choice.Candidate.Key]);
        var pick = suggestion.Pick;
        SummarySuggestionTitle.Text = $"Suggested: {Name(pick)}";
        var lines = new List<string>
        {
            pick.Bottleneck is null ? "Unlimited usage." : $"Best balanced capacity: {Math.Round(pick.Balanced)}% at its tightest limit ({pick.Bottleneck.Word})."
        };
        if (suggestion.QuickTask is { } quick)
            lines.Add($"For a quick task: {Name(quick.Choice)} - {Math.Round(quick.Choice.QuickTask)}% of this session used.");
        if (suggestion.Sustained is { } sustained)
            lines.Add($"For sustained work: {Name(sustained.Choice)} - {Math.Round(sustained.Choice.Sustained)}% of its {sustained.Window.Word} used.");
        if (suggestion.AfterReset is { } afterReset)
            lines.Add($"After {ResetTime(afterReset.Window.Reset!.Value)}: {Name(afterReset.Choice)} - {afterReset.Window.Word} limit resets.");

        SummarySuggestionNote.Text = string.Join('\n', lines);
        SummarySuggestion.Visibility = Visibility.Visible;
    }

    /// <summary>An overview row's limits, as the suggestion compares them.</summary>
    private static List<PaceWindow> PaceWindows(SummaryEntry entry, DateTimeOffset now) =>
        entry.Api is not null ? UsageSuggestion.BalanceWindows(entry.Balance)
        : entry.Copilot is { } copilot ? UsageSuggestion.CopilotWindows(copilot, now)
        : UsageSuggestion.PlanWindows(entry.Usage, entry.WeekWord, now);

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
        else if (entry.Item is not null && KnownAgents.IsFreebuff(entry.Item.Definition))
        {
            if (entry.Usage?.Allowance is { } allowance)
            {
                var reset = allowance.ResetsAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(allowance.ResetsAt) : (DateTimeOffset?)null;
                var remaining = reset is { } at && at <= now ? allowance.Limit : allowance.Remaining;
                var percent = allowance.Limit > 0 ? Math.Clamp((allowance.Limit - remaining) * 100 / allowance.Limit, 0, 100) : 0;
                var counts = $"{FreebuffUsage.FormatHours(remaining)} of {FreebuffUsage.FormatHours(allowance.Limit)} {allowance.Unit}";
                bars.Add(new SummaryBar(percent,
                    reset is { } when && when <= now ? $"{counts} · reset" : reset is { } later ? $"{counts} · resets {ResetTime(later)}" : counts,
                    "Daily hours"));
            }
            else if (entry.Checking)
                bars.Add(new SummaryBar(null, "Checking…"));
            else
                return null;
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
}
