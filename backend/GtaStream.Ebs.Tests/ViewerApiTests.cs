using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GtaStream.Ebs.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GtaStream.Ebs.Tests;

public class ViewerApiTests
{
    private static readonly byte[] TwitchSecret = Encoding.UTF8.GetBytes("twitch-extension-secret-for-tests");

    private static WebApplicationFactory<Program> Factory(string sim = "on") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("GTASTREAM_ADMIN_TOKEN", "geheim");
            b.UseSetting("GTASTREAM_TWITCH_SECRETS", Convert.ToBase64String(TwitchSecret));
            b.UseSetting("GTASTREAM_CHANNEL_ID", "12345");
            b.UseSetting("GTASTREAM_STUDIO_SIM", sim);
        });

    private static string TwitchToken(string opaque, string channel = "12345", long? exp = null, byte[]? secret = null) =>
        ViewerTokens.Sign(new JsonObject
        {
            ["exp"] = exp ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["opaque_user_id"] = opaque,
            ["channel_id"] = channel,
            ["role"] = "viewer",
        }, secret ?? TwitchSecret);

    private static HttpClient As(WebApplicationFactory<Program> f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Twitch_tokens_are_checked()
    {
        using var f = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(f, TwitchToken("U1", secret: "x"u8.ToArray())).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(f, TwitchToken("U1", exp: 1000)).GetAsync("/api/v1/me")).StatusCode);

        var me = await As(f, TwitchToken("U1")).GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("Anonymer Hacker", me.GetProperty("name").GetString());
        Assert.Equal(100, me.GetProperty("balance").GetInt32());
    }

    [Fact]
    public async Task Dev_tokens_are_never_twitch_tokens()
    {
        using var f = Factory();
        // A token marked as test login but signed with the Twitch secret must not pass.
        var forged = ViewerTokens.Sign(new JsonObject
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), ["opaque_user_id"] = "U2", ["channel_id"] = "12345",
            ["role"] = "broadcaster", ["gtastream_dev"] = true,
        }, TwitchSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(f, forged).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Dev_session_play_buy_and_watch_it_run_in_the_simulator()
    {
        using var f = Factory();
        var session = await (await f.CreateClient().PostAsync("/api/v1/dev/session", null)).Content.ReadFromJsonAsync<JsonElement>();
        var token = session.GetProperty("token").GetString()!;
        Assert.Equal("viewer", session.GetProperty("role").GetString());
        var c = As(f, token);

        // Renewing keeps the identity.
        var renewed = await (await c.PostAsync("/api/v1/dev/session", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(session.GetProperty("name").GetString(), renewed.GetProperty("name").GetString());

        var start = await (await c.PostAsJsonAsync("/api/v1/games/firewall/start", new { wide = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(start.GetProperty("wide").GetBoolean());
        var finish = await c.PostAsJsonAsync("/api/v1/games/firewall/finish", new { roundId = start.GetProperty("roundId").GetGuid(), taps = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);

        var catalog = await c.GetFromJsonAsync<JsonElement>("/api/v1/catalog");
        Assert.True(catalog.GetProperty("shopOpen").GetBoolean());

        var buy = await c.PostAsJsonAsync("/api/v1/purchase", new { eventId = "confetti" });
        Assert.Equal(HttpStatusCode.OK, buy.StatusCode);
        var again = await c.PostAsJsonAsync("/api/v1/purchase", new { eventId = "heal" });
        Assert.Equal(429, (int)again.StatusCode);
        Assert.Equal("rate_limited", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var status = await c.GetFromJsonAsync<JsonElement>("/api/v1/status");
        Assert.Equal("simulation", status.GetProperty("studio").GetProperty("mode").GetString());
        Assert.Single(status.GetProperty("queue").EnumerateArray());
    }

    [Fact]
    public async Task Panic_needs_the_broadcaster()
    {
        using var f = Factory();
        var viewer = await (await f.CreateClient().PostAsync("/api/v1/dev/session", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden, (await As(f, viewer.GetProperty("token").GetString()!).PostAsync("/api/v1/director/panic", null)).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dev/session");
        req.Headers.Add("X-Admin-Token", "geheim");
        var boss = await (await f.CreateClient().SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("broadcaster", boss.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await As(f, boss.GetProperty("token").GetString()!).PostAsync("/api/v1/director/panic", null)).StatusCode);
    }

    [Fact]
    public async Task Studio_host_gets_interventions_and_reports_back()
    {
        using var f = Factory(sim: "off");
        var viewer = As(f, TwitchToken("U9"));
        Assert.Equal(503, (int)(await viewer.PostAsJsonAsync("/api/v1/purchase", new { eventId = "confetti" })).StatusCode);

        var ws = f.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri(f.Server.BaseAddress, "/api/v1/studio"), CancellationToken.None));
        ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer geheim";
        using var socket = await ws.ConnectAsync(new Uri(f.Server.BaseAddress, "/api/v1/studio"), CancellationToken.None);

        Assert.Equal("welcome", (string?)(await ReceiveJson(socket))["type"]);
        await SendJson(socket, new JsonObject { ["type"] = "hello", ["game"] = "gtav", ["capabilities"] = new JsonArray("confetti", "heal") });
        await SendJson(socket, new JsonObject { ["type"] = "ping" });
        Assert.Equal("pong", (string?)(await ReceiveJson(socket))["type"]);

        var catalog = await viewer.GetFromJsonAsync<JsonElement>("/api/v1/catalog");
        var available = catalog.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("available").GetBoolean()).Select(e => e.GetProperty("id").GetString());
        Assert.Equal(new[] { "confetti", "heal" }, available.Order());

        Assert.Equal(409, (int)(await viewer.PostAsJsonAsync("/api/v1/purchase", new { eventId = "riot" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsJsonAsync("/api/v1/purchase", new { eventId = "confetti" })).StatusCode);

        var intervention = await ReceiveJson(socket);
        Assert.Equal("intervention", (string?)intervention["type"]);
        Assert.Equal("confetti", (string?)intervention["eventId"]);
        Assert.Equal("Anonymer Hacker", (string?)intervention["viewer"]);
        var id = (string)intervention["id"]!;
        await SendJson(socket, new JsonObject { ["type"] = "ack", ["id"] = id, ["accepted"] = true });
        await SendJson(socket, new JsonObject { ["type"] = "result", ["id"] = id, ["ok"] = true });
        await SendJson(socket, new JsonObject { ["type"] = "state", ["state"] = new JsonObject { ["inVehicle"] = true } });

        JsonElement status = default;
        for (var i = 0; i < 50; i++)
        {
            status = await viewer.GetFromJsonAsync<JsonElement>("/api/v1/status");
            if (status.GetProperty("recent").GetArrayLength() > 0 && status.GetProperty("studio").GetProperty("state").ValueKind != JsonValueKind.Null) break;
            await Task.Delay(50);
        }
        Assert.Equal("live", status.GetProperty("studio").GetProperty("mode").GetString());
        Assert.Equal("executed", status.GetProperty("recent")[0].GetProperty("state").GetString());
        Assert.True(status.GetProperty("studio").GetProperty("state").GetProperty("inVehicle").GetBoolean());
    }

    private static async Task SendJson(WebSocket s, JsonObject msg) =>
        await s.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject> ReceiveJson(WebSocket s)
    {
        var buf = new byte[16384];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r = await s.ReceiveAsync(buf, cts.Token);
        return JsonNode.Parse(buf.AsSpan(0, r.Count))!.AsObject();
    }
}
