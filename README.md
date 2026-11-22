# GTA V Stream

Asymmetrisches Twitch-Extension-Spiel zu GTA V. Zuschauer sammeln in der Extension Coins und lösen damit Events im Spiel aus, ein KI-Showrunner führt Regie.

## Aufbau

| Ordner | Inhalt |
|---|---|
| `backend/GtaStream.Ebs` | Extension Backend Service (C# ASP.NET Core 8), später mit PostgreSQL |
| `backend/GtaStream.Ebs.Tests` | xUnit-Tests |
| `extension` | Extension-Frontend (TypeScript, Preact, Vite) |
| `deploy/cloud/bootstrap.sh` | Einmalige Einrichtung (Benutzer mit sudo, root-Login ist aus; der Server gehört GTA V Stream allein):

```
scp deploy/cloud/gtastream-bootstrap.sh <server>:
ssh -t <server> sudo bash gtastream-bootstrap.sh
```

Ohne Argument bleibt der vorhandene `deploy`-Benutzer mit seinem Schlüssel, wie er ist. Ein Schlüssel als Argument braucht über ssh doppelte Anführungszeichen: `ssh -t <server> "sudo bash gtastream-bootstrap.sh 'ssh-ed25519 AAAA… name'"`.
