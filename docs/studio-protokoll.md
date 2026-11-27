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
| `intervention` | `id`, `eventId`, `eventName`, `category`, `durationSec`, `viewer`, `price`, `createdAt`, `deadline` | ein bezahlter Eingriff. Die Regie auf dem Host entscheidet, wann er läuft, spätestens bis `deadline`, sonst erstattet das EBS |
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

## Studio-Host

`studio/GtaStream.Studio` verbindet sich mit Wiederverbinden, meldet `hello` und `state` und gibt jeden Eingriff an die
Regie (`Director/Director.cs`, Klasse `Showrunner`). Die Spielseite steckt hinter `IGameAdapter`, heute ein
`FakeAdapter`; der echte Adapter spricht später per lokalem WebSocket mit dem ScriptHookVDotNet-Mod.

```
GTASTREAM_EBS=wss://2-29-164-176.sslip.io/api/v1/studio GTASTREAM_STUDIO_TOKEN=<admin-token> dotnet run --project studio/GtaStream.Studio
```

Mit `ANTHROPIC_API_KEY` schreibt Claude Banner und Sprachzeilen (`GTASTREAM_LLM_MODEL`, Standard `claude-haiku-5-5`),
sonst kommen sie aus Vorlagen.

## Regie (Showrunner)

Code entscheidet, ob und wann; das LLM nur, wie es erzählt wird (Konzept, Abschnitt 4).

1. **Annahme (`ack`):** abgelehnt wird nur, was das Spiel nicht kann (`unsupported`), was gerade nicht passt
   (`not_in_vehicle`) oder was beim Beifahrer-Modus nicht freigegeben ist (`human_driving`, erlaubt: harmlos, helfend,
   Stream).
2. **Sammeln:** 3 s warten, gleiche Eingriffe binnen 20 s werden gebündelt und laufen einmal mit Stärke n. Das EBS
   lässt dafür andere Zuschauer binnen 20 s „mitmachen“, trotz Event-Cooldown.
3. **Harte Sperren** (gelten auch kurz vor Ablauf): Spiel weg, Zwischensequenz, Pause oder Laden, Modus Pause,
   höchstens 3 laufende Effekte, chaotisch und surreal höchstens alle 90 s, 4 s Abstand zwischen zwei Eingriffen.
4. **Takt:** Spannungswert 0–100 (Fahndung × 10, über 100 km/h +15, Gesundheit unter 40 % +15, aktive Effekte × 10,
   Mission +10, −1 je 10 s seit dem letzten Eingriff, höchstens −30) gegen eine Sägezahnkurve (10 min Aufbau 20→80,
   1 min Gipfel 90, 3 min Pause 15). Liegt die Spannung mehr als 10 über der Kurve, warten störende, chaotische und
   surreale Eingriffe; helfende und harmlose laufen. 8 s vor Ablauf gibt der Takt nach.
5. **Erzählen:** Banner (höchstens 80 Zeichen) immer, Sprachzeile (höchstens 200) höchstens alle 45 s. LLM-Antworten
   werden auf Form, Länge und Wortfilter geprüft, sonst Vorlage. Zuschauernamen laufen durch den Wortfilter.
6. **Ergebnis (`result`):** für jeden gebündelten Kauf. Was bis 4 s vor Ablauf nicht laufen konnte, meldet die Regie
   als `director_hold_timeout`, das EBS erstattet.

Bezahlte Eingriffe werden nur verzögert, nie durch etwas anderes ersetzt.

## Overlay (Studio-Host → OBS)

Der Studio-Host öffnet auf `GTASTREAM_OVERLAY_URL` (Standard `http://127.0.0.1:7780`, nur lokal) eine Seite für die
OBS-Browserquelle und den WebSocket `/ws`. Nachrichten (JSON, `type`):

| type | Felder | Bedeutung |
|---|---|---|
| `status` | `mode`, `ebsConnected`, `tension`, `target`, `phase`, `queue` | jede Sekunde: Wer steuert, Spannung gegen Zielkurve |
| `banner` | `text`, `category` | ein Eingriff läuft, Banner 5 s, höchstens 3 gleichzeitig |
| `say` | `text`, `audio` (oder null) | Untertitel; `audio` ist `/audio/<id>` (MP3 von ElevenLabs oder WAV von Piper, die letzten 20 bleiben abrufbar) |

Die Seite spielt Audio nacheinander ab und verbindet sich nach einem Abbruch neu.
