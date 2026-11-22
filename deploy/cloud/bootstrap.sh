#!/usr/bin/env bash
# GTA V Stream test server: one-time setup of a fresh Ubuntu 24.04 VM (x86_64 or arm64), run as root.
#
#   ssh root@<IP> 'bash -s' < deploy/cloud/bootstrap.sh "<public deploy key>" [domain]
#
# Installs Postgres 16, the ASP.NET Core 8 runtime and Caddy (automatic HTTPS) from the Ubuntu archive, creates the users
# gtastream (runs the EBS) and deploy (GitHub Actions uploads releases), the database, the systemd unit, the firewall
# and a daily database backup. Without a domain the server is reachable at <ip-with-dashes>.sslip.io, which resolves to
# the IP and gets a real certificate. Safe to run again: existing passwords, tokens and releases are kept.
set -euo pipefail

DEPLOY_KEY="${1:?usage: bootstrap.sh \"<public deploy key>\" [domain]}"
IP=$(ip -4 route get 1.1.1.1 | awk '{for (i = 1; i < NF; i++) if ($i == "src") print $(i + 1)}')
DOMAIN="${2:-${IP//./-}.sslip.io}"
ROOT=/opt/gtastream
ENVFILE=/etc/gtastream/env
CADDYFILE=/etc/caddy/Caddyfile
MARKER="# gtastream: written by deploy/cloud/bootstrap.sh"
export DEBIAN_FRONTEND=noninteractive

# Do not overwrite a Caddyfile another project wrote (for example on a shared server).
if [ -f "$CADDYFILE" ] && ! grep -qF "$MARKER" "$CADDYFILE" && ! grep -q "/usr/share/caddy" "$CADDYFILE"; then
  echo "ABBRUCH: $CADDYFILE stammt von einem anderen Projekt. Bitte eigenen Server nehmen oder den Block von Hand ergaenzen." >&2
  exit 1
fi

echo "== Pakete"
apt-get update -q
apt-get install -y -q ca-certificates curl openssl postgresql aspnetcore-runtime-8.0 caddy ufw unattended-upgrades

echo "== Firewall"
ufw allow OpenSSH >/dev/null; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
ufw --force enable >/dev/null

echo "== Benutzer und Verzeichnisse"
id gtastream >/dev/null 2>&1 || useradd --system --home "$ROOT" --shell /usr/sbin/nologin gtastream
id deploy >/dev/null 2>&1 || useradd --create-home --shell /bin/bash deploy
install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
grep -qxF "$DEPLOY_KEY" /home/deploy/.ssh/authorized_keys 2>/dev/null || echo "$DEPLOY_KEY" >> /home/deploy/.ssh/authorized_keys
chown deploy:deploy /home/deploy/.ssh/authorized_keys; chmod 600 /home/deploy/.ssh/authorized_keys
install -d -m 755 -o deploy -g deploy "$ROOT" "$ROOT/releases" "$ROOT/incoming"
install -d -m 755 "$ROOT/bin" /etc/gtastream
install -d -m 750 -o postgres -g postgres /var/backups/gtastream

echo "== Datenbank und Secrets"
if [ ! -f "$ENVFILE" ]; then
  DBPASS=$(openssl rand -hex 24)
  ADMIN=$(openssl rand -hex 16)
  sudo -u postgres psql -v ON_ERROR_STOP=1 -q -c "create role gtastream login password '$DBPASS';"
  sudo -u postgres psql -v ON_ERROR_STOP=1 -q -c "create database gtastream owner gtastream;"
  cat > "$ENVFILE" <<EOF
# GTA V Stream test server. Changes: sudo systemctl restart gtastream-ebs
# Unprefixed so it wins over "Urls" in appsettings.json.
Urls=http://127.0.0.1:5090
GTASTREAM_DB=Host=localhost;Username=gtastream;Password=$DBPASS;Database=gtastream
# Header X-Admin-Token for /api/logs and later test inputs.
GTASTREAM_ADMIN_TOKEN=$ADMIN
EOF
fi
chown root:gtastream "$ENVFILE"; chmod 640 "$ENVFILE"
echo "DOMAIN=$DOMAIN" > /etc/gtastream/domain

echo "== Dienst"
cat > /etc/systemd/system/gtastream-ebs.service <<'EOF'
[Unit]
Description=GTA V Stream EBS (Testserver)
After=network-online.target postgresql.service
Wants=network-online.target postgresql.service

