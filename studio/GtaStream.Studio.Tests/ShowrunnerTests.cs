using GtaStream.Studio;
using GtaStream.Studio.Director;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GtaStream.Studio.Tests;

public class ShowrunnerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 20, 0, 0, TimeSpan.Zero));
    private readonly TestGame _game = new();
    private readonly RecordingOutput _out = new();
    private readonly List<(string Id, bool Ok, string? Reason)> _results = [];

    private Showrunner NewDirector(INarrator? narrator = null)
    {
        var d = new Showrunner(_game, narrator ?? new TemplateNarrator(new Random(1)), _out, _time);
        d.ReportResult = (id, ok, reason) => { _results.Add((id, ok, reason)); return Task.CompletedTask; };
        return d;
    }

    private int _n;
    private Intervention I(string eventId, string category, string viewer = "Anna", int durationSec = 0) => new(
        $"p{++_n}", eventId, eventId, category, durationSec, viewer, 100, _time.GetUtcNow(), _time.GetUtcNow().AddSeconds(90));

    private async Task Run(Showrunner d, TimeSpan span)
    {
        for (var t = TimeSpan.Zero; t < span; t += TimeSpan.FromMilliseconds(500))
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await d.Tick();
        }
    }

    [Fact]
    public void Tension_follows_the_concept_weights()
    {
        var calm = new GameState(true, false, 0, false, false, "autopilot");
        Assert.Equal(0, Tension.Compute(calm, 0, TimeSpan.FromMinutes(10)));
        var hot = new GameState(true, true, 3, false, false, "autopilot", SpeedKmh: 140, HealthPct: 30, MissionActive: true);
        Assert.Equal(30 + 15 + 15 + 20 + 10 - 3, Tension.Compute(hot, 2, TimeSpan.FromSeconds(30)));
        Assert.Equal(100, Tension.Compute(hot with { WantedLevel = 5 }, 3, TimeSpan.Zero));
    }

    [Fact]
    public void Target_curve_is_a_sawtooth()
    {
        Assert.Equal(20, TargetCurve.At(TimeSpan.Zero));
        Assert.Equal(50, TargetCurve.At(TimeSpan.FromMinutes(5)));
        Assert.Equal(90, TargetCurve.At(TimeSpan.FromMinutes(10.5)));
        Assert.Equal(15, TargetCurve.At(TimeSpan.FromMinutes(12)));
        Assert.Equal(20, TargetCurve.At(TargetCurve.Cycle));
        Assert.Equal("breather", TargetCurve.Phase(TimeSpan.FromMinutes(13)));
    }

    [Fact]
    public async Task Harmless_runs_after_the_gather_window_with_banner_and_result()
    {
        var d = NewDirector();
        var i = I("weather_storm", "harmless");
        Assert.True((await d.Offer(i))!.Accepted);
        await Run(d, TimeSpan.FromSeconds(2));
        Assert.Empty(_game.Runs);
        await Run(d, TimeSpan.FromSeconds(2));
        Assert.Equal("weather_storm", Assert.Single(_game.Runs).EffectId);
        Assert.Equal((i.Id, true, (string?)null), Assert.Single(_results));
        Assert.Contains("Anna", Assert.Single(_out.Banners));
        Assert.Single(_out.Lines);   // first speech is always due
    }

    [Fact]
    public async Task Equal_interventions_are_bundled_into_one_stronger_run()
    {
        var d = NewDirector();
        await d.Offer(I("tyre_burst", "disruptive", "Anna"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await d.Offer(I("tyre_burst", "disruptive", "Ben"));
        await d.Offer(I("tyre_burst", "disruptive", "Cem"));
        await Run(d, TimeSpan.FromSeconds(5));
        var run = Assert.Single(_game.Runs);
        Assert.Equal(3, run.Strength);
        Assert.Equal(3, _results.Count(r => r.Ok));
        Assert.Contains("3 Hacker", _out.Banners[0]);
    }

    [Fact]
    public async Task Chaos_keeps_90_seconds_apart_and_what_cannot_wait_is_rejected()
    {
        var d = NewDirector();
        await d.Offer(I("ragdoll", "chaotic"));
        await Run(d, TimeSpan.FromSeconds(4));
        Assert.Single(_game.Runs);

        var late = I("riot", "chaotic") with { Deadline = _time.GetUtcNow().AddSeconds(40) };
        await d.Offer(late);
        await Run(d, TimeSpan.FromSeconds(45));
        Assert.Single(_game.Runs);
        Assert.Contains((late.Id, false, "director_hold_timeout"), _results);

        await d.Offer(I("blackout", "chaotic"));
        await Run(d, TimeSpan.FromSeconds(50));   // 95 s after the first chaos
        Assert.Equal(2, _game.Runs.Count);
    }

    [Fact]
    public async Task High_tension_holds_escalation_but_lets_help_through_and_gives_way_at_the_deadline()
    {
        var d = NewDirector();
        _game.State = new GameState(true, true, 5, false, false, "autopilot", SpeedKmh: 150, HealthPct: 30);   // tension ~ 80 > target 20
        var sabotage = I("engine_damage", "disruptive");
        await d.Offer(sabotage);
        await d.Offer(I("heal", "helpful"));
        await Run(d, TimeSpan.FromSeconds(10));
        Assert.Equal("heal", Assert.Single(_game.Runs).EffectId);

        await Run(d, TimeSpan.FromSeconds(75));   // deadline 90 s: urgent from 82 s, then it goes despite the tension
        Assert.Equal("engine_damage", _game.Runs[^1].EffectId);
        Assert.Contains((sabotage.Id, true, (string?)null), _results);
    }

    [Fact]
    public async Task Cutscenes_block_even_at_the_deadline()
    {
        var d = NewDirector();
        _game.State = _game.State with { Cutscene = true };
        var i = I("confetti", "harmless");
        await d.Offer(i);
        await Run(d, TimeSpan.FromSeconds(90));
        Assert.Empty(_game.Runs);
        Assert.Contains((i.Id, false, "director_hold_timeout"), _results);
    }

    [Fact]
    public async Task Human_at_the_wheel_only_gets_what_is_released()
    {
        var d = NewDirector();
        _game.State = _game.State with { Mode = "copilot" };
        Assert.Equal(new Decision(false, "human_driving"), await d.Offer(I("rocket_boost", "chaotic")));
        Assert.True((await d.Offer(I("heal", "helpful")))!.Accepted);
        Assert.Equal(new Decision(false, "not_in_vehicle"), await d.Offer(I("vehicle_pink", "harmless")));
    }

    [Fact]
    public async Task Resent_interventions_are_not_run_twice()
    {
        var d = NewDirector();
        var i = I("confetti", "harmless");
        await d.Offer(i);
        Assert.Null(await d.Offer(i));
        await Run(d, TimeSpan.FromSeconds(4));
        Assert.Null(await d.Offer(i));
        Assert.Single(_game.Runs);
        Assert.Equal(2, _results.Count(r => r.Id == i.Id));   // result sent again after the resend
    }

    [Fact]
    public async Task At_most_three_effects_at_once()
    {
        var d = NewDirector();
        foreach (var e in new[] { "weather_storm", "time_midnight", "screen_trip", "drunk" })
            await d.Offer(I(e, e == "drunk" ? "disruptive" : "harmless", durationSec: 120));
        await Run(d, TimeSpan.FromSeconds(30));
        Assert.Equal(3, _game.Runs.Count);
        await Run(d, TimeSpan.FromSeconds(60));   // still blocked → rejected at the deadline
        Assert.Contains(_results, r => r.Reason == "director_hold_timeout");
    }

    [Fact]
    public async Task Panic_drops_the_queue_and_stops_the_game()
    {
        var d = NewDirector();
        await d.Offer(I("confetti", "harmless"));
        await d.Panic();
        Assert.Equal(0, d.QueueLength);
        Assert.True(_game.Stopped);
        await Run(d, TimeSpan.FromSeconds(10));
        Assert.Empty(_game.Runs);
    }

    [Fact]
    public async Task Speech_at_most_every_45_seconds()
    {
        var d = NewDirector();
        await d.Offer(I("confetti", "harmless"));
        await Run(d, TimeSpan.FromSeconds(10));
        await d.Offer(I("weather_storm", "harmless"));
        await Run(d, TimeSpan.FromSeconds(10));
        Assert.Equal(2, _out.Banners.Count);
        Assert.Single(_out.Lines);
    }
}

public sealed class TestGame : IGameAdapter
{
    public string Game => "test";
    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>
    {
        "weather_storm", "time_midnight", "screen_trip", "confetti", "vehicle_pink", "tyre_burst", "engine_damage", "drunk",
        "rocket_boost", "riot", "ragdoll", "blackout", "heal",
    };
    public GameState State { get; set; } = new(true, false, 0, false, false, "autopilot");
    public List<(string EffectId, int Strength)> Runs { get; } = [];
    public bool Stopped { get; private set; }

    public string? Check(string effectId) => effectId == "vehicle_pink" && !State.InVehicle ? "not_in_vehicle" : null;

    public Task<EffectOutcome> Run(string effectId, int durationSec, int strength, string viewer, CancellationToken ct)
    {
        Runs.Add((effectId, strength));
        return Task.FromResult(new EffectOutcome(true));
    }

    public Task StopAll() { Stopped = true; return Task.CompletedTask; }
}

public sealed class RecordingOutput : IShowOutput
{
    public List<string> Banners { get; } = [];
    public List<string> Lines { get; } = [];
    public Task Banner(string text, string category) { Banners.Add(text); return Task.CompletedTask; }
    public Task Say(string text) { Lines.Add(text); return Task.CompletedTask; }
}
