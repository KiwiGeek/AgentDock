using System.Text.Json;

namespace AgentDock.Services;

/// <summary>
/// Reads Crush's existing Hyper sign-in only for a balance check. Crush owns renewal of its
/// short-lived OAuth token; touching its refresh token would sign the CLI out.
/// </summary>
public static class CrushHyperToken
{
    public static string? Read()
    {
        try
        {
            var data = Environment.GetEnvironmentVariable("CRUSH_GLOBAL_DATA");
            if (string.IsNullOrWhiteSpace(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "crush");
            var path = Path.Combine(data, "crush.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("providers", out var providers)
                || !providers.TryGetProperty("hyper", out var hyper)
                || !hyper.TryGetProperty("oauth", out var oauth)
                || !oauth.TryGetProperty("expires_at", out var expiry)
                || !expiry.TryGetInt64(out var expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()
                || !oauth.TryGetProperty("access_token", out var token))
                return null;
            return token.GetString() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
