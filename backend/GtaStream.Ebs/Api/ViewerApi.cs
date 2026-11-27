using System.Security.Cryptography;
using System.Text;
using GtaStream.Ebs.Auth;
using GtaStream.Ebs.Economy;
using GtaStream.Ebs.Studio;

namespace GtaStream.Ebs.Api;

public sealed record StartRequest(bool Wide);
public sealed record FinishRequest(Guid RoundId, int[] Taps);
public sealed record PurchaseRequest(string EventId);

/// <summary>
/// Everything the extension frontend calls. Each call carries the Twitch extension JWT as "Authorization: Bearer";
/// on the test server a test login in the same shape stands in. Personal data comes in the responses; the public
/// status is polled (later also broadcast via Twitch PubSub, at most once a second).
/// </summary>
public static class ViewerApi
{
    private static readonly string[] NameParts1 = ["Neon", "Null", "Byte", "Glitch", "Turbo", "Pixel", "Proxy", "Schatten", "Daten", "Krypto"];
    private static readonly string[] NameParts2 = ["Fuchs", "Wolf", "Geist", "Rabe", "Otter", "Hai", "Luchs", "Kobold", "Falke", "Dachs"];

    public static IResult ToResult(object value) => value switch
    {
        ApiError e => Results.Json(new { error = e.Code, message = e.Message, retryAfterSec = e.RetryAfterSec }, statusCode: e.Status),
        _ => Results.Ok(value),
    };

    private static IResult WithViewer(HttpRequest req, ViewerTokens tokens, Func<Viewer, IResult> f)
    {
        var v = tokens.Verify(req.Headers.Authorization.ToString());
        return v is null ? Results.Json(new { error = "unauthorized", message = "Anmeldung fehlt oder ist abgelaufen." }, statusCode: 401) : f(v);
    }

    public static void MapViewerApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        var config = app.Configuration;

        // Test-server login: renews a valid token with the same identity, otherwise makes a new viewer.
        // With the admin token in X-Admin-Token the login gets the broadcaster role (director console).
        api.MapPost("/dev/session", (HttpRequest req, ViewerTokens tokens, StudioHub hub) =>
        {
            if (!tokens.DevLoginEnabled) return Results.NotFound();
            var old = tokens.Verify(req.Headers.Authorization.ToString());
            var admin = config["GTASTREAM_ADMIN_TOKEN"];
            var given = req.Headers["X-Admin-Token"].ToString();
            var isAdmin = !string.IsNullOrEmpty(admin) && given.Length > 0 &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(admin));
            var opaque = old is { IsDev: true } ? old.OpaqueUserId : "Udev" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var name = old is { IsDev: true } ? old.Name
                : $"{NameParts1[Random.Shared.Next(NameParts1.Length)]}-{NameParts2[Random.Shared.Next(NameParts2.Length)]}-{Random.Shared.Next(10, 100)}";
            var role = isAdmin ? "broadcaster" : old is { IsDev: true } ? old.Role : "viewer";
            var token = tokens.IssueDev(hub.ChannelId, opaque, name, role, TimeSpan.FromDays(30));
            return Results.Ok(new { token, channelId = hub.ChannelId, name, role });
        });

        api.MapGet("/me", (HttpRequest req, ViewerTokens tokens, Show show) => WithViewer(req, tokens, v => ToResult(show.Me(v))));

        api.MapGet("/status", (HttpRequest req, ViewerTokens tokens, Show show, StudioHub hub) =>
            WithViewer(req, tokens, v => ToResult(show.Status(v.ChannelId, hub.Describe(v.ChannelId)))));

        api.MapGet("/catalog", (HttpRequest req, ViewerTokens tokens, Show show) => WithViewer(req, tokens, v => ToResult(show.CatalogFor(v))));

        api.MapPost("/games/firewall/start", (HttpRequest req, StartRequest? body, ViewerTokens tokens, Show show) =>
            WithViewer(req, tokens, v => ToResult(show.StartRound(v, body?.Wide == true))));

        api.MapPost("/games/firewall/finish", (HttpRequest req, FinishRequest body, ViewerTokens tokens, Show show) =>
            WithViewer(req, tokens, v => body.Taps is null || body.Taps.Length > 400
                ? ToResult(new ApiError(400, "bad_request", "Ungültige Runde."))
                : ToResult(show.FinishRound(v, body.RoundId, body.Taps))));

        api.MapPost("/purchase", (HttpRequest req, PurchaseRequest body, ViewerTokens tokens, Show show) =>
            WithViewer(req, tokens, v => ToResult(show.Buy(v, body.EventId ?? ""))));

        // Director console (broadcaster and moderators): panic button refunds everything waiting and stops the game side.
        api.MapPost("/director/panic", async (HttpRequest req, ViewerTokens tokens, Show show, StudioHub hub) =>
        {
            var v = tokens.Verify(req.Headers.Authorization.ToString());
            if (v is null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            if (!v.CanDirect) return Results.Json(new { error = "forbidden", message = "Nur für Streamer und Moderatoren." }, statusCode: 403);
            var refunded = show.Panic(v.ChannelId);
            var sent = await hub.SendPanic(v.ChannelId);
            app.Logger.LogWarning("Panic von {Name}: {Count} erstattet, Studio benachrichtigt: {Sent}", v.Name, refunded, sent);
            return Results.Ok(new { refunded, studioNotified = sent });
        });
    }

    public static void MapStudioApi(this WebApplication app)
    {
        app.Map("/api/v1/studio", async (HttpContext ctx, StudioHub hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { error = "websocket_required" });
            if (!hub.Authorize(ctx.Request.Headers.Authorization.ToString())) return Results.Unauthorized();
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.Run(socket, ctx.RequestAborted);
            return Results.Empty;
        });

        app.MapGet("/api/v1/studio/info", (StudioHub hub) => Results.Ok(hub.Describe(hub.ChannelId)));
    }
}
