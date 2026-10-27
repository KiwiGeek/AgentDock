using AgentDock.Models;
using AgentDock.Services.Agents;
using AgentDock.Services.Usage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentDock.Views;

/// <summary>
/// The drawer's usage: each agent's checks, the bars, extras and breakdown, and the status line offer.
/// </summary>
public sealed partial class FlyoutWindow
{
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
        if (!PrepareAgentDrawer(item))
            return false;
        // The agent's own content replaces the overview.
        _summaryShowing = false;
        SummaryPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool PrepareAgentDrawer(AgentItem item)
    {
        RecentSection.Visibility = Visibility.Collapsed;
        if (!item.IsAgentAvailable)
            return false;
        // Linked to an API key (Aider, OpenCode or DeepSeek Harness, say): that balance, whatever the agent.
        if (ApiAccountFor(item.Definition) is { } api)
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartApiCheck(api);
            ShowApiUsage(api);
            ShowRecents(item);
            return true;
        }
        if (KnownAgents.IsFreebuff(item.Definition))
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartFreebuffCheck(item.Definition);
            ShowFreebuffCheck(item.Definition);
            ShowRecents(item);
            return true;
        }
        // The CLI and the desktop app share one account, so both show its usage.
        if (KnownAgents.IsCursor(item.Definition))
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartCursorCheck();
            ShowCursorCheck();
            ShowRecents(item);
            return true;
        }
        if (KnownAgents.IsKiro(item.Definition))
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartKiroCheck();
            ShowKiroCheck();
            ShowRecents(item);
            return true;
        }
        if (KnownAgents.IsGitHubCopilot(item.Definition))
        {
            _drawerItem = item;
            UsageOffer.IsOpen = false;
            StartCopilotCheck();
            ShowCopilotUsage();
            ShowRecents(item);
            return true;
        }
        // Claude and ChatGPT desktop share Claude Code's and Codex's default accounts (UsageAccount.For),
        // so they get the same drawer as those, below, with their own recents.
        if (!item.IsCli && UsageAccount.For(item.Definition) is null)
        {
            // Another desktop app that opens in a folder: its recent folders, when there are some.
            if (!item.TakesFolder)
                return false;
            UsagePanel.Visibility = Visibility.Collapsed;
            UsageOffer.IsOpen = false;
            if (!ShowRecents(item))
                return false;
            _drawerItem = item;
            return true;
        }

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

        switch (account?.Agent ?? KnownAgents.CommandName(item.Definition.Command))
        {
            case "claude":
                return PrepareClaudeDrawer(item, account!);
            case "codex":
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                // Live from Codex, as this account, at most every few minutes; what's known shows meanwhile.
                StartCodexCheck(account!);
                ShowCodexCheck(account!);
                ShowRecents(item);
                return true;
            case "grok":
                // From grok.com, with Grok Build's own sign-in.
                _drawerItem = item;
                UsageOffer.IsOpen = false;
                StartGrokCheck();
                ShowGrokCheck();
                ShowRecents(item);
                return true;
            default:
                // These CLIs have no common provider quota, but they do open in folders and can show them again.
                if (!RecentLaunches.Records(item.Definition))
                    return false;
                _drawerItem = item;
                UsagePanel.Visibility = Visibility.Collapsed;
                UsageOffer.IsOpen = false;
                return ShowRecents(item);
        }
    }

    /// <summary>Where this Claude account's usage comes from (set in the settings window, on the agent's page).</summary>
    private ClaudeUsageSource SourceFor(UsageAccount account)
    {
        if (account.IsDefault)
            return _state.ClaudeUsageSource;
        foreach (var (dir, source) in _state.ClaudeUsageSources)
            if (string.Equals(dir, account.ConfigDir, StringComparison.OrdinalIgnoreCase))
                return source;
        return ClaudeUsageSource.StatusLine;
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
            StartClaudeCheck(account);
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
                    "Claude Code's status line runs a copy of Agent Dock that isn't there any more, so usage here won't update.", closable: false);
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

        if (shown != _generation)
            return;
        if (_drawerOpen && !_drawerClosing && _drawerItem is { } item && showing(item))
            show();
        else if (_summaryShowing)
            ShowSummary();
    }

    /// <summary>Grok, Cursor and Kiro have one account each, their CLI's sign-in.</summary>
    private const string GrokCheckKey = "grok";
    private const string CursorCheckKey = "cursor";
    private const string KiroCheckKey = "kiro";

    // Each starts its agent's check unless one is running or fresh, and fills the drawer if it's
    // still on that agent when the answer comes (else the overview, if that's showing).

    private void StartClaudeCheck(UsageAccount account) =>
        StartLiveCheck(account.Key, ClaudeUsageApi.MinInterval, () => ClaudeUsageApi.FetchAsync(account), ClaudeUsageApi.Unreachable,
            shown => OnAccount(shown, account) && SourceFor(account) == ClaudeUsageSource.Api, () => ShowClaudeCheck(account));

    private void StartCodexCheck(UsageAccount account) =>
        StartLiveCheck(account.Key, CodexUsage.MinInterval, () => CodexUsage.FetchAsync(account), CodexUsage.Unreachable,
            shown => OnAccount(shown, account), () => ShowCodexCheck(account));

    private void StartGrokCheck() =>
        StartLiveCheck(GrokCheckKey, GrokUsage.MinInterval, GrokUsage.FetchAsync, GrokUsage.Unreachable,
            shown => KnownAgents.CommandName(shown.Definition.Command) == "grok", ShowGrokCheck);

    /// <summary>Freebuff's account, one per CLI config folder.</summary>
    private static string FreebuffCheckKey(AgentDefinition definition) =>
        $"freebuff|{FreebuffUsage.ConfigDirFor(definition).ToLowerInvariant()}";

    private void StartFreebuffCheck(AgentDefinition definition)
    {
        var key = FreebuffCheckKey(definition);
        StartLiveCheck(key, FreebuffUsage.MinInterval, () => FreebuffUsage.FetchAsync(FreebuffUsage.ConfigDirFor(definition)),
            FreebuffUsage.Unreachable,
            shown => KnownAgents.IsFreebuff(shown.Definition) && FreebuffCheckKey(shown.Definition) == key,
            () => ShowFreebuffCheck(definition));
    }

    private void StartCursorCheck() =>
        StartLiveCheck(CursorCheckKey, CursorUsage.MinInterval, CursorUsage.FetchAsync, CursorUsage.Unreachable,
            shown => KnownAgents.IsCursor(shown.Definition), ShowCursorCheck);

    private void StartKiroCheck() =>
        StartLiveCheck(KiroCheckKey, KiroUsage.MinInterval, KiroUsage.FetchAsync, KiroUsage.Unreachable,
            shown => KnownAgents.IsKiro(shown.Definition), ShowKiroCheck);

    private void StartCopilotCheck()
    {
        if (!_copilotFetching && (_copilotUsage is null || DateTimeOffset.UtcNow - _copilotUsage.FetchedAt > CopilotUsageMaxAge))
            _ = FetchCopilotUsageAsync();
    }

    private void ShowFreebuffCheck(AgentDefinition definition)
    {
        var key = FreebuffCheckKey(definition);
        var check = _liveChecks.GetValueOrDefault(key);
        if (!_savedUsage.TryGetValue(key, out var saved))
            _savedUsage[key] = saved = FreebuffUsage.LoadSaved(FreebuffUsage.ConfigDirFor(definition));
        var usage = check?.Result?.Usage ?? saved;
        // One daily pool, counted in hours rather than a 5-hour session or a week.
        ShowCheckedUsage("Freebuff usage", usage, check?.Running == true, check?.Result?.Problem,
            FreebuffUsage.Checking, FreebuffUsage.NoWindows, FreebuffUsage.ShowingSaved, showSession: false, weekLabel: "Daily");
        if (usage?.Allowance is not { } allowance)
            return;
        var now = DateTimeOffset.UtcNow;
        var reset = allowance.ResetsAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(allowance.ResetsAt) : (DateTimeOffset?)null;
        var remaining = reset is { } at && at <= now ? allowance.Limit : allowance.Remaining;
        var counts = $"{FreebuffUsage.FormatHours(remaining)} of {FreebuffUsage.FormatHours(allowance.Limit)} {allowance.Unit}";
        WeekText.Text = reset is { } when && when <= now ? $"{counts} · reset"
            : reset is { } later ? $"{counts} · resets {ResetTime(later)}"
            : counts;
    }

    private void ShowCursorCheck()
    {
        var check = _liveChecks.GetValueOrDefault(CursorCheckKey);
        if (!_savedUsage.TryGetValue(CursorCheckKey, out var saved))
            _savedUsage[CursorCheckKey] = saved = CursorUsage.LoadSaved();
        // One monthly billing period: the total, then its Auto + Composer and API pools under it.
        ShowCheckedUsage("Cursor usage", check?.Result?.Usage ?? saved, check?.Running == true, check?.Result?.Problem,
            CursorUsage.Checking, CursorUsage.NoWindows, CursorUsage.ShowingSaved, showSession: false, weekLabel: "Total");
    }

    private void ShowKiroCheck()
    {
        var check = _liveChecks.GetValueOrDefault(KiroCheckKey);
        if (!_savedUsage.TryGetValue(KiroCheckKey, out var saved))
            _savedUsage[KiroCheckKey] = saved = KiroUsage.LoadSaved();
        // Monthly plan credits only (no session window). Credit counts ride on Extra when the CLI gave them.
        var usage = check?.Result?.Usage ?? saved;
        ShowCheckedUsage("Kiro usage", usage, check?.Running == true, check?.Result?.Problem,
            KiroUsage.Checking, KiroUsage.NoWindows, KiroUsage.ShowingSaved, showSession: false, weekLabel: "Credits");
        if (usage is { SevenDay: { } week, Extra: { Enabled: false, Used: { } used, Limit: { } limit } })
        {
            var counts = System.Globalization.CultureInfo.CurrentCulture;
            WeekText.Text = week.ResetsAt > 0
                ? $"{used.ToString("0.##", counts)} of {limit.ToString("0.##", counts)} · resets {ResetTime(DateTimeOffset.FromUnixTimeSeconds(week.ResetsAt))}"
                : $"{used.ToString("0.##", counts)} of {limit.ToString("0.##", counts)}";
        }
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
        if (_drawerOpen && !_drawerClosing && _drawerItem is { } item && KnownAgents.IsGitHubCopilot(item.Definition))
            ShowCopilotUsage();
        else if (_summaryShowing)
            ShowSummary();
    }

    /// <summary>
    /// Copilot's single monthly window: AI credits (or, on GitHub's legacy billing, premium
    /// requests) used against the plan's entitlement, as GitHub reports both. Without an entitlement
    /// (unlimited, or none given) it's just the count. Overage shows underneath when allowed.
    /// </summary>
    private void ShowCopilotUsage()
    {
        UsageTitle.Text = "Copilot usage";
        UsagePanel.Visibility = Visibility.Visible;
        WeekGroup.Visibility = Visibility.Collapsed;
        SessionLabel.Visibility = SessionText.Visibility = Visibility.Visible; // Grok hides them
        ShowUsageExtras(null, DateTimeOffset.UtcNow);

        var usage = _copilotUsage;
        var hasData = usage?.Used is not null;
        SessionLabel.Text = usage is { Unit.Length: > 0 } ? char.ToUpperInvariant(usage.Unit[0]) + usage.Unit[1..] : "Premium usage";
        UsageRows.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        UsageNote.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = usage?.Problem ?? "Checking GitHub…";
        UsageUpdatedText.Text = "";
        if (!hasData)
            return;

        var now = DateTimeOffset.UtcNow;
        // GitHub resets on the 1st at 00:00 UTC; its own date is used when it gives one.
        var reset = usage!.ResetsAt ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var used = usage.Used!.Value;
        var counts = System.Globalization.CultureInfo.CurrentCulture;
        if (usage.Unlimited)
        {
            SessionBar.Visibility = Visibility.Collapsed;
            SessionText.Text = $"Unlimited · {used.ToString("N0", counts)} used";
        }
        else if (usage.Entitlement is { } entitlement)
        {
            SessionBar.Visibility = Visibility.Visible;
            ShowWindow(SessionBar, SessionText,
                new UsageWindow { UsedPercentage = Math.Min(100, used * 100 / entitlement), ResetsAt = reset.ToUnixTimeSeconds() }, now);
            SessionText.Text = $"{used.ToString("N0", counts)} of {entitlement.ToString("N0", counts)} · resets {ResetTime(reset)}";
        }
        else
        {
            SessionBar.Visibility = Visibility.Collapsed;
            SessionText.Text = $"{used.ToString("N0", counts)} used · resets {ResetTime(reset)}";
        }

        var overage = usage.OveragePermitted
            ? usage.Overage > 0 ? $"Over the plan: {usage.Overage.ToString("N0", counts)} {usage.Unit}" : "Usage over the plan is allowed"
            : null;
        ExtraUsageText.Text = overage ?? "";
        ExtraUsageText.Visibility = overage is null ? Visibility.Collapsed : Visibility.Visible;
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

        // A window that counts sessions (Freebuff's free ones) reads "2 of 5" rather than a percentage.
        var counts = window is { Used: { } countUsed, Limit: { } countLimit }
            ? $"{countUsed:0.#} of {countLimit:0.#}"
            : null;
        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt);
        if (window.ResetsAt > 0 && resetsAt <= now)
        {
            bar.Value = 0;
            text.Text = counts is null ? "0% · reset" : $"{counts} · reset";
            return;
        }

        var used = Math.Clamp(window.UsedPercentage, 0, 100);
        bar.Value = used;
        // Windows' own warning and error colours for the bar.
        bar.ShowError = used >= 90;
        bar.ShowPaused = used is >= 75 and < 90;
        var reset = window.ResetsAt > 0 ? $" · resets {ResetTime(resetsAt)}" : "";
        text.Text = counts is null ? $"{Math.Round(used)}%{reset}" : counts + reset;
    }

    private static string ResetTime(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var days = (local.Date - DateTime.Today).Days;
        // "t" is the short time only as a standard format on its own; inside "ddd t" it means "A"/"P".
        return days == 0 ? local.ToString("t") : days < 7 ? $"{local:ddd} {local:t}" : local.ToString("MMM d");
    }

    internal static string UpdatedAgo(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        return age.TotalMinutes < 1 ? "Updated just now"
            : age.TotalHours < 1 ? $"Updated {(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"Updated {(int)age.TotalHours} hr ago"
            : $"Updated {when.ToLocalTime():MMM d}";
    }


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
        if ((_drawerItem is null || !ShowRecents(_drawerItem)) && !ShowSummary())
            CloseDrawer(animate: true); // nothing left to show in it
    }
}
