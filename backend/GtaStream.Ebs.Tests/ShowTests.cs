using System.Text.Json;
using GtaStream.Ebs.Auth;
using GtaStream.Ebs.Economy;
using GtaStream.Ebs.Games;
using GtaStream.Ebs.Persistence;
using GtaStream.Ebs.Studio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GtaStream.Ebs.Tests;

public class ShowTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLedgerStore _store = new();
    private static readonly Viewer Anna = new("dev", "Uanna", "anna", "viewer", "Anna", true);
    private static readonly Viewer Ben = new("dev", "Uben", "ben", "viewer", "Ben", true);

    private Show NewShow(bool open = true)
    {
        var show = new Show(_store, _time, NullLogger<Show>.Instance);
        show.Capabilities = _ => open ? StudioHub.AllEvents : null;
        return show;
    }

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static int PlayPerfect(Show show, Viewer v, FakeTimeProvider time)
    {
        var start = J(show.StartRound(v, false));
        var seed = start.GetProperty("seed").GetUInt32();
        // Find a perfect tap sequence for this seed the same way the client vector was made.
        var taps = new List<int>();
        for (var t = 200; t < 25_000; t += 10)
        {
            var trial = taps.Append(t).ToList();
            if (taps.Count > 0 && t - taps[^1] < FirewallBreach.MinTapGapMs) continue;
            var r = FirewallBreach.Score(seed, false, trial);
            if (r.Misses == 0) taps = trial;
            if (taps.Count >= 20) break;
        }
        time.Advance(TimeSpan.FromMilliseconds(taps[^1] + 500));
        var finish = J(show.FinishRound(v, start.GetProperty("roundId").GetGuid(), taps));
        time.Advance(TimeSpan.FromSeconds(3));
        return finish.GetProperty("credited").GetInt32();
    }

    [Fact]
    public void New_viewer_gets_start_coins_once()
    {
        var show = NewShow();
        Assert.Equal(Show.StartCoins, J(show.Me(Anna)).GetProperty("balance").GetInt32());
        Assert.Equal(Show.StartCoins, J(show.Me(Anna)).GetProperty("balance").GetInt32());
    }

    [Fact]
    public void A_round_credits_coins_and_the_hour_is_capped()
    {
        var show = NewShow();
        var total = 0;
        for (var i = 0; i < 20; i++) total += PlayPerfect(show, Anna, _time);
        Assert.Equal(Show.HourlyGameCap, total);
        Assert.Equal(Show.StartCoins + Show.HourlyGameCap, show.Balance("dev", "Uanna"));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(PlayPerfect(show, Anna, _time) > 0);
    }

    [Fact]
    public void Rounds_cannot_be_replayed_or_claim_future_taps()
    {
        var show = NewShow();
        var start = J(show.StartRound(Anna, false));
        var id = start.GetProperty("roundId").GetGuid();
        _time.Advance(TimeSpan.FromSeconds(1));
        var res = show.FinishRound(Anna, id, [20_000]);
        Assert.Equal("clock_ahead", Assert.IsType<ApiError>(res).Code);
        Assert.Equal("round_unknown", Assert.IsType<ApiError>(show.FinishRound(Anna, id, [])).Code);
        Assert.Equal("rate_limited", Assert.IsType<ApiError>(show.StartRound(Anna, false)).Code);
    }

    [Fact]
    public void Purchase_reserves_then_executes_or_refunds()
    {
        var show = NewShow();
        show.Me(Anna);
        var bought = J(show.Buy(Anna, "weather_storm"));
        Assert.Equal(1, bought.GetProperty("position").GetInt32());
        Assert.Equal(50, bought.GetProperty("balance").GetInt32());
        var id = bought.GetProperty("purchase").GetProperty("id").GetGuid();

        var handed = show.TakeForStudio("dev", "host1");
        Assert.Single(handed);
        Assert.Empty(show.TakeForStudio("dev", "host1"));
        show.StudioAck(id, true, null);
        show.StudioResult(id, true, null);
        var active = J(show.Status("dev", "live")).GetProperty("active");
        Assert.Equal("weather_storm", active[0].GetProperty("eventId").GetString());
        Assert.Equal(50, show.Balance("dev", "Uanna"));

        // Rejected by the game: coins come back, and the event cooldown is lifted again.
        _time.Advance(TimeSpan.FromSeconds(11));
        var b2 = J(show.Buy(Anna, "vehicle_pink"));
        show.StudioAck(b2.GetProperty("purchase").GetProperty("id").GetGuid(), false, "not_in_vehicle");
        Assert.Equal(50, show.Balance("dev", "Uanna"));
        Assert.Equal(0, J(show.CatalogFor(Ben)).GetProperty("events").EnumerateArray().First(e => e.GetProperty("id").GetString() == "vehicle_pink").GetProperty("cooldownSec").GetInt32());
    }

    [Fact]
    public void Purchase_rules()
    {
        var show = NewShow();
        Assert.Equal("insufficient_coins", Assert.IsType<ApiError>(show.Buy(Anna, "moon_gravity")).Code);
        Assert.Equal("event_unknown", Assert.IsType<ApiError>(show.Buy(Anna, "nope")).Code);
        Assert.IsNotType<ApiError>(show.Buy(Anna, "confetti"));
        Assert.Equal("rate_limited", Assert.IsType<ApiError>(show.Buy(Anna, "weather_storm")).Code);
        Assert.IsNotType<ApiError>(show.Buy(Ben, "confetti"));   // joins Anna's purchase within 20 s
        _time.Advance(Show.JoinWindow + TimeSpan.FromSeconds(1));
        var carl = new Viewer("dev", "Ucarl", null, "viewer", "Carl", true);
        Assert.Equal("event_cooldown", Assert.IsType<ApiError>(show.Buy(carl, "confetti")).Code);
        Assert.Equal("shop_closed", Assert.IsType<ApiError>(NewShow(open: false).Buy(Ben, "confetti")).Code);
    }

    [Fact]
    public void Popular_events_get_more_expensive()
    {
        var show = NewShow();
        int Price() => J(show.CatalogFor(Ben)).GetProperty("events").EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == "confetti").GetProperty("price").GetInt32();
        Assert.Equal(60, Price());
        var id = J(show.Buy(Anna, "confetti")).GetProperty("purchase").GetProperty("id").GetGuid();
        show.StudioResult(id, true, null);
        Assert.Equal(69, Price());
        _time.Advance(Show.PriceWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(60, Price());
    }

    [Fact]
    public void Waiting_too_long_refunds_and_panic_refunds_everything()
    {
        var show = NewShow();
        show.Buy(Anna, "confetti");
        _time.Advance(Show.QueueTimeout + TimeSpan.FromSeconds(1));
        show.Tick();
        Assert.Equal(Show.StartCoins, show.Balance("dev", "Uanna"));

        show.Buy(Ben, "weather_storm");
        Assert.Equal(1, show.Panic("dev"));
        Assert.Equal(Show.StartCoins, show.Balance("dev", "Uben"));
    }

    [Fact]
    public void Restart_restores_balances_and_refunds_open_purchases()
    {
        var show = NewShow();
        show.Buy(Anna, "weather_storm");
        Assert.Equal(50, show.Balance("dev", "Uanna"));
        var restarted = NewShow();
        Assert.Equal(Show.StartCoins, restarted.Balance("dev", "Uanna"));
        Assert.Equal(Show.StartCoins, NewShow().Balance("dev", "Uanna"));
    }

    [Fact]
    public void Simulator_keeps_chaos_apart_and_refunds_what_waits_too_long()
    {
        var rich = new Viewer("dev", "Urich", null, "viewer", "Rich", true);
        _store.Append(new LedgerRow(0, _time.GetUtcNow(), "dev", "Urich", 5000, LedgerKind.Grant, null, "test"));
        var show = new Show(_store, _time, NullLogger<Show>.Instance);
        var hub = new StudioHub(show, new ConfigurationBuilder().Build(), _time, NullLogger<StudioHub>.Instance);
        var sim = new Simulator(show, hub, _time, NullLogger<Simulator>.Instance);

        show.Buy(rich, "ragdoll");
        sim.Step();
        _time.Advance(TimeSpan.FromSeconds(10));
        sim.Step();
        Assert.Single(J(show.Status("dev", "simulation")).GetProperty("recent").EnumerateArray());

        _time.Advance(TimeSpan.FromSeconds(1));
        show.Buy(rich, "rocket_boost");   // chaotic again within 90 s: would wait > 60 s
        sim.Step();
        var recent = J(show.Status("dev", "simulation")).GetProperty("recent");
        Assert.Contains(recent.EnumerateArray(), p => p.GetProperty("reason").GetString() == "director_hold_timeout");
    }
}
