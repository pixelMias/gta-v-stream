using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GtaStream.Ebs.Auth;

/// <summary>Who is calling, as the Twitch extension JWT (or a test-server token in the same shape) says.</summary>
public sealed record Viewer(string ChannelId, string OpaqueUserId, string? UserId, string Role, string Name, bool IsDev)
{
    public bool IsBroadcaster => Role == "broadcaster";
    public bool CanDirect => Role is "broadcaster" or "moderator";
    /// <summary>Key for coins: opaque ids stay stable per viewer and extension, also without shared identity.</summary>
    public string Account => OpaqueUserId;
}

/// <summary>
/// HS256 JWTs as Twitch signs them for extensions (secret = Base64-decoded extension secret). Twitch keeps an old
/// secret valid for about an hour after a new one is created, so several secrets are accepted.
/// </summary>
public sealed class ViewerTokens
{
    private readonly byte[][] _twitchSecrets;
    private readonly byte[]? _devSecret;
    private readonly TimeProvider _time;

    public ViewerTokens(IEnumerable<byte[]> twitchSecrets, byte[]? devSecret, TimeProvider time)
    {
        _twitchSecrets = twitchSecrets.ToArray();
        _devSecret = devSecret;
        _time = time;
    }

    public bool DevLoginEnabled => _devSecret != null;

    /// <summary>
    /// GTASTREAM_TWITCH_SECRETS: Base64 extension secrets, comma separated. GTASTREAM_DEV_LOGIN=off disables test logins;
    /// otherwise their key is derived from the admin token (stable across restarts) or random.
    /// </summary>
    public static ViewerTokens FromConfig(IConfiguration config, TimeProvider time)
    {
        var secrets = (config["GTASTREAM_TWITCH_SECRETS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Convert.FromBase64String);
        byte[]? dev = null;
        if (!string.Equals(config["GTASTREAM_DEV_LOGIN"], "off", StringComparison.OrdinalIgnoreCase))
        {
            var admin = config["GTASTREAM_ADMIN_TOKEN"];
            dev = string.IsNullOrEmpty(admin)
                ? RandomNumberGenerator.GetBytes(32)
                : HMACSHA256.HashData(Encoding.UTF8.GetBytes(admin), "gtastream-dev-login"u8.ToArray());
        }
        return new ViewerTokens(secrets, dev, time);
    }

    public Viewer? Verify(string? authorizationHeader)
    {
        if (authorizationHeader is null || !authorizationHeader.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = authorizationHeader["Bearer ".Length..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        JsonObject header, payload;
        try
        {
            header = JsonNode.Parse(Base64Url.Decode(parts[0]))!.AsObject();
            payload = JsonNode.Parse(Base64Url.Decode(parts[1]))!.AsObject();
        }
        catch (Exception) { return null; }
        if ((string?)header["alg"] != "HS256") return null;

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        byte[] signature;
        try { signature = Base64Url.Decode(parts[2]); } catch (FormatException) { return null; }

        var isDev = payload["gtastream_dev"]?.GetValue<bool>() == true;
        var keys = isDev ? (_devSecret is null ? [] : new[] { _devSecret }) : _twitchSecrets;
        if (!keys.Any(k => CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(k, signed), signature))) return null;

        var exp = payload["exp"]?.GetValue<long>() ?? 0;
        if (exp < _time.GetUtcNow().ToUnixTimeSeconds()) return null;

        var channel = (string?)payload["channel_id"];
        var opaque = (string?)payload["opaque_user_id"];
        var role = (string?)payload["role"] ?? "viewer";
        if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(opaque)) return null;
        var userId = (string?)payload["user_id"];
        var name = (string?)payload["gtastream_name"] ?? DefaultName(opaque, userId);
        return new Viewer(channel, opaque, string.IsNullOrEmpty(userId) ? null : userId, role, name, isDev);
    }

    /// <summary>Without a Helix lookup the EBS knows no display name; a shared identity at least gets a stable tag.</summary>
    public static string DefaultName(string opaque, string? userId) =>
        string.IsNullOrEmpty(userId) ? "Anonymer Hacker" : $"Hacker #{userId[^Math.Min(4, userId.Length)..]}";

    /// <summary>Test-server login in the shape of a Twitch token, marked so it is never checked against a Twitch secret.</summary>
    public string IssueDev(string channelId, string opaqueUserId, string name, string role, TimeSpan lifetime)
    {
        if (_devSecret is null) throw new InvalidOperationException("Test-Login ist aus.");
        var payload = new JsonObject
        {
            ["exp"] = _time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds(),
            ["opaque_user_id"] = opaqueUserId,
            ["user_id"] = opaqueUserId,
            ["channel_id"] = channelId,
            ["role"] = role,
            ["gtastream_dev"] = true,
            ["gtastream_name"] = name,
        };
        return Sign(payload, _devSecret);
    }

    public static string Sign(JsonObject payload, byte[] secret)
    {
        var head = Base64Url.Encode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray());
        var body = Base64Url.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var sig = Base64Url.Encode(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(head + "." + body)));
        return head + "." + body + "." + sig;
    }
}

public static class Base64Url
{
    public static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
