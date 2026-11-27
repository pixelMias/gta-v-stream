namespace GtaStream.Ebs.Economy;

public enum EventCategory { Harmless, Disruptive, Chaotic, Surreal, Helpful, Stream }

/// <summary>
/// One intervention viewers can buy. Ids are game neutral (tyre_burst, not the native's name), so a later adapter for
/// another game can offer the same catalog. Times in seconds; DurationSec 0 means a one-off effect.
/// </summary>
public sealed record EventDef(
    string Id, string Name, string Description, EventCategory Category,
    int Cost, int DurationSec, int CooldownSec, int ViewerCooldownSec);

/// <summary>The MVP column of the event catalog in the concept (section 3).</summary>
public static class Catalog
{
    public static readonly IReadOnlyList<EventDef> All =
    [
        new("weather_storm", "Gewitter", "Das Wetter schlägt um: Blitz, Donner, nasse Straßen.", EventCategory.Harmless, 50, 180, 120, 60),
        new("time_midnight", "Mitternacht", "Die Uhr springt auf 0 Uhr und bleibt dort.", EventCategory.Harmless, 50, 180, 180, 60),
        new("screen_trip", "Drogenfilter", "Farben verzerren sich 30 Sekunden lang.", EventCategory.Harmless, 80, 30, 180, 60),
        new("confetti", "Konfetti", "Eine Konfetti-Explosion rund um die Figur.", EventCategory.Harmless, 60, 0, 60, 30),
        new("vehicle_pink", "Pink lackieren", "Das aktuelle Fahrzeug wird pink.", EventCategory.Harmless, 40, 0, 60, 30),
        new("name_banner", "Name als Banner", "Dein Name groß im Stream-Overlay.", EventCategory.Stream, 75, 6, 60, 60),

        new("wanted_up", "Fahndung +1", "Die Polizei interessiert sich plötzlich sehr.", EventCategory.Disruptive, 200, 0, 180, 300),
        new("tyre_burst", "Reifen platzt", "Ein Reifen des Fahrzeugs gibt auf.", EventCategory.Disruptive, 150, 0, 120, 120),
        new("engine_damage", "Motorschaden", "Der Motor qualmt, bis jemand repariert.", EventCategory.Disruptive, 250, 0, 300, 300),
        new("drunk", "Betrunken", "Die Figur torkelt 45 Sekunden lang.", EventCategory.Disruptive, 150, 45, 240, 240),
        new("emp", "EMP", "Alle Motoren im Umkreis gehen aus, die Ampeln spielen verrückt.", EventCategory.Disruptive, 300, 20, 300, 300),
        new("bounty_hunters", "Kopfgeldjäger", "Zwei Gegner im Auto nehmen die Verfolgung auf.", EventCategory.Disruptive, 350, 0, 300, 300),

        new("rocket_boost", "Raketen-Boost", "Das Auto schießt nach vorn.", EventCategory.Chaotic, 400, 0, 180, 300),
        new("riot", "Aufstand", "Passanten prügeln sich 45 Sekunden lang.", EventCategory.Chaotic, 800, 45, 900, 900),
        new("ragdoll", "Umfallen", "Die Figur fällt einfach um.", EventCategory.Chaotic, 300, 3, 180, 180),
        new("blackout", "Stadt-Blackout", "Nacht, alle Lichter der Stadt aus.", EventCategory.Chaotic, 600, 60, 600, 600),

        new("moon_gravity", "Mond-Schwerkraft", "60 Sekunden mit Mond-Schwerkraft.", EventCategory.Surreal, 1500, 60, 900, 900),
        new("slow_motion", "Zeitlupe", "30 Sekunden Zeitlupe.", EventCategory.Surreal, 800, 30, 600, 600),
        new("tpose_anomaly", "T-Pose-Anomalie", "Eine eingefrorene Gestalt mit Kloschüssel taucht auf.", EventCategory.Surreal, 900, 0, 600, 600),

        new("heal", "Heilen", "Gesundheit und Rüstung voll.", EventCategory.Helpful, 150, 0, 180, 180),
        new("vehicle_fix", "Reparieren", "Das Fahrzeug ist wieder wie neu.", EventCategory.Helpful, 200, 0, 180, 180),
        new("wanted_clear", "Fahndung löschen", "Die Polizei vergisst alles.", EventCategory.Helpful, 400, 0, 300, 300),
    ];

    private static readonly Dictionary<string, EventDef> ById = All.ToDictionary(e => e.Id);

    public static EventDef? Find(string id) => ById.GetValueOrDefault(id);

    public static string CategoryKey(EventCategory c) => c switch
    {
        EventCategory.Harmless => "harmless",
        EventCategory.Disruptive => "disruptive",
        EventCategory.Chaotic => "chaotic",
        EventCategory.Surreal => "surreal",
        EventCategory.Helpful => "helpful",
        _ => "stream",
    };
}
