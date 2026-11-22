using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GtaStream.Ebs.Tests;

public class ApiTests
{
    private static WebApplicationFactory<Program> Factory(string? adminToken = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (adminToken != null) b.UseSetting("GTASTREAM_ADMIN_TOKEN", adminToken);
        });

    [Fact]
    public async Task Health_reports_commit_in_the_form_the_deploy_script_greps()
    {
        using var client = Factory().CreateClient();
        var body = await client.GetStringAsync("/api/health");
        Assert.Contains("\"commit\":\"", body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("off", doc.RootElement.GetProperty("db").GetString());
    }

    [Fact]
    public async Task Logs_do_not_exist_without_configured_token()
    {
        using var client = Factory().CreateClient();
        var res = await client.GetAsync("/api/logs");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Logs_need_the_right_token()
    {
        using var client = Factory("geheim").CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/logs")).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/logs?limit=50");
        req.Headers.Add("X-Admin-Token", "falsch");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(req)).StatusCode);

        req = new HttpRequestMessage(HttpMethod.Get, "/api/logs?limit=50");
        req.Headers.Add("X-Admin-Token", "geheim");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entries = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(entries.EnumerateArray(), e => e.GetProperty("message").GetString()!.Contains("EBS gestartet"));
    }

    [Fact]
    public void Buffer_keeps_only_the_newest_entries()
    {
        var buffer = new LogBuffer(capacity: 3);
        for (var i = 0; i < 5; i++) buffer.Add(Microsoft.Extensions.Logging.LogLevel.Information, "t", $"m{i}", null);
        buffer.Add(Microsoft.Extensions.Logging.LogLevel.Warning, "t", "w", null);
        var all = buffer.Read(10, Microsoft.Extensions.Logging.LogLevel.Trace, 0);
        Assert.Equal(new[] { "m3", "m4", "w" }, all.Select(e => e.Message));
        Assert.Single(buffer.Read(10, Microsoft.Extensions.Logging.LogLevel.Warning, 0));
        Assert.Equal(2, buffer.Read(10, Microsoft.Extensions.Logging.LogLevel.Trace, all[0].Id).Count);
    }
}
