namespace GtaStream.Studio.Director;

/// <summary>A paid intervention as the EBS sends it (docs/studio-protokoll.md).</summary>
public sealed record Intervention(
    string Id, string EventId, string EventName, string Category, int DurationSec, string Viewer, int Price,
    DateTimeOffset CreatedAt, DateTimeOffset Deadline);

/// <summary>Where the show goes: overlay banner and voice. Console for now, later overlay server and TTS.</summary>
public interface IShowOutput
{
    Task Banner(string text, string category);
    Task Say(string text);
}

public sealed class ConsoleShowOutput : IShowOutput
{
    public Task Banner(string text, string category) { Console.WriteLine($"[banner/{category}] {text}"); return Task.CompletedTask; }
    public Task Say(string text) { Console.WriteLine($"[stimme] {text}"); return Task.CompletedTask; }
}

/// <summary>Accepted or rejected, as the EBS expects it in "ack".</summary>
public sealed record Decision(bool Accepted, string? Reason);

/// <summary>
/// The showrunner (concept, section 4): deterministic code decides whether and when an intervention runs, the narrator
/// only tells it. Per tick it checks safety flags, capacity and the category cooldown, compares the tension with the
/// target curve, bundles equal interventions and releases at most one. Paid interventions are only ever delayed,
/// never swapped for something else; whatever cannot run before its deadline is rejected so the EBS refunds it.
/// </summary>
public sealed class Showrunner
{
    public static readonly TimeSpan GatherWindow = TimeSpan.FromSeconds(3);     // wait a moment for equal purchases to bundle
    public static readonly TimeSpan BundleWindow = TimeSpan.FromSeconds(20);    // concept: equal interventions within 20 s
    public static readonly TimeSpan ReleaseGap = TimeSpan.FromSeconds(4);       // breathing room between two interventions
    public static readonly TimeSpan ChaosGap = TimeSpan.FromSeconds(90);        // concept: max. 1 chaotic event per 90 s
    public static readonly TimeSpan SpeechGap = TimeSpan.FromSeconds(45);       // concept: TTS at most every 45 s
    public static readonly TimeSpan DeadlineMargin = TimeSpan.FromSeconds(8);   // leave time to run and report
    public const int MaxActiveEffects = 3;                                       // concept: also enforced by the mod
    public const int HoldMargin = 10;                                            // tension may exceed the target by this much

    /// <summary>Categories that count as "chaos" for the 90 s gap.</summary>
    public static readonly IReadOnlySet<string> Chaos = new HashSet<string> { "chaotic", "surreal" };
    /// <summary>Held back while the tension is above the curve.</summary>
    public static readonly IReadOnlySet<string> Escalating = new HashSet<string> { "disruptive", "chaotic", "surreal" };
    /// <summary>Concept: when Matthias drives, only what he released; default harmless and helpful.</summary>
    public IReadOnlySet<string> AllowedWhileHumanDrives { get; set; } = new HashSet<string> { "harmless", "helpful", "stream" };

    private readonly IGameAdapter _game;
    private readonly INarrator _narrator;
    private readonly IShowOutput _output;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _start;
    private readonly List<Intervention> _queue = [];
    private readonly List<(string EventId, DateTimeOffset Until)> _active = [];
    private readonly Dictionary<string, (bool Ok, string? Reason)?> _known = [];   // id → null while queued, result when done
    private DateTimeOffset _lastRelease = DateTimeOffset.MinValue;
    private DateTimeOffset _lastChaos = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSpeech = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    /// <summary>Sends "result" to the EBS: id, ok, reason.</summary>
    public Func<string, bool, string?, Task> ReportResult { get; set; } = (_, _, _) => Task.CompletedTask;

