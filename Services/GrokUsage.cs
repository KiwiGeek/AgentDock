using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Grok Build usage, from grok.com. Grok Build's CLI has no usage call, so this asks grok.com the way
/// its own usage page does: <c>POST /grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig</c>
/// (gRPC-web, protobuf), with Grok Build's own sign-in: the x.ai session in <c>~\.grok\auth.json</c>
/// as a bearer token (owner's decision, 2026-10-16; grok.com accepts it). Undocumented: found by
/// watching grok.com's usage page; any failure is "no data", never an error.
/// </summary>
/// <remarks>
/// The token is read-only to Agent Dock: it's never refreshed (Grok Build does that, and a refresh
/// here would race it), the file is never written, and the token is never logged, shown or saved.
/// An expired token sends nothing. It's short-lived (hours), so the drawer can fall back to the last
/// saved numbers until Grok Build next runs.
/// </remarks>
public static class GrokUsage
{
    public const string Checking = "Checking your Grok limits…";
    public const string NoWindows = "Your Grok usage appears here once grok.com reports it.";
    public const string NotSignedIn = "Sign in to Grok Build, then open this again.";
    public const string SignInExpired = "Grok Build's sign-in has expired. Run Grok Build to renew it.";
    public const string Unreachable = "grok.com couldn't be reached.";
    public const string NotUnderstood = "grok.com's reply wasn't understood.";
    public const string ShowingSaved = "Showing the last saved limits.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string UsagePath { get; } =
        Path.Combine(AppIdentity.SharedLocalFolder, "grok-usage.json");

    /// <summary>Grok Build's sign-in: one entry per x.ai session, each with a <c>key</c> and <c>expires_at</c>.</summary>
    private static string AuthPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json");

    /// <summary>
    /// What grok.com's usage chart breaks the week down by (field 7, by number), as seen on the
    /// owner's account. Others show as "Other".
    /// </summary>
    private static readonly Dictionary<ulong, string> Categories = new()
    {
        [2] = "Grok Build", [4] = "Chat", [5] = "Imagine", [9] = "Grok bot", [10] = "Cursor"
    };

    /// <summary>Checks once, off the UI thread. A successful answer is saved.</summary>
    public static Task<UsageFetch> FetchAsync() => Task.Run(FetchCoreAsync);