[Service]
# notify: ready once Kestrel listens.
Type=notify
User=gtastream
WorkingDirectory=/opt/gtastream/current/server
ExecStart=/usr/bin/dotnet /opt/gtastream/current/server/GtaStream.Ebs.dll
EnvironmentFile=/etc/gtastream/env
Environment=ASPNETCORE_ENVIRONMENT=Production DOTNET_CLI_TELEMETRY_OPTOUT=1
Restart=always
RestartSec=5
TimeoutStopSec=30
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true

[Install]
WantedBy=multi-user.target
EOF

cat > "$CADDYFILE" <<EOF
$MARKER
$DOMAIN {
	encode zstd gzip
	handle /api/* {
		reverse_proxy 127.0.0.1:5090
	}
	handle {
		root * $ROOT/current/web
		try_files {path} /index.html
		file_server
	}
	header {
		Strict-Transport-Security "max-age=31536000"
		X-Content-Type-Options nosniff
		Referrer-Policy no-referrer
	}
}
EOF
chown root:caddy "$CADDYFILE"; chmod 640 "$CADDYFILE"

# Activation script for the deploy user: unpack, switch, restart, check health, roll back on failure.
cat > "$ROOT/bin/activate" <<'EOF'
#!/usr/bin/env bash
# Usage (as deploy): activate <commit>   – expects /opt/gtastream/incoming/<commit>.tar.gz
set -euo pipefail
SHA="${1:?commit}"; ROOT=/opt/gtastream; REL="$ROOT/releases/$SHA"
PREV=$(readlink -f "$ROOT/current" 2>/dev/null || true)
rm -rf "$REL"; mkdir -p "$REL"
tar -xzf "$ROOT/incoming/$SHA.tar.gz" -C "$REL"; rm -f "$ROOT/incoming/$SHA.tar.gz"
ln -sfn "$REL" "$ROOT/current.new" && mv -T "$ROOT/current.new" "$ROOT/current"
sudo /usr/bin/systemctl restart gtastream-ebs
for _ in $(seq 1 60); do
  if curl -fs http://127.0.0.1:5090/api/health | grep -q "\"commit\":\"$SHA\""; then
    echo "OK: $SHA laeuft"
    ls -1dt "$ROOT"/releases/* | tail -n +6 | xargs -r rm -rf   # keep the last five releases
    exit 0
  fi
  sleep 2
done
echo "FEHLER: $SHA wird nicht gesund – zurueck auf ${PREV:-nichts}" >&2
sudo /usr/bin/journalctl -u gtastream-ebs -n 40 --no-pager >&2 || true
if [ -n "$PREV" ] && [ "$PREV" != "$REL" ]; then
  ln -sfn "$PREV" "$ROOT/current.new" && mv -T "$ROOT/current.new" "$ROOT/current"
  sudo /usr/bin/systemctl restart gtastream-ebs
fi
exit 1
EOF
chmod 755 "$ROOT/bin/activate"
cat > /etc/sudoers.d/gtastream-deploy <<'EOF'
deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart gtastream-ebs, /usr/bin/journalctl -u gtastream-ebs *
EOF
chmod 440 /etc/sudoers.d/gtastream-deploy; visudo -cq

echo "== Backup (taeglich, 14 Tage)"
cat > /etc/systemd/system/gtastream-backup.service <<'EOF'
[Unit]
Description=GTA V Stream Datenbank-Backup
[Service]
Type=oneshot
User=postgres
ExecStart=/bin/sh -c 'pg_dump -Fc gtastream > /var/backups/gtastream/gtastream-$(date +%%F).dump && find /var/backups/gtastream -name "*.dump" -mtime +14 -delete'
EOF
cat > /etc/systemd/system/gtastream-backup.timer <<'EOF'
[Unit]
Description=GTA V Stream Datenbank-Backup taeglich
[Timer]
OnCalendar=*-*-* 04:27
Persistent=true
[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable gtastream-ebs >/dev/null
systemctl enable --now gtastream-backup.timer >/dev/null
systemctl reload caddy 2>/dev/null || systemctl restart caddy

echo
echo "Fertig. Adresse: https://$DOMAIN"
echo "Admin-Token fuer /api/logs: sudo grep ADMIN_TOKEN $ENVFILE"
echo "Der EBS startet mit dem ersten Deploy aus GitHub Actions."
echo "Known-Hosts-Zeile fuer das GitHub-Secret DEPLOY_KNOWN_HOSTS:"
ssh-keyscan -t ed25519 "$IP" 2>/dev/null || true
