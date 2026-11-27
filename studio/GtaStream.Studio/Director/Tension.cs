namespace GtaStream.Studio.Director;

/// <summary>
/// Tension from 0 to 100, computed every tick from the game state (concept, section 4, "Spannungswert"). The weights
/// are starting values, to be tuned after the first streams.
/// </summary>
public static class Tension
{
    public static int Compute(GameState s, int activeEffects, TimeSpan sinceLastEvent)
    {
        double t = s.WantedLevel * 10;
        if (s.SpeedKmh > 100) t += 15;
        if (s.HealthPct < 40) t += 15;
        t += activeEffects * 10;
        if (s.MissionActive) t += 10;
        t -= Math.Min(30, sinceLastEvent.TotalSeconds / 10);
        return (int)Math.Clamp(Math.Round(t), 0, 100);
    }
}

/// <summary>
/// Where tension should be: a sawtooth of 10 min build-up (20 → 80), 1 min peak (90) and 3 min breather (15),
/// 14 min per cycle (concept: 8–12 min build-up, short peak, 2–3 min pause).
/// </summary>
public static class TargetCurve
{
    public static readonly TimeSpan BuildUp = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Peak = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Breather = TimeSpan.FromMinutes(3);
    public static TimeSpan Cycle => BuildUp + Peak + Breather;

    public static int At(TimeSpan sinceStart)
    {
        var t = TimeSpan.FromTicks(sinceStart.Ticks % Cycle.Ticks);
        if (t < BuildUp) return (int)Math.Round(20 + 60 * t / BuildUp);
        if (t < BuildUp + Peak) return 90;
        return 15;
    }

    public static string Phase(TimeSpan sinceStart)
    {
        var t = TimeSpan.FromTicks(sinceStart.Ticks % Cycle.Ticks);
        return t < BuildUp ? "build_up" : t < BuildUp + Peak ? "peak" : "breather";
    }
}
