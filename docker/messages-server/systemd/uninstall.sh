#!/bin/sh
# Removes Messages Server installed by install.sh. Run it as root:
#
#   sudo /opt/messages-server/uninstall.sh            the service, the nightly update and the program; the data, keys, backups and settings stay
#   sudo /opt/messages-server/uninstall.sh --data     also the data folder: sign-ins, paired devices, the server's stores and log
#   sudo /opt/messages-server/uninstall.sh --keys     also the keys, and with them this server's Messenger history and its Tailscale identity
#   sudo /opt/messages-server/uninstall.sh --all      the data, the keys, the settings and the account messages-server
#   sudo /opt/messages-server/uninstall.sh --backups  also the server's own backups (never removed otherwise, not even by --all)
#
# The data is useless without the keys and the keys hold the Messenger history, so keep both or remove both. Nothing
# here signs out at a service: remove the accounts first (Settings → Messages Server → Manage server) to sign the server
# out of them, and unpair it on the phone for Google Messages. Google Chrome, when install.sh --google put it here, stays
# installed (apt-get remove google-chrome-stable takes it away); its policy for the server goes.
set -eu

data=no; keys=no; everything=no; backups=no
for argument in "$@"; do
    case "$argument" in
        --data) data=yes ;;
        --keys) keys=yes ;;
        --backups) backups=yes ;;
        --all) data=yes; keys=yes; everything=yes ;;
        *) echo "uninstall.sh: it reads --data, --keys, --backups and --all only. Nothing was removed." >&2; exit 2 ;;
    esac
done
[ "$(id -u)" -eq 0 ] || { echo "uninstall.sh: run it as root." >&2; exit 1; }

units=/etc/systemd/system
systemctl disable --now messages-server-update.timer 2>/dev/null || true
if [ -e "$units/messages-server.service" ]; then
    echo "Stopping Messages Server (pending sends finish first; up to 60 s)..."
    systemctl disable --now messages-server 2>/dev/null || true
fi
rm -f "$units/messages-server.service" "$units/messages-server-update.service" "$units/messages-server-update.timer"
rm -rf "$units/messages-server.service.d"
systemctl daemon-reload
rm -f /etc/opt/chrome/policies/managed/messages-server.json
rm -rf /opt/messages-server /opt/messages-server.new /opt/messages-server.old
echo "Removed the service, the nightly update and /opt/messages-server."

if [ "$data" = yes ]; then rm -rf /var/lib/messages-server/data; echo "Removed the data folder."; fi
if [ "$keys" = yes ]; then rm -rf /var/lib/messages-server/keys; echo "Removed the keys."; fi
if [ "$backups" = yes ]; then
    # A folder mounted from elsewhere (a Proxmox host's) is emptied, not removed.
    if [ -d /var/lib/messages-server/backups ]; then find /var/lib/messages-server/backups -mindepth 1 -delete; fi
    rmdir /var/lib/messages-server/backups 2>/dev/null || true
    echo "Removed the backups."
fi
if [ "$data" = yes ] && [ "$keys" = yes ]; then rmdir /var/lib/messages-server 2>/dev/null || true; fi
if [ "$everything" = yes ]; then
    rm -rf /etc/messages-server
    if getent passwd messages-server >/dev/null 2>&1; then userdel messages-server 2>/dev/null || true; fi
    if getent group messages-server >/dev/null 2>&1; then groupdel messages-server 2>/dev/null || true; fi
    echo "Removed the settings and the account."
fi
if [ -d /var/lib/messages-server/backups ]; then echo "Kept the backups in /var/lib/messages-server/backups."; fi
if [ "$data" = no ]; then echo "Kept /var/lib/messages-server and /etc/messages-server; install.sh picks them up again."; fi
exit 0
