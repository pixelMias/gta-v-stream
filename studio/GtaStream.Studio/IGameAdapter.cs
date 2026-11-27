namespace GtaStream.Studio;

/// <summary>
/// The studio host's view of the game. The real adapter will talk to the ScriptHookVDotNet mod over a local WebSocket
/// (concept, section 6: hello, state, event, command, ack, result, ping plus effect.start / effect.stop); until it
/// exists, <see cref="FakeAdapter"/> stands in. Natives run only in the mod's OnTick, never here.
/// </summary>
public interface IGameAdapter
{
    /// <summary>Game name for the EBS, e.g. "gtav".</summary>
    string Game { get; }

    /// <summary>Game-neutral effect ids the game can run right now (catalog ids such as tyre_burst).</summary>
    IReadOnlySet<string> Capabilities { get; }

    /// <summary>Small snapshot for viewers and the director: in vehicle, wanted level, cutscene and so on.</summary>
    GameState State { get; }

    /// <summary>Checks whether the effect fits the moment; null = accepted, otherwise a reason such as not_in_vehicle.</summary>
    string? Check(string effectId);

    /// <summary>Starts the effect; the mod reverts timed effects on its own, even if the studio host dies.</summary>
    Task<EffectOutcome> Run(string effectId, int durationSec, string viewer, CancellationToken ct);

    /// <summary>Panic button: stop every effect at once.</summary>
    Task StopAll();
}

public sealed record GameState(bool Connected, bool InVehicle, int WantedLevel, bool Cutscene, bool Paused, string Mode);

public sealed record EffectOutcome(bool Ok, string? Reason = null);

/// <summary>Pretends to be the game: accepts everything except vehicle effects every now and then.</summary>
public sealed class FakeAdapter : IGameAdapter
{
    private static readonly string[] VehicleEffects = ["tyre_burst", "engine_damage", "vehicle_pink", "rocket_boost", "vehicle_fix"];
    private bool _inVehicle = true;

    public string Game => "fake";

    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>
    {
        "weather_storm", "time_midnight", "screen_trip", "confetti", "vehicle_pink", "name_banner", "wanted_up", "tyre_burst",
        "engine_damage", "drunk", "emp", "bounty_hunters", "rocket_boost", "riot", "ragdoll", "blackout", "moon_gravity",
        "slow_motion", "tpose_anomaly", "heal", "vehicle_fix", "wanted_clear",
    };

    public GameState State => new(true, _inVehicle, 0, false, false, "autopilot");

    public string? Check(string effectId)
    {
        _inVehicle = Random.Shared.NextDouble() < 0.8;
        return !_inVehicle && VehicleEffects.Contains(effectId) ? "not_in_vehicle" : null;
    }

    public async Task<EffectOutcome> Run(string effectId, int durationSec, string viewer, CancellationToken ct)
    {
        Console.WriteLine($"[spiel] {effectId} für {viewer}{(durationSec > 0 ? $", {durationSec} s" : "")}");
        await Task.Delay(200, ct);
        return new EffectOutcome(true);
    }

    public Task StopAll()
    {
        Console.WriteLine("[spiel] Notabschaltung: alle Effekte aus");
        return Task.CompletedTask;
    }
}
