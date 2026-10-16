using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Cursor plan usage, the numbers cursor.com's dashboard shows: <c>GET https://cursor.com/api/usage-summary</c>,
/// signed in as the Cursor CLI is (its access token from <c>%APPDATA%\cursor\auth.json</c>, sent as the
/// dashboard's session cookie). Owner's decision, 2026-10-16. Undocumented; any failure is "no data".
/// One account for the CLI and the desktop app, so both drawers show it.
/// </summary>
/// <remarks>
/// The token is read-only to Agent Dock: never refreshed (the CLI does that, and its refresh token
/// is single-use), the file is never written, and the token is never logged, shown or saved. An
/// expired token sends nothing. Cursor's own <c>api2.cursor.sh</c> <c>DashboardService/GetCurrentPeriodUsage</c>
/// gives the same percentages with a bearer token, but not the plan or on-demand spend, so it isn't used.
/// </remarks>
public static class CursorUsage
{
    public const string Checking = "Checking your Cursor usage…";
    public const string NoWindows = "Your Cursor usage appears here once Cursor reports it.";
    public const string NotSignedIn = "Sign in to Cursor CLI (cursor-agent login), then open this again.";
    public const string SignInExpired = "Cursor CLI's sign-in has expired. Run Cursor CLI to renew it.";
    public const string Unreachable = "cursor.com couldn't be reached.";
    public const string NotUnderstood = "cursor.com's reply wasn't understood.";
    public const string ShowingSaved = "Showing the last saved usage.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    // The session cookie is set by hand, which a handler with its own cookie jar would drop.
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private static string UsagePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock", "cursor-usage.json");

    private static string AuthPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cursor", "auth.json");

    /// <summary>Checks once, off the UI thread. A successful answer is saved.</summary>
    public static Task<UsageFetch> FetchAsync() => Task.Run(FetchCoreAsync);

    private static async Task<UsageFetch> FetchCoreAsync()
    {
        try
        {
            var (cookie, problem) = ReadSessionCookie(DateTimeOffset.UtcNow);
            if (cookie is null)
                return new UsageFetch(null, problem);

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.Accept.ParseAdd("application/json");
            cookie = null;

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            if ((int)response.StatusCode is 401 or 403)
                return new UsageFetch(null, NotSignedIn);
            if (!response.IsSuccessStatusCode)
                return new UsageFetch(null, Unreachable);
            var result = Parse(await response.Content.ReadAsStringAsync(), DateTimeOffset.UtcNow);
            if (result.Usage is { } usage)
                Save(usage);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new UsageFetch(null, Unreachable);
        }
    }

    /// <summary>
    /// The dashboard's session cookie, <c>WorkosCursorSessionToken=&lt;user id&gt;::&lt;access token&gt;</c>
    /// (URL-encoded), built from the CLI's sign-in, or why there isn't one. The user id is the part of
    /// the token's <c>sub</c> after the "|"; <c>exp</c> says whether it's still good.
    /// </summary>
    private static (string? Cookie, string? Problem) ReadSessionCookie(DateTimeOffset now)
    {
        if (!File.Exists(AuthPath))
            return (null, NotSignedIn);
        string json;
        using (var stream = new FileStream(AuthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("accessToken", out var access) || access.GetString() is not { Length: > 0 } token)
            return (null, NotSignedIn);

        var parts = token.Split('.');
        if (parts.Length != 3)
            return (null, NotSignedIn);
        using var claims = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = claims.RootElement;
        if (root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var expires)
            && DateTimeOffset.FromUnixTimeSeconds(expires) <= now.AddSeconds(30))
            return (null, SignInExpired);
        if (!root.TryGetProperty("sub", out var sub) || sub.GetString() is not { Length: > 0 } subject)
            return (null, NotSignedIn);
        var userId = subject[(subject.LastIndexOf('|') + 1)..];
        return ($"WorkosCursorSessionToken={Uri.EscapeDataString(userId)}%3A%3A{token}", null);
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    /// <summary>
    /// Pure. The reply, as seen 2026-10-16 (Pro+): <c>billingCycleEnd</c> (ISO 8601), <c>membershipType</c>
    /// ("pro_plus"), and <c>individualUsage.plan</c> with <c>totalPercentUsed</c>, <c>autoPercentUsed</c>
    /// and <c>apiPercentUsed</c> (0–100; the dashboard's Total, Auto + Composer and API) plus amounts
    /// in cents. <c>individualUsage.onDemand</c> (<c>enabled</c>, <c>used</c>, <c>limit</c>, cents) is
    /// spend beyond the plan.
    /// </summary>
    internal static UsageFetch Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new UsageFetch(null, NotUnderstood);

            var usage = new PlanUsage { UpdatedAt = now.ToUnixTimeSeconds() };
            if (root.TryGetProperty("membershipType", out var membership) && membership.GetString() is { Length: > 0 } plan)
                usage.Plan = plan.Replace("_plus", "+", StringComparison.OrdinalIgnoreCase);

            var resets = root.TryGetProperty("billingCycleEnd", out var end) && end.ValueKind == JsonValueKind.String
                         && DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToUnixTimeSeconds()
                : 0;

            if (root.TryGetProperty("individualUsage", out var individual) && individual.ValueKind == JsonValueKind.Object)
            {
                if (individual.TryGetProperty("plan", out var planUsage) && planUsage.ValueKind == JsonValueKind.Object)
                {
                    if (Number(planUsage, "totalPercentUsed") is { } total)
                        usage.SevenDay = new UsageWindow { UsedPercentage = total, ResetsAt = resets };
                    var parts = new List<UsageWindow>();
                    if (Number(planUsage, "autoPercentUsed") is { } auto)
                        parts.Add(new UsageWindow { Title = "Auto + Composer", UsedPercentage = auto, ResetsAt = resets });
                    if (Number(planUsage, "apiPercentUsed") is { } api)
                        parts.Add(new UsageWindow { Title = "API", UsedPercentage = api, ResetsAt = resets });
                    usage.ModelWeeks = parts.Count > 0 ? parts : null;
                }
                if (individual.TryGetProperty("onDemand", out var onDemand) && onDemand.ValueKind == JsonValueKind.Object
                    && onDemand.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                    usage.Extra = new ExtraUsage
                    {
                        Enabled = true,
                        Dollars = true,
                        Used = Number(onDemand, "used") / 100,
                        Limit = Number(onDemand, "limit") / 100
                    };
            }
            return new UsageFetch(usage, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new UsageFetch(null, NotUnderstood);
        }
    }

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>The last answer saved, or null if there's none yet.</summary>
    public static PlanUsage? LoadSaved()
    {
        try
        {
            return File.Exists(UsagePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(UsagePath), DockJsonContext.Default.PlanUsage)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save(PlanUsage usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UsagePath)!);
        var temp = $"{UsagePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, DockJsonContext.Default.PlanUsage), Encoding.UTF8);
        File.Move(temp, UsagePath, overwrite: true);
    }
}
