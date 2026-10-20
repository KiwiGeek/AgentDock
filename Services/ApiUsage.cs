using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// What a check found: the balance (DeepSeek), or the spend since a date (Anthropic, which has no
/// balance API), or a sentence saying why not.
/// </summary>
public sealed record ApiBalanceFetch(double? Balance, string Currency, bool Exhausted, string? Problem, double? Spent = null);

/// <summary>A service an API key can be for: its id in agents.json, its name, the variable tools read its key from.</summary>
public sealed record ApiProvider(string Id, string Name, string Variable, bool ChecksBalance);

/// <summary>
/// The balance on an API key (owner's request, 2026-10-19), for the usage overview and the drawer
/// of agents linked to it. A prepaid balance has no limit, so its percentage is measured against
/// <see cref="ApiBalanceState.Full"/>: what it was after the last top-up, or what the user says it was.
/// </summary>
public static class ApiUsage
{
    public const string NoKey = "Add this account's API key in settings.";
    public const string NoAdminKey = "Add an Admin API key in settings to see Anthropic spending.";
    public const string BadKey = "The service didn't accept this API key.";
    public const string Unreachable = "The service couldn't be reached.";
    public const string NotUnderstood = "The service's reply wasn't understood.";
    public const string Checking = "Checking the balance…";
    public const string ShowingSaved = "Showing the last saved balance.";
    public const string NoBalanceCheck = "Agent Dock can't read this service's balance.";

    public const string NeedsAdminKey =
        "Anthropic reports spending only to an Admin API key (sk-ant-admin…), which an organization's Console settings can create.";

    /// <summary>
    /// The services an API key can be for (Settings → Accounts; owner's request, 2026-10-20: one key,
    /// handed to every agent that can use it). The first is a new key's default. Only DeepSeek and
    /// Anthropic have a balance (or spend) Agent Dock can read; the rest are keys only.
    /// </summary>
    public static readonly ApiProvider[] Providers =
    [
        new("deepseek", "DeepSeek", "DEEPSEEK_API_KEY", ChecksBalance: true),
        new("anthropic", "Anthropic", "ANTHROPIC_API_KEY", ChecksBalance: true),
        new("openai", "OpenAI", "OPENAI_API_KEY", ChecksBalance: false),
        new("openrouter", "OpenRouter", "OPENROUTER_API_KEY", ChecksBalance: false),
        new("gemini", "Google Gemini", "GEMINI_API_KEY", ChecksBalance: false),
        new("xai", "xAI", "XAI_API_KEY", ChecksBalance: false)
    ];

