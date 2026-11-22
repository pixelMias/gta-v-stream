# GTA V Stream

Asymmetrisches Twitch-Extension-Spiel zu GTA V. Zuschauer sammeln in der Extension Coins und lösen damit Events im Spiel aus, ein KI-Showrunner führt Regie.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `backend/GtaStream.Ebs` | Extension Backend Service (C# ASP.NET Core 8), später mit PostgreSQL |
| `backend/GtaStream.Ebs.Tests` | xUnit-Tests |
| `extension` | Extension-Frontend (TypeScript, Preact, Vite) |
| `deploy/cloud/bootstrap.sh` | Einmalige Einrichtung des Testservers (root) |
| `.github/workflows/deploy.yml` | Testen bei jedem Push, Deploy bei Push auf `main` |

## Lokal

```
dotnet test GtaStream.sln
dotnet run --project backend/GtaStream.Ebs      # http://127.0.0.1:5090/api/health
cd extension && npm install && npm run dev      # leitet /api an den EBS weiter
```

## Testserver

Push auf `main` → GitHub Actions testet, baut ein Release, lädt es als Benutzer `deploy` per SSH hoch und ruft `/opt/gtastream/bin/activate <commit>` auf. Das Skript schaltet um, startet `gtastream-ebs` neu und rollt zurück, wenn `/api/health` den neuen Commit nicht binnen 2 Minuten meldet.

Benötigte Repository-**Secrets** (nicht Variables): `DEPLOY_HOST`, `DEPLOY_SSH_KEY`, `DEPLOY_KNOWN_HOSTS`.

Endpunkte:

- `GET /api/health` → `{"status":"ok","commit":"<sha>","startedAt":"…","db":"ok|down|off"}`
- `GET /api/logs?limit=200&level=Warning&after=<id>` mit Header `X-Admin-Token` → die neuesten Logzeilen aus dem Speicher (Token steht in `/etc/gtastream/env`).

Einmalige Einrichtung (nur root):

```
ssh root@<IP> 'bash -s' < deploy/cloud/bootstrap.sh "<öffentlicher Deploy-Schlüssel>"
```
