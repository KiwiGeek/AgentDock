using AgentDock.Models;

namespace AgentDock.Services.Usage;

/// <summary>One active quota window, normalized for comparing agents with unlike reset periods.</summary>
/// <param name="Word">What the window is called in the suggestion: session, week, month, day or balance.</param>
/// <param name="Used">Percent used, 0–100.</param>
/// <param name="Elapsed">How far through the window it is, 0–100, when its length is known.</param>
public sealed record PaceWindow(string Word, double Used, double? Elapsed, DateTimeOffset? Reset);

/// <summary>Something the usage overview could suggest: an agent's account, or an API key.</summary>
/// <param name="Key">Tells candidates apart (the overview row's key).</param>
/// <param name="IsApi">An API key's balance rather than a plan.</param>
/// <param name="Unlimited">No finite limit at all (GitHub's unlimited Copilot entitlement).</param>
public sealed record SuggestionCandidate(string Key, bool IsApi, bool Unlimited, IReadOnlyList<PaceWindow> Windows);

/// <summary>
/// A candidate's capacity from three useful perspectives. The balanced score is the bottleneck: an
/// agent cannot take more work than its most-used active limit allows.
/// </summary>
public sealed record CapacityChoice(SuggestionCandidate Candidate, PaceWindow? Bottleneck, double Balanced,
    double QuickTask, double Sustained);

/// <summary>An alternative to the pick, and the window that makes it one.</summary>
public sealed record SuggestionAlternative(CapacityChoice Choice, PaceWindow Window);

/// <summary>The overview's suggestion: the pick, and the alternatives worth naming beside it.</summary>
public sealed record Suggestion(CapacityChoice Pick, SuggestionAlternative? QuickTask,
    SuggestionAlternative? Sustained, SuggestionAlternative? AfterReset);

/// <summary>
/// Which agent the usage overview suggests (owner's request, 2026-10-16): the least constrained
/// across every active quota window. Pure, so it can be tested; the flyout turns its rows into
/// <see cref="SuggestionCandidate"/>s and words the result.
/// </summary>
public static class UsageSuggestion
{
    /// <summary>A reset this close counts as a reason to wait for another agent.</summary>
    private static readonly TimeSpan ImminentReset = TimeSpan.FromHours(3);

    /// <summary>Points of headroom an alternative needs over the pick to be worth naming.</summary>
    private const double MaterialDifference = 15;

