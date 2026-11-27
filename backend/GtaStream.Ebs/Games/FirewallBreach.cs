namespace GtaStream.Ebs.Games;

/// <summary>
/// mulberry32, bit for bit the same as extension/src/games/prng.ts. The client animates with it, the server replays
/// the taps with it, so a round can be checked without trusting the client's score.
/// </summary>
public sealed class Mulberry32(uint seed)
{
    private uint _state = seed;

    public double Next()
    {
        _state += 0x6D2B79F5;
        var t = _state;
        t = (t ^ (t >> 15)) * (t | 1);
        t ^= t + (t ^ (t >> 7)) * (t | 61);
        return (t ^ (t >> 14)) / 4294967296.0;
    }
}

/// <summary>
/// Firewall Breach: a pointer turns, a tap inside the green sector is a hit. Each hit raises the level (faster pointer,
/// smaller sector, more coins), a miss drops back to level 1. After every tap the sector jumps to a new place.
/// Rules are mirrored in extension/src/games/firewall.ts; tests on both sides share the same vector.
/// </summary>
public static class FirewallBreach
{
    public const int DurationMs = 25_000;
    public const int MinTapGapMs = 150;     // faster is not human (concept: no reaction under 120 ms)
    public const int MaxRoundCoins = 50;
    public const double ToleranceDeg = 3;   // input and frame latency
    public const double WideFactor = 1.2;   // touch: hit windows 20 % wider

    public static readonly double[] SpeedDegPerSec = [150, 190, 230, 270, 310];
    public static readonly double[] SectorDeg = [70, 56, 44, 34, 26];

    public sealed record Result(bool Valid, string? Reason, int Hits, int Misses, int BestLevel, int Coins);

    public static Result Score(uint seed, bool wide, IReadOnlyList<int> tapsMs)
    {
        var rng = new Mulberry32(seed);
        var angle = rng.Next() * 360;
        var center = rng.Next() * 360;
        var level = 1;
        var lastT = 0;
        int hits = 0, misses = 0, best = 1, coins = 0;

        for (var i = 0; i < tapsMs.Count; i++)
        {
            var t = tapsMs[i];
            if (t < 0 || t > DurationMs) return new(false, "tap_out_of_round", 0, 0, 0, 0);
            if (i > 0 && t - lastT < MinTapGapMs) return new(false, "taps_too_fast", 0, 0, 0, 0);

            angle = (angle + SpeedDegPerSec[level - 1] * (t - lastT) / 1000.0) % 360;
            lastT = t;
            var half = SectorDeg[level - 1] * (wide ? WideFactor : 1) / 2 + ToleranceDeg;
            if (Distance(angle, center) <= half)
            {
                hits++;
                coins += level;
                level = Math.Min(5, level + 1);
                best = Math.Max(best, level);
            }
            else
            {
                misses++;
                level = 1;
            }
            center = rng.Next() * 360;
        }
        return new(true, null, hits, misses, best, Math.Min(coins, MaxRoundCoins));
    }

    public static double Distance(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);
}