    public static ApiProvider? ProviderFor(string provider) =>
        Providers.FirstOrDefault(p => p.Id.Equals(provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether Agent Dock can read this service's balance or spend, so it has a bar, an overview row and checks.</summary>
    public static bool ChecksBalance(string provider) => ProviderFor(provider)?.ChecksBalance == true;

    /// <summary>
    /// The service reports spending, not a balance (Anthropic), so 100% is only known once the user
    /// sets their balance, and spending is counted from then.
    /// </summary>
    public static bool ReportsSpend(string provider) => provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase);

    /// <summary>How long an answer stands before the drawer or overview asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string ProviderName(string provider) => ProviderFor(provider)?.Name ?? provider;

    /// <summary>
    /// The key that reads usage: the Admin key for Anthropic, the API key otherwise; as written, or
    /// the environment variable it names.
    /// </summary>
    public static string? KeyFor(ApiAccount account) =>
        Expand(ReportsSpend(account.Provider) ? account.AdminKey : account.ApiKey);

    private static string? Expand(string? setting)
    {
        if (setting?.Trim() is not { Length: > 0 } key)
            return null;
        var expanded = Environment.ExpandEnvironmentVariables(key);
        // An environment variable that isn't set stays as "%NAME%".
        return expanded.StartsWith('%') && expanded.EndsWith('%') ? null : expanded;
    }

    /// <summary>The variable most tools read the service's key from.</summary>
    public static string KeyVariable(string provider) => ProviderFor(provider)?.Variable ?? "DEEPSEEK_API_KEY";

    /// <summary>
    /// The variable <paramref name="agent"/> reads <paramref name="provider"/>'s key from, or null when
    /// Agent Dock doesn't hand it that key (owner's request, 2026-10-20: a key is added once and linked
    /// to every agent that can use it). From each tool's documentation; desktop apps never (agents.json
    /// variables don't reach them), nor agents that sign in to a plan of their own (Codex, Grok Build,
    /// Cursor CLI, Copilot CLI). Claude Code takes an Anthropic key, which moves it off its plan, so
    /// settings warns before linking it. A command of the user's own gets the usual variable.
    /// </summary>
    public static string? KeyVariableFor(AgentDefinition agent, string provider)
    {
        if (agent.Kind != AgentKind.Cli || ProviderFor(provider) is not { } service)
            return null;
        var command = Path.GetFileNameWithoutExtension(agent.Command?.Trim().Trim('"') ?? "").ToLowerInvariant();
        var known = (agent.Catalog ?? KnownAgents.All().FirstOrDefault(k =>
            string.Equals(k.Command, command, StringComparison.OrdinalIgnoreCase))?.Id)?.ToLowerInvariant();
        return (known ?? command) switch
        {
            // Aider goes through LiteLLM, which reads every one of these.
            "aider" => service.Variable,
            // OpenCode's providers come from models.dev, where Google's key has its own name.
            "opencode" => service.Id == "gemini" ? "GOOGLE_GENERATIVE_AI_API_KEY" : service.Variable,
            "crush" => service.Id is "anthropic" or "openai" or "openrouter" or "gemini" or "xai" ? service.Variable : null,
            "goose" => service.Id switch
            {
                "anthropic" or "openai" or "openrouter" => service.Variable,
                "gemini" => "GOOGLE_API_KEY",
                _ => null
            },
            "claude" => service.Id == "anthropic" ? service.Variable : null,
            "qwen" => service.Id == "openai" ? service.Variable : null,
            // DeepSeek Harness (`dsh`): its LLM and web plugins default to DEEPSEEK_API_KEY.
            "deepseek" or "dsh" => service.Id == "deepseek" ? service.Variable : null,
            _ when known is null => service.Variable, // the user's own command
            _ => null
        };
    }

    /// <summary>Linking <paramref name="agent"/> to an Anthropic key moves it from its Claude plan to API billing.</summary>
    public static bool LeavesPlan(AgentDefinition agent, string provider) =>
        provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileNameWithoutExtension(agent.Command?.Trim().Trim('"') ?? "").Equals("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>The account <paramref name="agent"/> is linked to, if it still exists.</summary>
    public static ApiAccount? AccountFor(AgentDefinition agent, IEnumerable<ApiAccount> accounts) =>
        agent.ApiAccount is { } id ? accounts.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>
    /// An agent linked to an account gets its key without typing variables (owner's requests,
    /// 2026-10-19 for Aider, 2026-10-20 for every agent that can take it): the variable from
    /// <see cref="KeyVariableFor"/>, set to the account's API key as written (so <c>%NAME%</c> stays a
    /// reference), unless the agent's own variables set it already.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? EnvironmentFor(AgentDefinition agent, IEnumerable<ApiAccount> accounts)
    {
        if (AccountFor(agent, accounts) is not { ApiKey: { Length: > 0 } key } account
            || KeyVariableFor(agent, account.Provider) is not { } variable)
            return agent.Env;
        var env = new Dictionary<string, string>(agent.Env ?? [], StringComparer.OrdinalIgnoreCase);
        if (!env.ContainsKey(variable))
            env[variable] = key.Trim();
        return env;
    }

    public static bool IsAider(AgentDefinition agent) =>
        (agent.Catalog ?? agent.Id).Equals("aider", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileNameWithoutExtension(agent.Command?.Trim().Trim('"') ?? "").Equals("aider", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks once, off the UI thread. Never throws. <paramref name="since"/> is where a spend-reporting
    /// service starts counting (<see cref="ApiBalanceState.Since"/>; 0 is the start of this month).
    /// </summary>
    public static Task<ApiBalanceFetch> FetchAsync(ApiAccount account, long since = 0) =>
        Task.Run(() => FetchCoreAsync(account, since));

    private static async Task<ApiBalanceFetch> FetchCoreAsync(ApiAccount account, long since)
    {
        try
        {
            if (!ChecksBalance(account.Provider))
                return new ApiBalanceFetch(null, "", false, NoBalanceCheck);
            if (KeyFor(account) is not { } key)
                return new ApiBalanceFetch(null, "", false, ReportsSpend(account.Provider) ? NoAdminKey : NoKey);
            if (account.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
                return await FetchAnthropicAsync(key, since);
            if (!account.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                return new ApiBalanceFetch(null, "", false, NotUnderstood);

            // Documented: api-docs.deepseek.com, "Get User Balance".
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Client.SendAsync(request);
            if ((int)response.StatusCode is 401 or 403)
                return new ApiBalanceFetch(null, "", false, BadKey);
            if (!response.IsSuccessStatusCode)
                return new ApiBalanceFetch(null, "", false, Unreachable);
            return ParseDeepSeek(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ApiBalanceFetch(null, "", false, Unreachable);
        }
    }

    /// <summary>
    /// Pure. DeepSeek's reply: <c>is_available</c>, and <c>balance_infos</c>, one per currency (USD or
    /// CNY), each with <c>total_balance</c> (granted plus topped up) as a string. USD is preferred.
    /// </summary>
    internal static ApiBalanceFetch ParseDeepSeek(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("balance_infos", out var infos)
                || infos.ValueKind != JsonValueKind.Array)
                return new ApiBalanceFetch(null, "", false, NotUnderstood);
            var available = !root.TryGetProperty("is_available", out var ok) || ok.ValueKind != JsonValueKind.False;

            (double Amount, string Currency)? pick = null;
            foreach (var info in infos.EnumerateArray())
            {
                if (info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("total_balance", out var total) || Amount(total) is not { } amount)
                    continue;
                var currency = info.TryGetProperty("currency", out var c) && c.GetString() is { Length: > 0 } code ? code : "USD";
                if (pick is null || currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
                    pick = (amount, currency);
            }
            // No balance at all yet is a zero balance, not a mistake.
            return pick is { } found
                ? new ApiBalanceFetch(found.Amount, found.Currency, !available, null)
                : new ApiBalanceFetch(0, "USD", !available, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new ApiBalanceFetch(null, "", false, NotUnderstood);
        }
    }

    /// <summary>
    /// Anthropic has no balance API (the Console shows it; claude-code issue #47574 asks for one), so
    /// this is the Admin API's documented Cost Report: <c>GET /v1/organizations/cost_report</c> with
    /// <c>x-api-key</c> (an Admin key), daily buckets from <paramref name="since"/>'s day, at most 31
    /// a page, followed through <c>next_page</c>. Amounts are cents.
    /// </summary>
    private static async Task<ApiBalanceFetch> FetchAnthropicAsync(string key, long since)
    {
        if (!key.StartsWith("sk-ant-admin", StringComparison.Ordinal))
            return new ApiBalanceFetch(null, "", false, NeedsAdminKey);
        var now = DateTimeOffset.UtcNow;
        var start = since > 0 ? DateTimeOffset.FromUnixTimeSeconds(since) : new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var startText = new DateTimeOffset(start.Year, start.Month, start.Day, 0, 0, 0, TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        double cents = 0;
        string? page = null;
        for (var pages = 0; pages < 24; pages++)
        {
            var url = $"https://api.anthropic.com/v1/organizations/cost_report?starting_at={Uri.EscapeDataString(startText)}&bucket_width=1d&limit=31"
                      + (page is null ? "" : $"&page={Uri.EscapeDataString(page)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Client.SendAsync(request);
            if ((int)response.StatusCode is 401 or 403)
                return new ApiBalanceFetch(null, "", false, BadKey);
            if (!response.IsSuccessStatusCode)
                return new ApiBalanceFetch(null, "", false, Unreachable);
            if (ParseAnthropicPage(await response.Content.ReadAsStringAsync()) is not { } parsed)
                return new ApiBalanceFetch(null, "", false, NotUnderstood);
            cents += parsed.Cents;
            if (parsed.NextPage is null)
                return new ApiBalanceFetch(null, "USD", false, null, Spent: cents / 100);
            page = parsed.NextPage;
        }
        return new ApiBalanceFetch(null, "USD", false, null, Spent: cents / 100);
    }

    /// <summary>Pure. One Cost Report page: the cents in every bucket's results, and the next page if there's more.</summary>
    internal static (double Cents, string? NextPage)? ParseAnthropicPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            double cents = 0;
            foreach (var bucket in data.EnumerateArray())
                if (bucket.ValueKind == JsonValueKind.Object && bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    foreach (var result in results.EnumerateArray())
                        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("amount", out var amount) && Amount(amount) is { } value)
                            cents += value;
            var more = root.TryGetProperty("has_more", out var hasMore) && hasMore.ValueKind == JsonValueKind.True;
            var next = more && root.TryGetProperty("next_page", out var token) && token.GetString() is { Length: > 0 } t ? t : null;
            return (cents, next);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static double? Amount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    /// <summary>
    /// Takes in a new balance. The first one is 100%. A rise is a top-up and is added to 100%, so
    /// what was used before it still counts; a fall is usage. A 100% below the balance can't be
    /// right (a hand-set amount from before a top-up), so it moves up to the balance.
    /// </summary>
    public static void Track(ApiBalanceState state, ApiBalanceFetch fetch, DateTimeOffset now)
    {
        // Spend reported (Anthropic): the balance follows from what was set by hand, when it was.
        if (fetch.Spent is { } spent)
        {
            state.Spent = spent;
            state.Balance = state.Full > 0 ? Math.Max(0, state.Full - spent) : 0;
            state.Currency = fetch.Currency;
            state.Exhausted = false;
            state.UpdatedAt = now.ToUnixTimeSeconds();
            return;
        }
        if (fetch.Balance is not { } balance)
            return;
        if (state.Full <= 0)
            state.Full = balance;
        else if (state.UpdatedAt != 0 && balance > state.Balance + 0.005)
            state.Full += balance - state.Balance;
        state.Full = Math.Max(state.Full, balance);
        state.Balance = balance;
        state.Currency = fetch.Currency;
        state.Exhausted = fetch.Exhausted;
        state.UpdatedAt = now.ToUnixTimeSeconds();
    }

    /// <summary>0–100: how much of the 100% amount has gone. An exhausted balance is 100.</summary>
    public static double UsedPercent(ApiBalanceState state) =>
        state.Exhausted ? 100 : state.Full > 0 ? Math.Clamp((state.Full - state.Balance) / state.Full * 100, 0, 100) : 0;

    /// <summary>"$5.80", or "¥40.12", or "12.00 EUR".</summary>
    public static string Money(double amount, string currency) => currency.ToUpperInvariant() switch
    {
        "USD" => amount.ToString("$0.00", CultureInfo.InvariantCulture),
        "CNY" => amount.ToString("¥0.00", CultureInfo.InvariantCulture),
        _ => $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency}"
    };

    /// <summary>An id not used by another account: the provider's, else "deepseek-2", "deepseek-3"…</summary>
    public static string NewId(string provider, IEnumerable<ApiAccount> accounts)
    {
        var taken = accounts.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(provider))
            return provider;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{provider}-{n}"))
                return $"{provider}-{n}";
    }

    /// <summary>Whether there's a 100% to measure against: always after a balance, only once set by hand for spend.</summary>
    public static bool HasPercent(ApiBalanceState state) => state.Full > 0 || state.Exhausted;

    /// <summary>"42% used · $5.80 left", or with only the spend known, "$12.40 spent since 1 Oct".</summary>
    public static string Summary(ApiBalanceState state) => HasPercent(state)
        ? $"{Math.Round(UsedPercent(state))}% used · {Money(state.Balance, state.Currency)} left"
        : $"{Money(state.Spent ?? 0, state.Currency)} spent since {SinceText(state)}";

    /// <summary>"1 Oct": where spending is counted from.</summary>
    public static string SinceText(ApiBalanceState state)
    {
        var now = DateTimeOffset.Now;
        var since = state.Since > 0 ? DateTimeOffset.FromUnixTimeSeconds(state.Since).ToLocalTime() : new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        return since.ToString("d MMM", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Whether an agent seems to use this provider, to offer linking it: Aider's model is
    /// <c>provider/model</c>, and OpenCode keeps a key per provider in its <c>auth.json</c> (only the
    /// provider names are read, never the keys).
    /// </summary>
    public static bool AgentUses(AgentDefinition agent, string provider)
    {
        var catalog = (agent.Catalog ?? agent.Id).ToLowerInvariant();
        var command = Path.GetFileNameWithoutExtension(agent.Command?.Trim().Trim('"') ?? "").ToLowerInvariant();
        var anthropic = provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase);
        // Claude Code on an API key instead of a plan.
        if (command == "claude")
            return anthropic && agent.Env?.TryGetValue("ANTHROPIC_API_KEY", out var set) == true && !string.IsNullOrWhiteSpace(set);
        if (catalog == "aider" || command == "aider")
            return agent.Model?.Trim() is { Length: > 0 } model
                   && (model.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase)
                       || model.StartsWith(provider + "-", StringComparison.OrdinalIgnoreCase)
                       || (anthropic && model.StartsWith("claude", StringComparison.OrdinalIgnoreCase)));
        if (catalog == "opencode" || command == "opencode")
            return OpenCodeProviders().Contains(provider, StringComparer.OrdinalIgnoreCase);
        // DeepSeek Harness is DeepSeek's own agent; it always wants that key.
        if (catalog == "deepseek" || command == "dsh")
            return provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static List<string> OpenCodeProviders()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "auth.json");
        try
        {
            if (!File.Exists(path))
                return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Select(p => p.Name).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
