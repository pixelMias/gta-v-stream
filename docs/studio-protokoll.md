# Studio-Protokoll (EBS ↔ Studio-Host), Version 1

Der Studio-Host auf dem Stream-PC baut die Verbindung **ausgehend** auf, zu Hause sind keine offenen Ports nötig.

```
wss://<ebs>/api/v1/studio
Authorization: Bearer <GTASTREAM_STUDIO_TOKEN>   (Testserver: ohne eigenes Token gilt das Admin-Token)
```

Pro Kanal gilt genau ein Host. Verbindet sich ein zweiter, trennt das EBS den alten mit Close-Grund `replaced`.
Jede Nachricht ist ein JSON-Objekt mit `type`.

## EBS → Host

| type | Felder | Bedeutung |
|---|---|---|
| `welcome` | `channelId`, `protocol` | direkt nach dem Verbinden |
| `intervention` | `id`, `eventId`, `category`, `durationSec`, `viewer`, `price`, `createdAt`, `deadline` | ein bezahlter Eingriff. Die Regie auf dem Host entscheidet, wann er läuft, spätestens bis `deadline`, sonst erstattet das EBS |
| `panic` | – | Notabschaltung: alle Effekte sofort beenden. Die Warteschlange hat das EBS schon erstattet |
| `pong` | – | Antwort auf `ping` |
| `error` | `code` | z. B. `bad_json`, `unknown_type` |

Nach einem Verbindungsabbruch schickt das EBS alle noch offenen Eingriffe erneut. Der Host muss also doppelte `id`s erkennen.

## Host → EBS

| type | Felder | Bedeutung |
|---|---|---|
| `hello` | `game`, `capabilities` (Liste von Event-IDs) | gleich nach `welcome`. Die Extension bietet nur Events an, die hier stehen |
| `state` | `state` (kleines Objekt) | etwa alle 2 s, z. B. `inVehicle`, `wanted`, `cutscene`, `paused`, `mode`. Wird in der Extension angezeigt |
| `ack` | `id`, `accepted`, `reason?` | angenommen oder abgelehnt (`not_in_vehicle`, `cutscene`, …). Abgelehnt heißt: Coins zurück |
| `result` | `id`, `ok`, `reason?` | ausgeführt (`ok: true`) oder gescheitert (Coins zurück) |
| `ping` | – | Lebenszeichen, das EBS antwortet mit `pong` |

Event-IDs sind spielneutral (`tyre_burst`, nicht der Native-Name), der Katalog steht in
`backend/GtaStream.Ebs/Economy/Catalog.cs`.

## Ablauf eines Kaufs

1. Zuschauer kauft in der Extension, das EBS bucht die Coins ab (Ledger: `purchase`) und stellt ihn in die Warteschlange.
2. Das EBS schickt `intervention` an den Host.
3. Host antwortet mit `ack`, die Regie hält den Eingriff ggf. zurück (höchstens 60 s), dann führt der Mod ihn aus.
4. Host meldet `result`. Das EBS bucht `executed` (0 Coins) oder `refund`.
5. Kommt binnen 90 s nach dem Kauf kein `result`, erstattet das EBS von selbst.

## Ohne Host

Ist kein Host verbunden, spielt auf dem Testserver ein eingebauter Simulator die Spielseite (Badge „Simulation“ in der
Extension). Er hält chaotische und surreale Eingriffe mindestens 90 s auseinander und erstattet, was länger als 60 s
warten müsste. Mit `GTASTREAM_STUDIO_SIM=off` ist der Shop ohne Host geschlossen („Signal verloren“).

## Referenz-Client

`studio/GtaStream.Studio` ist der Anfang des Studio-Hosts: Verbindung mit Wiederverbinden, `hello`, `state`, `ack`,
`result`, `panic`. Die Spielseite ist hinter `IGameAdapter` versteckt, heute mit einem `FakeAdapter`. Der echte Adapter
spricht später per lokalem WebSocket mit dem ScriptHookVDotNet-Mod.

```
GTASTREAM_EBS=wss://2-29-164-176.sslip.io/api/v1/studio GTASTREAM_STUDIO_TOKEN=<admin-token> dotnet run --project studio/GtaStream.Studio
```
