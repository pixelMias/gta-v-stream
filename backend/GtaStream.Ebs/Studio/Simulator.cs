using GtaStream.Ebs.Economy;

namespace GtaStream.Ebs.Studio;

/// <summary>
/// Stands in for studio host and game while neither exists: accepts every intervention, keeps chaotic ones at least
/// 90 s apart like the director would (concept, cooldown per category), and reports them executed a few seconds later.
/// Interventions that would wait longer than 60 s are rejected and refunded. Never runs for a channel with a real host.
/// </summary>
public sealed class Simulator(Show show, StudioHub hub, TimeProvider time, ILogger<Simulator> log)
{
    public const string ConnectionId = "simulator";
    public static readonly TimeSpan ChaosGap = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTimeOffset> _lastChaos = [];
    private readonly Dictionary<string, DateTimeOffset> _nextSlot = [];
    private readonly List<(DateTimeOffset At, Guid Id)> _pending = [];

    public void Step()
    {
        var now = time.GetUtcNow();
        if (hub.SimulatorEnabled)
            foreach (var channel in show.ChannelsWithQueue())
            {
                if (hub.HasHost(channel)) continue;
                foreach (var p in show.TakeForStudio(channel, ConnectionId))
                {
                    var chaotic = p.Event.Category is EventCategory.Chaotic or EventCategory.Surreal;
                    // Interventions run one after another, a few seconds apart.
                    var at = Max(now.AddSeconds(Random.Shared.Next(2, 6)), _nextSlot.GetValueOrDefault(channel));
                    if (chaotic && _lastChaos.TryGetValue(channel, out var last)) at = Max(at, last + ChaosGap);
                    if (at - p.CreatedAt > MaxHold)
                    {
                        show.StudioAck(p.Id, false, "director_hold_timeout");
                        continue;
                    }
                    show.StudioAck(p.Id, true, null);
                    if (chaotic) _lastChaos[channel] = at;
                    _nextSlot[channel] = at.AddSeconds(3);
                    _pending.Add((at, p.Id));
                    log.LogInformation("Simulator: {Event} von {Viewer} läuft um {At:HH:mm:ss}", p.Event.Id, p.ViewerName, at);
                }
            }

        foreach (var due in _pending.Where(x => x.At <= now).ToList())
        {
            _pending.Remove(due);
            show.StudioResult(due.Id, true, null);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

/// <summary>Drives timeouts and the simulator twice a second.</summary>
public sealed class ShowClock(Show show, Simulator sim, TimeProvider time, ILogger<ShowClock> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { show.Tick(); sim.Step(); }
            catch (Exception e) { log.LogError(e, "Takt fehlgeschlagen"); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), time, stop); } catch (OperationCanceledException) { }
        }
    }
}
