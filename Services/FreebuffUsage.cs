using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Freebuff's daily hours, from the same session endpoint its CLI reads (GET /api/v1/freebuff/session).
/// One hour is five freebucks on the wire. The sign-in is Freebuff CLI's own: Agent Dock reads it
/// from Windows Credential Manager (or, from older CLIs, credentials.json) read-only, never refreshes
/// it, never writes it, and never logs, shows or saves the token. Undocumented, so any failure is
/// "no data".
/// </summary>
public static partial class FreebuffUsage
{
    public const string Checking = "Checking your Freebuff hours…";
    public const string NoWindows = "Your Freebuff hours appear here once Freebuff reports them.";
    public const string NotSignedIn = "Sign in to Freebuff (freebuff login), then open this again.";
    public const string SignInExpired = "Freebuff's sign-in has expired. Run Freebuff to renew it.";
    public const string Unreachable = "codebuff.com couldn't be reached.";
    public const string NotUnderstood = "codebuff.com's reply wasn't understood.";
    public const string ShowingSaved = "Showing the last saved usage.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    // codebuff.com redirects here; .NET's HttpClient drops Authorization on a redirect, which
    // the API reads as signed out. Go straight to the host that serves the endpoint.
    private const string SessionUrl = "https://www.codebuff.com/api/v1/freebuff/session";
    private const double FreebucksPerHour = 5;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string DefaultConfigDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "manicode");

    /// <summary>
    /// The CLI's config folder for an agent: its own <c>FREEBUFF_CONFIG_DIR</c> when set, else Agent
    /// Dock's environment, else the default. Copies of Freebuff can therefore show their own accounts.
    /// </summary>
    public static string ConfigDirFor(AgentDefinition? definition)
    {
        var configured = definition?.Env?
            .FirstOrDefault(pair => string.Equals(pair.Key, "FREEBUFF_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)).Value;
        configured ??= Environment.GetEnvironmentVariable("FREEBUFF_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'))));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // The CLI refuses a bad folder too; fall back to the account Agent Dock can read.
            }
        }
        return DefaultConfigDir;
    }

    /// <summary>Checks once, off the UI thread. A successful answer is saved.</summary>
    public static Task<UsageFetch> FetchAsync(string configDir) => Task.Run(() => FetchCoreAsync(configDir));

    private static async Task<UsageFetch> FetchCoreAsync(string configDir)
    {
        try
        {
            var token = ReadToken(configDir);
            if (token is null)
                return new UsageFetch(null, NotSignedIn);

            using var request = new HttpRequestMessage(HttpMethod.Get, SessionUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.Accept.ParseAdd("application/json");
            token = null;

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            if ((int)response.StatusCode is 401 or 403)
                return new UsageFetch(null, SignInExpired);
            if (!response.IsSuccessStatusCode)
                return new UsageFetch(null, response.StatusCode == System.Net.HttpStatusCode.NotFound ? NotUnderstood : Unreachable);

            var result = Parse(await response.Content.ReadAsStringAsync(), DateTimeOffset.UtcNow);
            if (result.Usage is { } usage)
                Save(usage, configDir);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new UsageFetch(null, Unreachable);
        }
    }

    /// <summary>
    /// The token Freebuff CLI stored: an older CLI kept <c>authToken</c> in credentials.json; current
    /// ones keep it in Windows Credential Manager under <c>freebuff-cli/&lt;credentials path&gt;</c>.
    /// </summary>
    private static string? ReadToken(string configDir)
    {
        var credentialsPath = Path.Combine(configDir, "credentials.json");
        // The keychain is authoritative: a file left over from an older CLI may still hold the
        // token it had before signing in again.
        if (CredentialManager.Read("freebuff-cli/" + credentialsPath) is { } keychain)
            return keychain;
        try
        {
            if (!File.Exists(credentialsPath))
                return null;
            using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            // The current file keeps the profile under "default"; very old ones may have it at
            // the root.
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("authToken", out var auth)
                    && auth.GetString() is { Length: > 0 } stored)
                    return stored;
                if (root.TryGetProperty("default", out var profile) && profile.ValueKind == JsonValueKind.Object
                    && profile.TryGetProperty("authToken", out var defaultAuth)
                    && defaultAuth.GetString() is { Length: > 0 } defaultStored)
                    return defaultStored;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // No file, or one being written: report the sign-in as missing.
        }
        return null;
    }

    /// <summary>
    /// Pure. The reply as seen 2026-10-23: <c>freebucks.daily</c> with <c>limit</c>, <c>spent</c>,
    /// <c>remaining</c> (freebucks, five to an hour) and <c>resetAt</c> (ISO 8601). <c>status</c> is
    /// "none" when no session is running, which still carries the day's numbers. The plan name is
    /// the paid tier, else Free.
    /// </summary>
    internal static UsageFetch Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new UsageFetch(null, NotUnderstood);

            if (root.TryGetProperty("status", out var status) && status.GetString() is "country_blocked" or "banned")
                return new UsageFetch(null, "Freebuff says this account can't use the service.");

            if (!root.TryGetProperty("freebucks", out var freebucks) || freebucks.ValueKind != JsonValueKind.Object
                || !freebucks.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object)
                return new UsageFetch(null, NoWindows);

            var limit = Number(daily, "limit");
            var remaining = Number(daily, "remaining");
            if (limit is not > 0 || remaining is null)
                return new UsageFetch(null, NoWindows);
            var spent = Number(daily, "spent") ?? Math.Max(0, limit.Value - remaining.Value);
            var resets = ResetAt(daily);

            var usage = new PlanUsage
            {
                UpdatedAt = now.ToUnixTimeSeconds(),
                Plan = PlanName(root),
                SevenDay = new UsageWindow
                {
                    UsedPercentage = Math.Clamp(spent * 100 / limit.Value, 0, 100),
                    ResetsAt = resets
                },
                Allowance = new UsageAllowance
                {
                    Remaining = Math.Max(0, remaining.Value) / FreebucksPerHour,
                    Limit = limit.Value / FreebucksPerHour,
                    Unit = "hours",
                    ResetsAt = resets
                }
            };
            return new UsageFetch(usage, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new UsageFetch(null, NotUnderstood);
        }
    }

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static long ResetAt(JsonElement daily) =>
        daily.TryGetProperty("resetAt", out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at.ToUnixTimeSeconds()
            : 0;

    private static string PlanName(JsonElement root)
    {
        if (root.TryGetProperty("subscription", out var subscription) && subscription.ValueKind == JsonValueKind.Object
            && subscription.TryGetProperty("tierId", out var tier) && tier.ValueKind == JsonValueKind.String
            && tier.GetString() is { Length: > 0 } id)
            return id;
        return "free";
    }

    /// <summary>Hours shown in the drawer and overview, e.g. "30" or "24.5".</summary>
    public static string FormatHours(double hours) => hours.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>The last answer saved for this account, or null if there's none yet.</summary>
    public static PlanUsage? LoadSaved(string configDir)
    {
        try
        {
            var path = UsageFileFor(configDir);
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), DockJsonContext.Default.PlanUsage)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string UsageFileFor(string configDir) =>
        UsageAccount.UsageFile("freebuff-usage", configDir, DefaultConfigDir);

    private static void Save(PlanUsage usage, string configDir)
    {
        var path = UsageFileFor(configDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, DockJsonContext.Default.PlanUsage), Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Windows Credential Manager, through the same generic credentials Bun.secrets writes. The target
    /// is <c>freebuff-cli/&lt;credentials path&gt;</c>. Bun stored the blob as UTF-8 here; the decoder
    /// accepts UTF-16LE too, in case another runtime or version writes it that way.
    /// </summary>
    private static partial class CredentialManager
    {
        private const uint GenericCredential = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public nint TargetName;
            public nint Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public nint CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public nint Attributes;
            public nint TargetAlias;
            public nint UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredRead(string target, uint type, uint flags, out nint credential);

        [LibraryImport("advapi32.dll")]
        private static partial void CredFree(nint buffer);

        public static string? Read(string target)
        {
            if (!CredRead(target, GenericCredential, 0, out var buffer) || buffer == 0)
                return null;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(buffer);
                if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0)
                    return null;
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Decode(bytes);
            }
            finally
            {
                CredFree(buffer);
            }
        }

        private static string? Decode(byte[] blob)
        {
            var text = Encoding.Unicode.GetString(blob).TrimEnd('\0');
            if (IsToken(text))
                return text;
            text = Encoding.UTF8.GetString(blob).TrimEnd('\0');
            return IsToken(text) ? text : null;
        }

        private static bool IsToken(string text) =>
            text.Length >= 8 && text.All(c => c is >= '!' and <= '~');
    }
}