    public Showrunner(IGameAdapter game, INarrator narrator, IShowOutput output, TimeProvider time)
    {
        _game = game;
        _narrator = narrator;
        _output = output;
        _time = time;
        _start = time.GetUtcNow();
        _lastRelease = _start;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    public int QueueLength { get { lock (_lock) return _queue.Count; } }

    /// <summary>Snapshot for logs, tests and later the director console.</summary>
    public (int Tension, int Target, string Phase) Pulse()
    {
        lock (_lock)
        {
            var now = Now;
            _active.RemoveAll(a => a.Until <= now);
            return (Tension.Compute(_game.State, _active.Count, now - _lastRelease), TargetCurve.At(now - _start), TargetCurve.Phase(now - _start));
        }
    }

    /// <summary>
    /// First look at a new intervention, answered right away as "ack". Null means: already known (the EBS resends open
    /// interventions after a reconnect), nothing new to say; a finished one gets its result again.
    /// </summary>
    public async Task<Decision?> Offer(Intervention i)
    {
        (bool Ok, string? Reason)? done;
        lock (_lock)
        {
            if (!_known.TryGetValue(i.Id, out done))
            {
                var reason = Refuse(i, _game.State);
                if (reason is not null) return new Decision(false, reason);
                _known[i.Id] = null;
                _queue.Add(i);
                return new Decision(true, null);
            }
        }
        if (done is { } d) await ReportResult(i.Id, d.Ok, d.Reason);
        return null;
    }

    private string? Refuse(Intervention i, GameState s)
    {
        if (!_game.Capabilities.Contains(i.EventId)) return "unsupported";
        if (s.Mode == "copilot" && !AllowedWhileHumanDrives.Contains(i.Category)) return "human_driving";
        return _game.Check(i.EventId);
    }

    /// <summary>Called about twice a second. Releases at most one intervention (with everything bundled into it).</summary>
    public async Task Tick(CancellationToken ct = default)
    {
        List<Intervention>? bundle = null;
        List<(Intervention I, string Reason)> rejected = [];
        int tension, target;
        GameState state;
        lock (_lock)
        {
            var now = Now;
            state = _game.State;
            _active.RemoveAll(a => a.Until <= now);
            tension = Tension.Compute(state, _active.Count, now - _lastRelease);
            target = TargetCurve.At(now - _start);

            foreach (var i in _queue.OrderBy(i => i.CreatedAt).ToList())
            {
                var urgent = now >= i.Deadline - DeadlineMargin;
                var blocker = Blocker(i, state, now, tension, target, urgent);
                if (blocker is null && (now - i.CreatedAt >= GatherWindow || urgent))
                {
                    bundle = _queue.Where(o => o.EventId == i.EventId && o.CreatedAt - i.CreatedAt <= BundleWindow).ToList();
                    foreach (var b in bundle) _queue.Remove(b);
                    break;
                }
                if (urgent && blocker is not null && now >= i.Deadline - DeadlineMargin / 2)
                {
                    _queue.Remove(i);
                    rejected.Add((i, "director_hold_timeout"));
                }
            }

            if (bundle is not null)
            {
                var first = bundle[0];
                _lastRelease = now;
                if (Chaos.Contains(first.Category)) _lastChaos = now;
                if (first.DurationSec > 0) _active.Add((first.EventId, now.AddSeconds(first.DurationSec)));
            }
            foreach (var (i, reason) in rejected) _known[i.Id] = (false, reason);
        }

        foreach (var (i, reason) in rejected)
        {
            Console.WriteLine($"[regie] {i.EventId} von {i.Viewer} verworfen: {reason}");
            await ReportResult(i.Id, false, reason);
        }
        if (bundle is not null) await Release(bundle, state, tension, target, ct);
    }

    /// <summary>Why an intervention has to wait right now; null = it may go.</summary>
    private string? Blocker(Intervention i, GameState s, DateTimeOffset now, int tension, int target, bool urgent)
    {
        // Hard rules first: these hold even at the deadline.
        if (!s.Connected) return "game_offline";
        if (s.Cutscene || s.Paused || s.Loading) return "unsafe_moment";
        if (s.Mode == "pause") return "director_pause";
        if (s.Mode == "copilot" && !AllowedWhileHumanDrives.Contains(i.Category)) return "human_driving";
        if (_active.Count >= MaxActiveEffects) return "too_many_effects";
        if (Chaos.Contains(i.Category) && now - _lastChaos < ChaosGap) return "chaos_gap";
        if (now - _lastRelease < ReleaseGap) return "release_gap";
        // Pacing: soft, gives way when the deadline comes close.
        if (!urgent && Escalating.Contains(i.Category) && tension > target + HoldMargin) return "tension_high";
        return null;
    }

    private async Task Release(List<Intervention> bundle, GameState state, int tension, int target, CancellationToken ct)
    {
        var first = bundle[0];
        var viewers = bundle.Select(b => WordFilter.SafeName(b.Viewer)).Distinct().ToList();
        Console.WriteLine($"[regie] los: {first.EventId} ×{bundle.Count} (Spannung {tension}, Ziel {target})");

        var wantSpeech = false;
        lock (_lock)
            if (Now - _lastSpeech >= SpeechGap) { wantSpeech = true; _lastSpeech = Now; }
        var narration = await _narrator.Narrate(new NarrationRequest(first.EventId, first.EventName, first.Category, viewers, StateLine(state), wantSpeech), ct);
        if (narration is not null)
        {
            await _output.Banner(narration.Banner, first.Category);
            if (narration.Say is { } say) await _output.Say(say);
        }

        EffectOutcome outcome;
        try { outcome = await _game.Run(first.EventId, first.DurationSec, bundle.Count, TemplateNarrator.Who(viewers), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { outcome = new EffectOutcome(false, "adapter_error"); }

        lock (_lock)
            foreach (var b in bundle) _known[b.Id] = (outcome.Ok, outcome.Reason);
        foreach (var b in bundle) await ReportResult(b.Id, outcome.Ok, outcome.Reason);
    }

    public static string StateLine(GameState s)
    {
        var parts = new List<string> { s.InVehicle ? $"im Auto, {s.SpeedKmh:0} km/h" : "zu Fuß" };
        if (s.WantedLevel > 0) parts.Add($"Fahndungsstufe {s.WantedLevel}");
        parts.Add($"Gesundheit {s.HealthPct} %");
        if (s.MissionActive) parts.Add("Mission läuft");
        if (s.Mode == "copilot") parts.Add("ein Mensch fährt gerade");
        return string.Join(", ", parts);
    }

    /// <summary>Panic button: the EBS already refunded the queue, so it is just dropped; the game stops every effect.</summary>
    public async Task Panic()
    {
        lock (_lock)
        {
            foreach (var i in _queue) _known[i.Id] = (false, "panic");
            _queue.Clear();
            _active.Clear();
        }
        await _game.StopAll();
    }
}
