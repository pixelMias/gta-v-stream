using GtaStream.Ebs.Auth;
using GtaStream.Ebs.Games;
using GtaStream.Ebs.Persistence;

namespace GtaStream.Ebs.Economy;

public sealed record ApiError(int Status, string Code, string Message, int? RetryAfterSec = null);

public enum PurchaseState { Queued, Accepted, Executed, Refunded }

public sealed class Purchase
{
    public required Guid Id { get; init; }
    public required string ChannelId { get; init; }
    public required string Account { get; init; }
    public required string ViewerName { get; init; }
    public required EventDef Event { get; init; }
    public required int Price { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public PurchaseState State { get; set; } = PurchaseState.Queued;
    public string? Reason { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    /// <summary>Studio connection the purchase was handed to; null = not handed out yet (or that connection is gone).</summary>
    public string? DispatchedTo { get; set; }
}

public sealed record Round(Guid Id, string ChannelId, string Account, uint Seed, bool Wide, DateTimeOffset IssuedAt);

/// <summary>
/// The EBS's single source of truth for coins, rounds, purchases, cooldowns and the queue (concept, sections 2 and 6).
/// Everything runs under one lock; every coin movement is written to the ledger store before it counts.
/// </summary>
public sealed class Show
{
    public const int StartCoins = 100;
    public const int WalletCap = 5000;
    public const int HourlyGameCap = 600;
    public static readonly TimeSpan GameStartGap = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PurchaseGap = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RoundGrace = TimeSpan.FromSeconds(30);
    /// <summary>Concept: the director holds an intervention up to 60 s; add time for transport and execution.</summary>
    public static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan PriceWindow = TimeSpan.FromMinutes(10);
    /// <summary>How long after a purchase others can join the same intervention despite the event cooldown.</summary>
    public static readonly TimeSpan JoinWindow = TimeSpan.FromSeconds(20);
    public const double PriceStep = 0.15;

    private readonly object _lock = new();
    private readonly ILedgerStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<Show> _log;

    private readonly Dictionary<(string, string), int> _balances = [];
    private readonly Dictionary<(string, string), string> _names;
    private readonly List<LedgerRow> _recent = [];   // last 24 h, for caps and the leaderboard
    private readonly Dictionary<(string, string), DateTimeOffset> _lastGameStart = [];
    private readonly Dictionary<(string, string), DateTimeOffset> _lastPurchase = [];
    private readonly Dictionary<(string, string, string), DateTimeOffset> _viewerEventLast = [];
    private readonly Dictionary<(string, string), DateTimeOffset> _eventLast = [];
    private readonly Dictionary<Guid, Round> _rounds = [];
    private readonly Dictionary<Guid, Purchase> _purchases = [];

    /// <summary>Which events the studio host of a channel can run right now; null = shop closed (no host, no simulator).</summary>
    public Func<string, IReadOnlySet<string>?> Capabilities { get; set; } = _ => null;

    public Show(ILedgerStore store, TimeProvider time, ILogger<Show> log)
    {
        _store = store;
        _time = time;
        _log = log;
        _names = new Dictionary<(string, string), string>(store.LoadNames().ToDictionary(kv => (kv.Key.Channel, kv.Key.Account), kv => kv.Value));
        Restore(store.LoadAll());
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private void Restore(IReadOnlyList<LedgerRow> rows)
    {
        var open = new Dictionary<string, LedgerRow>();
        foreach (var row in rows)
        {
            var key = (row.ChannelId, row.Account);
            _balances[key] = _balances.GetValueOrDefault(key) + row.Delta;
            if (row.At > Now.AddHours(-24)) _recent.Add(row);
            if (row.Kind == LedgerKind.Purchase && row.Ref != null) open[row.Ref] = row;
            else if (row.Kind is LedgerKind.Refund or LedgerKind.Executed && row.Ref != null) open.Remove(row.Ref);
        }
        // Purchases that were still waiting when the EBS stopped will never run: give the coins back.
        foreach (var row in open.Values)
            Book(row.ChannelId, row.Account, -row.Delta, LedgerKind.Refund, row.Ref, "restart");
        if (rows.Count > 0) _log.LogInformation("Ledger geladen: {Rows} Buchungen, {Open} offene Käufe erstattet", rows.Count, open.Count);
    }

    private LedgerRow Book(string channel, string account, int delta, string kind, string? reference, string? meta)
    {
        var row = _store.Append(new LedgerRow(0, Now, channel, account, delta, kind, reference, meta));
        var key = (channel, account);
        _balances[key] = _balances.GetValueOrDefault(key) + delta;
        _recent.Add(row);
        return row;
    }

    /// <summary>First contact books the start credit; a changed name is stored for leaderboard and banners.</summary>
    private void Touch(Viewer v)
    {
        var key = (v.ChannelId, v.Account);
        if (!_balances.ContainsKey(key)) Book(v.ChannelId, v.Account, StartCoins, LedgerKind.Grant, null, "start");
        if (_names.GetValueOrDefault(key) != v.Name)
        {
            _store.SaveName(v.ChannelId, v.Account, v.Name);
            _names[key] = v.Name;
        }
    }

    private int GameCoinsLastHour(string channel, string account)
    {
        var since = Now.AddHours(-1);
        return _recent.Where(r => r.Kind == LedgerKind.Game && r.ChannelId == channel && r.Account == account && r.At > since).Sum(r => r.Delta);
    }

    private static int Remaining(DateTimeOffset? last, TimeSpan gap, DateTimeOffset now) =>
        last is null ? 0 : Math.Max(0, (int)Math.Ceiling((last.Value + gap - now).TotalSeconds));

    // ---------------------------------------------------------------- minigame

    public object StartRound(Viewer v, bool wide)
    {
        lock (_lock)
        {
            Touch(v);
            var key = (v.ChannelId, v.Account);
            var wait = Remaining(_lastGameStart.TryGetValue(key, out var last) ? last : null, GameStartGap, Now);
            if (wait > 0) return new ApiError(429, "rate_limited", "Kurz durchatmen, gleich geht die nächste Runde.", wait);
            _lastGameStart[key] = Now;

            foreach (var old in _rounds.Values.Where(r => r.IssuedAt < Now - TimeSpan.FromMinutes(5)).ToList()) _rounds.Remove(old.Id);
            var round = new Round(Guid.NewGuid(), v.ChannelId, v.Account, (uint)Random.Shared.NextInt64(1, uint.MaxValue), wide, Now);
            _rounds[round.Id] = round;
            return new
            {
                roundId = round.Id,
                seed = round.Seed,
                wide,
                durationMs = FirewallBreach.DurationMs,
                minTapGapMs = FirewallBreach.MinTapGapMs,
                speedDegPerSec = FirewallBreach.SpeedDegPerSec,
                sectorDeg = FirewallBreach.SectorDeg,
                wideFactor = FirewallBreach.WideFactor,
                hourlyLeft = Math.Max(0, HourlyGameCap - GameCoinsLastHour(v.ChannelId, v.Account)),
            };
        }
    }

    public object FinishRound(Viewer v, Guid roundId, IReadOnlyList<int> taps)
    {
        lock (_lock)
        {
            if (!_rounds.Remove(roundId, out var round) || round.ChannelId != v.ChannelId || round.Account != v.Account)
                return new ApiError(404, "round_unknown", "Diese Runde kennt der Server nicht (mehr).");
            var elapsed = Now - round.IssuedAt;
            if (elapsed > TimeSpan.FromMilliseconds(FirewallBreach.DurationMs) + RoundGrace)
                return new ApiError(410, "round_expired", "Die Runde ist abgelaufen.");

            var score = FirewallBreach.Score(round.Seed, round.Wide, taps);
            // A tap cannot lie in the future: the client started its clock after the server issued the round.
            if (score.Valid && taps.Count > 0 && taps[^1] > elapsed.TotalMilliseconds + 1500)
                score = score with { Valid = false, Reason = "clock_ahead" };
            if (!score.Valid)
            {
                _log.LogWarning("Runde {Round} von {Account} verworfen: {Reason}", roundId, v.Account, score.Reason);
                return new ApiError(422, score.Reason!, "Die Runde ließ sich nicht nachprüfen und zählt nicht.");
            }

            var key = (v.ChannelId, v.Account);
            var hourlyLeft = Math.Max(0, HourlyGameCap - GameCoinsLastHour(v.ChannelId, v.Account));
            var walletLeft = Math.Max(0, WalletCap - _balances.GetValueOrDefault(key));
            var credited = Math.Min(score.Coins, Math.Min(hourlyLeft, walletLeft));
            if (credited > 0) Book(v.ChannelId, v.Account, credited, LedgerKind.Game, round.Id.ToString(), "firewall");
            return new
            {
                hits = score.Hits,
                misses = score.Misses,
                bestLevel = score.BestLevel,
                earned = score.Coins,
                credited,
                cappedBy = credited == score.Coins ? null : hourlyLeft <= walletLeft ? "hour" : "wallet",
                balance = _balances[key],
            };
        }
    }

    // ---------------------------------------------------------------- shop

    private int PriceOf(string channel, EventDef e)
    {
        var since = Now - PriceWindow;
        var n = _purchases.Values.Count(p => p.ChannelId == channel && p.Event.Id == e.Id && p.CreatedAt > since && p.State != PurchaseState.Refunded);
        return (int)Math.Round(e.Cost * (1 + PriceStep * n));
    }

    public object CatalogFor(Viewer v)
    {
        lock (_lock)
        {
            var caps = Capabilities(v.ChannelId);
            return new
            {
                shopOpen = caps != null,
                events = Catalog.All.Select(e => new
                {
                    id = e.Id,
                    name = e.Name,
                    description = e.Description,
                    category = Catalog.CategoryKey(e.Category),
                    price = PriceOf(v.ChannelId, e),
                    basePrice = e.Cost,
                    durationSec = e.DurationSec,
                    available = caps?.Contains(e.Id) == true,
                    cooldownSec = Remaining(_eventLast.TryGetValue((v.ChannelId, e.Id), out var t) ? t : null, TimeSpan.FromSeconds(e.CooldownSec), Now),
                    joinable = _purchases.Values.Any(o => o.ChannelId == v.ChannelId && o.Event.Id == e.Id &&
                        o.State is PurchaseState.Queued or PurchaseState.Accepted && Now - o.CreatedAt <= JoinWindow),
                    viewerCooldownSec = Remaining(_viewerEventLast.TryGetValue((v.ChannelId, v.Account, e.Id), out var u) ? u : null, TimeSpan.FromSeconds(e.ViewerCooldownSec), Now),
                }),
            };
        }
    }

    public object Buy(Viewer v, string eventId)
    {
        lock (_lock)
        {
            Touch(v);
            var e = Catalog.Find(eventId);
            if (e is null) return new ApiError(404, "event_unknown", "Diesen Eingriff gibt es nicht.");
            var caps = Capabilities(v.ChannelId);
            if (caps is null) return new ApiError(503, "shop_closed", "Signal verloren: Das Spiel ist gerade nicht verbunden.");
            if (!caps.Contains(e.Id)) return new ApiError(409, "event_unavailable", "Das Spiel kann diesen Eingriff gerade nicht.");

            var key = (v.ChannelId, v.Account);
            var wait = Remaining(_lastPurchase.TryGetValue(key, out var lp) ? lp : null, PurchaseGap, Now);
            if (wait > 0) return new ApiError(429, "rate_limited", "Nicht so schnell, Hacker.", wait);
            wait = Remaining(_eventLast.TryGetValue((v.ChannelId, e.Id), out var el) ? el : null, TimeSpan.FromSeconds(e.CooldownSec), Now);
            // Within 20 s of a purchase that has not run yet, others may join it: the director bundles them into one
            // stronger intervention (concept: "3 Hacker legen gleichzeitig die Reifen lahm").
            var joins = wait > 0 && _purchases.Values.Any(o => o.ChannelId == v.ChannelId && o.Event.Id == e.Id &&
                o.State is PurchaseState.Queued or PurchaseState.Accepted && Now - o.CreatedAt <= JoinWindow);
            if (wait > 0 && !joins) return new ApiError(409, "event_cooldown", $"{e.Name} lädt noch nach.", wait);
            wait = Remaining(_viewerEventLast.TryGetValue((v.ChannelId, v.Account, e.Id), out var ve) ? ve : null, TimeSpan.FromSeconds(e.ViewerCooldownSec), Now);
            if (wait > 0) return new ApiError(409, "viewer_cooldown", $"Du hast {e.Name} gerade erst ausgelöst.", wait);

            var price = PriceOf(v.ChannelId, e);
            if (_balances.GetValueOrDefault(key) < price) return new ApiError(402, "insufficient_coins", "Dafür reichen deine Coins noch nicht.");

            var p = new Purchase { Id = Guid.NewGuid(), ChannelId = v.ChannelId, Account = v.Account, ViewerName = v.Name, Event = e, Price = price, CreatedAt = Now };
            Book(v.ChannelId, v.Account, -price, LedgerKind.Purchase, p.Id.ToString(), e.Id);
            _purchases[p.Id] = p;
            _lastPurchase[key] = Now;
            if (!joins) _eventLast[(v.ChannelId, e.Id)] = Now;
            _viewerEventLast[(v.ChannelId, v.Account, e.Id)] = Now;
            _log.LogInformation("Kauf {Purchase}: {Event} für {Price} von {Name}", p.Id, e.Id, price, v.Name);
            return new { purchase = View(p), position = QueuePosition(p), balance = _balances[key] };
        }
    }

    private int QueuePosition(Purchase p) =>
        1 + _purchases.Values.Count(o => o.ChannelId == p.ChannelId && o.State is PurchaseState.Queued or PurchaseState.Accepted && o.CreatedAt < p.CreatedAt);

    private object View(Purchase p) => new
    {
        id = p.Id,
        eventId = p.Event.Id,
        eventName = p.Event.Name,
        category = Catalog.CategoryKey(p.Event.Category),
        viewer = p.ViewerName,
        price = p.Price,
        state = p.State.ToString().ToLowerInvariant(),
        reason = p.Reason,
        createdAt = p.CreatedAt,
        executedAt = p.ExecutedAt,
        endsAt = p.EndsAt,
    };

    private void Refund(Purchase p, string reason)
    {
        if (p.State is PurchaseState.Executed or PurchaseState.Refunded) return;
        Book(p.ChannelId, p.Account, p.Price, LedgerKind.Refund, p.Id.ToString(), reason);
        p.State = PurchaseState.Refunded;
        p.Reason = reason;
        // A refunded intervention did not happen, so it should not block the event for others.
        if (_eventLast.TryGetValue((p.ChannelId, p.Event.Id), out var t) && t == p.CreatedAt) _eventLast.Remove((p.ChannelId, p.Event.Id));
        _log.LogInformation("Kauf {Purchase} erstattet: {Reason}", p.Id, reason);
    }

    // ---------------------------------------------------------------- studio side

    /// <summary>Queued purchases not yet handed to this studio connection, oldest first.</summary>
    public IReadOnlyList<Purchase> TakeForStudio(string channel, string connectionId)
    {
        lock (_lock)
        {
            var list = _purchases.Values
                .Where(p => p.ChannelId == channel && p.State == PurchaseState.Queued && p.DispatchedTo != connectionId)
                .OrderBy(p => p.CreatedAt).ToList();
            foreach (var p in list) p.DispatchedTo = connectionId;
            return list;
        }
    }

    public void StudioAck(Guid id, bool accepted, string? reason)
    {
        lock (_lock)
        {
            if (!_purchases.TryGetValue(id, out var p)) return;
            if (!accepted) Refund(p, reason ?? "rejected");
            else if (p.State == PurchaseState.Queued) p.State = PurchaseState.Accepted;
        }
    }

    public void StudioResult(Guid id, bool ok, string? reason)
    {
        lock (_lock)
        {
            if (!_purchases.TryGetValue(id, out var p) || p.State is PurchaseState.Executed or PurchaseState.Refunded) return;
            if (!ok) { Refund(p, reason ?? "failed"); return; }
            Book(p.ChannelId, p.Account, 0, LedgerKind.Executed, p.Id.ToString(), p.Event.Id);
            p.State = PurchaseState.Executed;
            p.ExecutedAt = Now;
            p.EndsAt = p.Event.DurationSec > 0 ? Now.AddSeconds(p.Event.DurationSec) : null;
        }
    }

    public IReadOnlyList<string> ChannelsWithQueue()
    {
        lock (_lock) return _purchases.Values.Where(p => p.State == PurchaseState.Queued).Select(p => p.ChannelId).Distinct().ToList();
    }

    /// <summary>Studio connection lost: everything it had not finished goes back to the queue for the next one.</summary>
    public void StudioGone(string connectionId)
    {
        lock (_lock)
            foreach (var p in _purchases.Values.Where(p => p.DispatchedTo == connectionId && p.State is PurchaseState.Queued or PurchaseState.Accepted))
            {
                p.DispatchedTo = null;
                p.State = PurchaseState.Queued;
            }
    }

    /// <summary>Refunds what waited too long, drops old rounds and history. Called about once a second.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            foreach (var p in _purchases.Values.Where(p => p.State is PurchaseState.Queued or PurchaseState.Accepted && Now - p.CreatedAt > QueueTimeout).ToList())
                Refund(p, "timeout");
            foreach (var p in _purchases.Values.Where(p => p.State is PurchaseState.Executed or PurchaseState.Refunded && Now - p.CreatedAt > TimeSpan.FromHours(1)).ToList())
                _purchases.Remove(p.Id);
            _recent.RemoveAll(r => r.At < Now.AddHours(-24));
        }
    }

    /// <summary>Panic button: everything waiting is refunded. Returns how many.</summary>
    public int Panic(string channel)
    {
        lock (_lock)
        {
            var open = _purchases.Values.Where(p => p.ChannelId == channel && p.State is PurchaseState.Queued or PurchaseState.Accepted).ToList();
            foreach (var p in open) Refund(p, "panic");
            return open.Count;
        }
    }

    // ---------------------------------------------------------------- views

    public object Me(Viewer v)
    {
        lock (_lock)
        {
            Touch(v);
            var key = (v.ChannelId, v.Account);
            return new
            {
                name = v.Name,
                role = v.Role,
                dev = v.IsDev,
                balance = _balances[key],
                walletCap = WalletCap,
                hourlyCap = HourlyGameCap,
                hourlyEarned = GameCoinsLastHour(v.ChannelId, v.Account),
                purchases = _purchases.Values.Where(p => p.ChannelId == v.ChannelId && p.Account == v.Account)
                    .OrderByDescending(p => p.CreatedAt).Take(10)
                    .Select(p => new { purchase = View(p), position = p.State is PurchaseState.Queued or PurchaseState.Accepted ? QueuePosition(p) : (int?)null }),
            };
        }
    }

    public object Status(string channel, object studio)
    {
        lock (_lock)
        {
            var mine = _purchases.Values.Where(p => p.ChannelId == channel).ToList();
            var since = Now.AddHours(-12);
            var leaders = _recent.Where(r => r.ChannelId == channel && r.Kind == LedgerKind.Game && r.At > since)
                .GroupBy(r => r.Account)
                .Select(g => new { name = _names.GetValueOrDefault((channel, g.Key)) ?? "Anonymer Hacker", coins = g.Sum(r => r.Delta) })
                .OrderByDescending(x => x.coins).Take(10).ToList();
            return new
            {
                studio,
                shopOpen = Capabilities(channel) != null,
                queue = mine.Where(p => p.State is PurchaseState.Queued or PurchaseState.Accepted).OrderBy(p => p.CreatedAt).Select(View),
                active = mine.Where(p => p.State == PurchaseState.Executed && p.EndsAt > Now).OrderBy(p => p.EndsAt).Select(View),
                recent = mine.Where(p => p.State is PurchaseState.Executed or PurchaseState.Refunded).OrderByDescending(p => p.ExecutedAt ?? p.CreatedAt).Take(8).Select(View),
                leaderboard = leaders,
                serverTime = Now,
            };
        }
    }

    public int Balance(string channel, string account) { lock (_lock) return _balances.GetValueOrDefault((channel, account)); }
}
