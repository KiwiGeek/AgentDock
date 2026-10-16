using System.Globalization;
using System.Text.Json;
using AgentDock.Models;
using Microsoft.Web.WebView2.Core;

namespace AgentDock.Services;

/// <summary>
/// Grok Build usage, from grok.com. Grok Build's CLI has no usage call, so this asks grok.com the way
/// its own usage page does: <c>POST /grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig</c>
/// (gRPC-web, protobuf). It runs inside an invisible WebView2 page on grok.com, in Agent Dock's own
/// profile (<see cref="DataFolder"/>) where the user signed in (<see cref="GrokSignInWindow"/>), so the
/// sign-in and grok.com's protections are handled as for the website, and Agent Dock never reads a
/// cookie or token. Undocumented: found by watching grok.com's usage page (2026-10-16); any failure
/// is "no data", never an error.
/// </summary>
public static class GrokUsage
{
    public const string Checking = "Checking your Grok limits…";
    public const string NoWindows = "Your Grok usage appears here once grok.com reports it.";
    public const string NotSignedIn = "Sign in to grok.com again to see your usage here.";
    public const string Unreachable = "grok.com couldn't be reached.";
    public const string NotUnderstood = "grok.com's reply wasn't understood.";
    public const string ShowingSaved = "Showing the last saved limits.";

    /// <summary>How long an answer stands before the drawer asks again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Agent Dock's own WebView2 profile: its grok.com sign-in lives here, nowhere else.</summary>
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock", "WebView2");

    private static string UsagePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock", "grok-usage.json");

    /// <summary>Whether the user has opened the sign-in window at least once (not whether they're still signed in).</summary>
    public static bool HasProfile => Directory.Exists(DataFolder);

    /// <summary>
    /// What grok.com's usage chart breaks the week down by (field 7, by number), as seen on the
    /// owner's account. Others show as "Other".
    /// </summary>
    private static readonly Dictionary<ulong, string> Categories = new()
    {
        [2] = "Grok Build", [4] = "Chat", [5] = "Imagine", [9] = "Grok bot", [10] = "Cursor"
    };

    /// <summary>
    /// The request the usage page makes, from a grok.com page so it carries that page's sign-in. The
    /// body is an empty gRPC-web message. The reply comes back as base64 through postMessage, since a
    /// script's own result can't wait for a fetch.
    /// </summary>
    private const string FetchScript = """
        (async () => {
          const send = (status, body, headers) => window.chrome.webview.postMessage(JSON.stringify({ agentDock: true, status, body, headers }));
          try {
            const reply = await fetch('/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig', {
              method: 'POST', credentials: 'include',
              headers: { 'content-type': 'application/grpc-web+proto', 'x-grpc-web': '1', 'accept': 'application/grpc-web+proto' },
              body: new Uint8Array([0, 0, 0, 0, 0]) });
            const bytes = new Uint8Array(await reply.arrayBuffer());
            let text = '';
            for (const b of bytes) text += String.fromCharCode(b);
            send(reply.status, btoa(text), {
              contentType: reply.headers.get('content-type') || '',
              grpcStatus: reply.headers.get('grpc-status') || '',
              grpcMessage: reply.headers.get('grpc-message') || '' });
          } catch (e) {
            send(0, '', {});
          }
        })();
        """;

    /// <summary>
    /// Checks once. Must run on the UI thread: WebView2 is driven from there, through a hidden
    /// controller parented to <paramref name="hwnd"/> and closed when done.
    /// </summary>
    public static async Task<UsageFetch> FetchAsync(nint hwnd)
    {
        if (!HasProfile)
            return new UsageFetch(null, NotSignedIn);

        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", DataFolder, new CoreWebView2EnvironmentOptions());
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
            controller.IsVisible = false;
            var web = controller.CoreWebView2;

            var loaded = new TaskCompletionSource();
            var answered = new TaskCompletionSource<string?>();
            web.NavigationCompleted += (_, _) => loaded.TrySetResult();
            web.WebMessageReceived += (_, args) =>
            {
                try { answered.TrySetResult(args.TryGetWebMessageAsString()); }
                catch (ArgumentException) { answered.TrySetResult(null); }
            };

            // A plain text file on grok.com: the page's origin and sign-in, without loading the app.
            web.Navigate("https://grok.com/robots.txt");
            if (await Task.WhenAny(loaded.Task, Task.Delay(StepTimeout)) != loaded.Task)
                return new UsageFetch(null, Unreachable);
            await web.ExecuteScriptAsync(FetchScript);
            if (await Task.WhenAny(answered.Task, Task.Delay(StepTimeout)) != answered.Task || answered.Task.Result is not { } message)
                return new UsageFetch(null, Unreachable);

            using var json = JsonDocument.Parse(message);
            var status = json.RootElement.GetProperty("status").GetInt32();
            if (status is 401 or 403)
                return new UsageFetch(null, NotSignedIn);
            if (status == 0)
                return new UsageFetch(null, Unreachable);

            string Header(string name) =>
                json.RootElement.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
                && headers.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
            var result = Parse(Convert.FromBase64String(json.RootElement.GetProperty("body").GetString() ?? ""), DateTimeOffset.UtcNow,
                status, Header("contentType"), Header("grpcStatus"), Header("grpcMessage"));
            if (result.Usage is { } usage)
                Save(usage);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new UsageFetch(null, Unreachable);
        }
        finally
        {
            controller?.Close();
        }
    }

    /// <summary>
    /// Signs Agent Dock out of grok.com: clears everything its own WebView2 profile keeps (cookies,
    /// site storage, cache) and deletes the saved usage. The user's browser and Grok Build's own
    /// login aren't touched. UI thread, like <see cref="FetchAsync"/>. False if it couldn't be cleared.
    /// </summary>
    public static async Task<bool> SignOutAsync(nint hwnd)
    {
        if (File.Exists(UsagePath))
            File.Delete(UsagePath);
        if (!HasProfile)
            return true;

        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", DataFolder, new CoreWebView2EnvironmentOptions());
            controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
            controller.IsVisible = false;
            controller.CoreWebView2.CookieManager.DeleteAllCookies();
            await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            controller?.Close();
        }
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
    /// reply isn't gRPC at all. All of those mean "sign in", not "not understood".
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