    private static async Task<UsageFetch> FetchCoreAsync()
    {
        try
        {
            var (token, problem) = ReadToken(DateTimeOffset.UtcNow);
            if (token is null)
                return new UsageFetch(null, problem);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
            request.Headers.Accept.ParseAdd("application/grpc-web+proto");
            // An empty gRPC-web message: a data frame of length 0.
            request.Content = new ByteArrayContent([0, 0, 0, 0, 0]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
            token = null;

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            var body = await response.Content.ReadAsByteArrayAsync();
            string Header(string name) =>
                response.Headers.TryGetValues(name, out var values) || response.TrailingHeaders.TryGetValues(name, out values)
                    ? values.FirstOrDefault() ?? ""
                    : "";
            var result = Parse(body, DateTimeOffset.UtcNow, (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "", Header("grpc-status"), Header("grpc-message"));
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
    /// Grok Build's current x.ai session token from auth.json, or why there isn't one. Of several
    /// sessions, the one that expires last. Only <c>key</c> and <c>expires_at</c> are read.
    /// </summary>
    private static (string? Token, string? Problem) ReadToken(DateTimeOffset now)
    {
        if (!File.Exists(AuthPath))
            return (null, NotSignedIn);
        string json;
        // Grok Build may be rewriting it at the same moment.
        using (var stream = new FileStream(AuthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return (null, NotSignedIn);
        string? best = null;
        var bestExpiry = DateTimeOffset.MinValue;
        foreach (var session in doc.RootElement.EnumerateObject())
        {
            if (session.Value.ValueKind != JsonValueKind.Object
                || !session.Value.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String
                || key.GetString() is not { Length: > 0 } value)
                continue;
            var expiry = session.Value.TryGetProperty("expires_at", out var at) && at.ValueKind == JsonValueKind.String
                         && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MaxValue; // no expiry given: let grok.com decide
            if (best is null || expiry > bestExpiry)
                (best, bestExpiry) = (value, expiry);
        }
        if (best is null)
            return (null, NotSignedIn);
        return bestExpiry <= now.AddSeconds(30) ? (null, SignInExpired) : (best, null);
    }

    /// <summary>
    /// Pure. A gRPC-web reply: frames (a flag byte, then a 4-byte length), data first, then a trailer
    /// with <c>grpc-status</c>. The message's field 1 holds the week:
    /// 1 = percent used (float), 4 / 5 = the window's start and end (Timestamp), 7 = the breakdown
    /// (1 = category, 2 = share, float), 12.1 = the extra usage credit left, in cents
    /// (it went from 372 to 359 as the owner used some).
    /// </summary>
    /// <remarks>
    /// Signed out, grok.com sends no message, and the status can come in the response headers
    /// (<paramref name="headerStatus"/>, <paramref name="headerMessage"/>) instead of a trailer, or the
    /// reply isn't gRPC at all. All of those mean "sign in", not "not understood". (Seen through the
    /// browser sign-in this replaced; with a bearer token, a rejected one gets 401.)
    /// </remarks>
    internal static UsageFetch Parse(byte[] reply, DateTimeOffset now, int httpStatus = 200,
        string contentType = "application/grpc-web+proto", string headerStatus = "", string headerMessage = "")
    {
        if (httpStatus is 401 or 403)
            return new UsageFetch(null, NotSignedIn);
        if (httpStatus >= 500)
            return new UsageFetch(null, Unreachable);
        // Not gRPC at all (a sign-in page or a redirect): the page wasn't signed in.
        if (contentType.Length > 0 && !contentType.Contains("grpc", StringComparison.OrdinalIgnoreCase))
            return new UsageFetch(null, NotSignedIn);

        try
        {
            byte[]? message = null;
            var grpcStatus = int.TryParse(headerStatus, CultureInfo.InvariantCulture, out var fromHeader) ? fromHeader : 0;
            var grpcMessage = headerMessage;
            for (var at = 0; at + 5 <= reply.Length;)
            {
                var flag = reply[at];
                var length = (reply[at + 1] << 24) | (reply[at + 2] << 16) | (reply[at + 3] << 8) | reply[at + 4];
                if (length < 0 || at + 5 + length > reply.Length)
                    return new UsageFetch(null, NotUnderstood);
                var frame = reply.AsSpan(at + 5, length);
                if ((flag & 0x80) != 0)
                {
                    foreach (var line in System.Text.Encoding.ASCII.GetString(frame).Split('\n'))
                        if (line.StartsWith("grpc-status:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line["grpc-status:".Length..].Trim(), CultureInfo.InvariantCulture, out grpcStatus);
                        else if (line.StartsWith("grpc-message:", StringComparison.OrdinalIgnoreCase))
                            grpcMessage = line["grpc-message:".Length..].Trim();
                }
                else
                {
                    message ??= frame.ToArray();
                }
                at += 5 + length;
            }

            // 16 is UNAUTHENTICATED, 7 PERMISSION_DENIED; some servers only say so in the message.
            if (grpcStatus is 16 or 7
                || grpcMessage.Contains("auth", StringComparison.OrdinalIgnoreCase)
                || grpcMessage.Contains("sign", StringComparison.OrdinalIgnoreCase)
                || grpcMessage.Contains("login", StringComparison.OrdinalIgnoreCase))
                return new UsageFetch(null, NotSignedIn);
            if (grpcStatus != 0)
                return new UsageFetch(null, NotUnderstood);
            // Nothing at all, and no error: what grok.com sends to a page that isn't signed in.
            if (message is null)
                return new UsageFetch(null, NotSignedIn);

            var week = Field(message, 1);
            if (week is null)
                return new UsageFetch(new PlanUsage { UpdatedAt = now.ToUnixTimeSeconds() }, null);

            var usage = new PlanUsage { UpdatedAt = now.ToUnixTimeSeconds() };
            if (Fixed32(week, 1) is { } used)
                usage.SevenDay = new UsageWindow
                {
                    UsedPercentage = used,
                    ResetsAt = Field(week, 5) is { } end && Varint(end, 1) is { } seconds ? (long)seconds : 0
                };

            var shares = new List<UsageShare>();
            foreach (var part in Fields(week, 7))
                if (Fixed32(part, 2) is { } share)
                    shares.Add(new UsageShare
                    {
                        Label = Varint(part, 1) is { } category && Categories.TryGetValue(category, out var name) ? name : "Other",
                        Percent = share
                    });
            usage.Breakdown = shares.Count > 0 ? shares : null;

            if (Field(week, 12) is { } credit && Varint(credit, 1) is { } cents && cents > 0)
                usage.Extra = new ExtraUsage
                {
                    Enabled = true,
                    Balance = (cents / 100.0).ToString("C2", CultureInfo.GetCultureInfo("en-US"))
                };
            return new UsageFetch(usage, null);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return new UsageFetch(null, NotUnderstood);
        }
    }

    // A minimal protobuf reader: enough to pick fields out by number.

    private static IEnumerable<(ulong Field, int Type, ulong Value, byte[]? Bytes)> Read(byte[] message)
    {
        var list = new List<(ulong, int, ulong, byte[]?)>();
        for (var at = 0; at < message.Length;)
        {
            var key = ReadVarint(message, ref at);
            var type = (int)(key & 7);
            switch (type)
            {
                case 0: list.Add((key >> 3, 0, ReadVarint(message, ref at), null)); break;
                case 1: list.Add((key >> 3, 1, BitConverter.ToUInt64(message, at), null)); at += 8; break;
                case 5: list.Add((key >> 3, 5, BitConverter.ToUInt32(message, at), null)); at += 4; break;
                case 2:
                    var length = (int)ReadVarint(message, ref at);
                    list.Add((key >> 3, 2, 0, message.AsSpan(at, length).ToArray()));
                    at += length;
                    break;
                default: throw new ArgumentException("unknown wire type");
            }
        }
        return list;
    }

    private static ulong ReadVarint(byte[] data, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = data[at++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
    }

    private static byte[]? Field(byte[] message, ulong number) => Fields(message, number).FirstOrDefault();

    private static IEnumerable<byte[]> Fields(byte[] message, ulong number) =>
        Read(message).Where(f => f.Field == number && f.Type == 2).Select(f => f.Bytes!);

    private static ulong? Varint(byte[] message, ulong number) =>
        Read(message).Where(f => f.Field == number && f.Type == 0).Select(f => (ulong?)f.Value).FirstOrDefault();

    private static float? Fixed32(byte[] message, ulong number) =>
        Read(message).Where(f => f.Field == number && f.Type == 5)
            .Select(f => (float?)BitConverter.UInt32BitsToSingle((uint)f.Value)).FirstOrDefault();

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
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, DockJsonContext.Default.PlanUsage));
        File.Move(temp, UsagePath, overwrite: true);
    }
}