    /// <summary>
    /// A plan's limits with how far through each window it is: a session is five hours, a week seven
    /// days, a month (Cursor's billing period, Kiro's) the month before its reset, a day (Freebuff's)
    /// the day before. <paramref name="longWord"/> names the longer window ("week", "month" or "day").
    /// </summary>
    public static List<PaceWindow> PlanWindows(PlanUsage? usage, string longWord, DateTimeOffset now)
    {
        var windows = new List<PaceWindow>();
        void Add(UsageWindow? window, string word, Func<DateTimeOffset, DateTimeOffset> startOf)
        {
            if (window is null)
                return;
            if (window.ResetsAt <= 0)
            {
                windows.Add(new PaceWindow(word, Math.Clamp(window.UsedPercentage, 0, 100), null, null));
                return;
            }
            var reset = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt);
            // Already reset: a fresh window.
            windows.Add(reset <= now
                ? new PaceWindow(word, 0, 0, reset)
                : new PaceWindow(word, Math.Clamp(window.UsedPercentage, 0, 100), Elapsed(startOf(reset), reset, now), reset));
        }
        Add(usage?.FiveHour, "session", reset => reset.AddHours(-5));
        Add(usage?.SevenDay, longWord,
            longWord == "month" ? reset => reset.AddMonths(-1)
            : longWord == "day" ? reset => reset.AddDays(-1)
            : reset => reset.AddDays(-7));
        return windows;
    }

    /// <summary>Copilot's AI credits this month. None when unlimited or without an entitlement.</summary>
    public static List<PaceWindow> CopilotWindows(CopilotUsageResult copilot, DateTimeOffset now)
    {
        if (copilot is not { Used: { } used, Unlimited: false, Entitlement: { } entitlement })
            return [];
        var reset = copilot.ResetsAt ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        return [new PaceWindow("month", Math.Clamp(used * 100 / entitlement, 0, 100), Elapsed(reset.AddMonths(-1), reset, now), reset)];
    }

    /// <summary>
    /// A prepaid balance has no window: how much of the last top-up is gone, for as long as it lasts.
    /// Spend with no balance set (Anthropic) has nothing to compare, so it gives none.
    /// </summary>
    public static List<PaceWindow> BalanceWindows(ApiBalanceState? balance) =>
        balance is { } state && ApiUsage.HasPercent(state) ? [new PaceWindow("balance", ApiUsage.UsedPercent(state), null, null)] : [];

    /// <summary>Scores usable windows without assuming model quality, cost or the user's task size. Null when it can't take work.</summary>
    public static CapacityChoice? CapacityFor(SuggestionCandidate candidate) => CapacityFor(candidate, candidate.Windows);

    private static CapacityChoice? CapacityFor(SuggestionCandidate candidate, IReadOnlyList<PaceWindow> windows)
    {
        if (candidate.Unlimited)
            return new CapacityChoice(candidate, null, 0, 0, 0);
        if (windows.Count == 0 || windows.Any(window => window.Used >= 100))
            return null;

        var bottleneck = windows.OrderByDescending(window => window.Used).First();
        var session = windows.FirstOrDefault(window => window.Word == "session");
        var longer = windows.FirstOrDefault(window => IsLong(window.Word));
        return new CapacityChoice(candidate, bottleneck, bottleneck.Used, session?.Used ?? bottleneck.Used,
            longer?.Used ?? bottleneck.Used);
    }

    /// <summary>
    /// The least constrained candidate by its tightest window (a minimax score), then the others worth
    /// naming: one for a quick task, one for sustained work, and one whose limit resets within three
    /// hours, each only when materially better than the pick. Null with fewer than two usable
    /// candidates, counted before <paramref name="preferPlans"/> narrows them, since a suggestion needs
    /// something to choose between.
    /// </summary>
    /// <param name="preferPlans">Included usage first: API keys only once no plan has room.</param>
    public static Suggestion? Suggest(IReadOnlyList<SuggestionCandidate> candidates, bool preferPlans, DateTimeOffset now)
    {
        var choices = candidates.Select(CapacityFor).OfType<CapacityChoice>().ToList();
        if (choices.Count < 2)
            return null;
        if (preferPlans && choices.Any(choice => !choice.Candidate.IsApi))
            choices = choices.Where(choice => !choice.Candidate.IsApi).ToList();

        var pick = choices.OrderBy(choice => choice.Balanced).ThenBy(choice => choice.Sustained)
            .ThenBy(choice => choice.QuickTask).First();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pick.Candidate.Key };

        var quick = Best(choices, shown, window => window.Word == "session", choice => choice.QuickTask);
        if (quick is not null && MateriallyBetter(pick.QuickTask, quick.Choice.QuickTask))
            shown.Add(quick.Choice.Candidate.Key);
        else
            quick = null;

        var sustained = Best(choices, shown, window => IsLong(window.Word), choice => choice.Sustained);
        if (sustained is not null && MateriallyBetter(pick.Sustained, sustained.Choice.Sustained))
            shown.Add(sustained.Choice.Candidate.Key);
        else
            sustained = null;

        // Every candidate, not only those the preference left: a plan resetting soon is worth knowing.
        SuggestionAlternative? afterReset = null;
        foreach (var candidate in candidates)
        {
            for (var index = 0; index < candidate.Windows.Count; index++)
            {
                var window = candidate.Windows[index];
                if (window.Reset is not { } reset || reset <= now || reset - now > ImminentReset)
                    continue;
                var simulated = candidate.Windows.Select((other, otherIndex) => otherIndex == index ? other with { Used = 0 } : other).ToList();
                if (CapacityFor(candidate, simulated) is not { } choice || shown.Contains(candidate.Key))
                    continue;
                if (afterReset is null || choice.Balanced < afterReset.Choice.Balanced)
                    afterReset = new SuggestionAlternative(choice, window);
            }
        }
        if (afterReset is not null && !MateriallyBetter(pick.Balanced, afterReset.Choice.Balanced))
            afterReset = null;

        return new Suggestion(pick, quick, sustained, afterReset);
    }

    /// <summary>The candidate not yet named that has a window <paramref name="has"/> accepts, lowest <paramref name="score"/> first.</summary>
    private static SuggestionAlternative? Best(List<CapacityChoice> choices, HashSet<string> shown,
        Func<PaceWindow, bool> has, Func<CapacityChoice, double> score) =>
        choices.Where(choice => !shown.Contains(choice.Candidate.Key))
            .Select(choice => (Choice: choice, Window: choice.Candidate.Windows.FirstOrDefault(has)))
            .Where(candidate => candidate.Window is not null)
            .OrderBy(candidate => score(candidate.Choice)).ThenBy(candidate => candidate.Choice.Balanced)
            .Select(candidate => new SuggestionAlternative(candidate.Choice, candidate.Window!))
            .FirstOrDefault();

    private static bool MateriallyBetter(double current, double alternative) => current - alternative >= MaterialDifference;

    private static bool IsLong(string word) => word is "week" or "month" or "day" or "balance";

    private static double? Elapsed(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now) =>
        end > start ? Math.Clamp((now - start) / (end - start) * 100, 0, 100) : null;
}
