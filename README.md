# GTA V Stream

Asymmetrisches Twitch-Extension-Spiel zu GTA V. Zuschauer sammeln in der Extension Coins und lösen damit Events im Spiel aus, ein KI-Showrunner führt Regie.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `backend/GtaStream.Ebs` | Extension Backend Service (C# ASP.NET Core 8) mit PostgreSQL |
| `backend/GtaStream.Ebs.Tests` | xUnit-Tests |
| `extension` | Extension-Frontend (TypeScript, Preact, Vite) |
| `studio/GtaStream.Studio` | Studio-Host auf dem Stream-PC: Verbindung zum EBS, Regie (Showrunner), OBS-Overlay und Stimme; Spielseite noch Fake |
| `studio/GtaStream.Studio.Tests` | xUnit-Tests der Regie |
| `docs/studio-protokoll.md` | Protokoll zwischen EBS und Studio-Host, Regeln der Regie |
| `deploy/cloud/gtastream-bootstrap.sh` | Einmalige Einrichtung des Testservers (siehe unten) |

## Was die Extension kann (Stand 27.11.2026)

- Anmeldung: in Twitch über das Extension-JWT (`GTASTREAM_TWITCH_SECRETS`, Base64, mehrere mit Komma), auf dem Testserver
  über einen Test-Login (`/api/v1/dev/session`, aus mit `GTASTREAM_DEV_LOGIN=off`). `?admin=<Admin-Token>` in der
  Adresse gibt die Streamer-Rolle mit Notabschaltung.
- Minispiel Firewall Breach: Der Server gibt einen Seed aus und spielt die Taps nach, der Client entscheidet keine Coins.
- Coins: 100 Startguthaben, höchstens 50 pro Runde, 600 pro Stunde aus Spielen, Wallet höchstens 5.000. Jede Buchung
  steht in der Tabelle `ledger` (nur anhängend), Kontostände sind Summen.
- Eingriffe: 22 Events aus der MVP-Spalte des Konzepts, Cooldowns pro Kanal und pro Zuschauer, 1 Kauf alle 10 s,
  dynamische Preise (+15 % je Kauf in 10 min), Erstattung bei Ablehnung, Zeitüberschreitung, Notabschaltung oder Neustart.
- Live-Ansicht: Warteschlange, laufende Effekte, letzte Eingriffe, Top-Hacker der letzten 12 Stunden.
- Layout per Container Queries: Panel (318 px), Handy und breite Desktop-Fläche.

## Lokal starten

```
dotnet run --project backend/GtaStream.Ebs          # EBS auf 127.0.0.1:5090, ohne GTASTREAM_DB im Speicher
(cd extension && npm ci && npm run dev)             # http://localhost:5173, /api wird zum EBS weitergeleitet
```

## Studio-Host auf dem Stream-PC

Schlüssel und Einstellungen stehen in `.env` im Repo-Ordner (steht in `.gitignore`, kommt nie ins Repo). Vorlage:

```
cp .env_example .env      # dann die Schlüssel eintragen
dotnet run --project studio/GtaStream.Studio
```

Gesetzte Umgebungsvariablen gehen vor, leere Werte zählen nicht. Ohne `ANTHROPIC_API_KEY` kommen die Texte aus
Vorlagen, ohne ElevenLabs und Piper gibt es nur Untertitel.

OBS: Quelle „Browser“, Adresse `http://127.0.0.1:7780/`, 1920 × 1080, Haken bei „Audio über OBS steuern“, dann
hört der Stream die Stimme und der Lautstärkeregler in OBS gilt. Zum Ansehen im normalen Browser `/?debug` anhängen.
Das Overlay zeigt, wer am Steuer ist, Spannung und Zielkurve, Banner zu jedem Eingriff und Untertitel zur Stimme.

Stimme: ElevenLabs (Flash) in den Premium-Stunden (`GTASTREAM_TTS_PREMIUM_HOURS`, Standard 18-22 Uhr Ortszeit), sonst
Piper lokal; fällt ElevenLabs aus, springt Piper ein. Höchstens alle 45 s ein Satz.

## Testserver einrichten

Einmalige Einrichtung (Benutzer mit sudo, root-Login ist aus; der Server gehört GTA V Stream allein):

```
scp deploy/cloud/gtastream-bootstrap.sh <server>:
ssh -t <server> sudo bash gtastream-bootstrap.sh
```

Ohne Argument bleibt der vorhandene `deploy`-Benutzer mit seinem Schlüssel, wie er ist. Ein Schlüssel als Argument braucht über ssh doppelte Anführungszeichen: `ssh -t <server> "sudo bash gtastream-bootstrap.sh 'ssh-ed25519 AAAA… name'"`.
