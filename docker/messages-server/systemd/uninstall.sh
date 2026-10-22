#!/bin/sh
# Removes Messages Server installed by install.sh. Run it as root:
#
#   sudo /opt/messages-server/uninstall.sh            the service and the program; the data, keys and settings stay
#   sudo /opt/messages-server/uninstall.sh --data     also the data folder: sign-ins, paired devices, the server's stores and log
#   sudo /opt/messages-server/uninstall.sh --keys     also the keys, and with them this server's Messenger history
#   sudo /opt/messages-server/uninstall.sh --all      all of the above, the settings and the account messages-server
#
# The data is useless without the keys and the keys hold the Messenger history, so keep both or remove both. Nothing
# here signs out at a service: remove the accounts first (Settings → Messages Server → Manage server) to sign the server
# out of them, and unpair it on the phone for Google Messages.
set -eu

data=no; keys=no; everything=no
for argument in "$@"; do
    case "$argument" in
        --data) data=yes ;;
        --keys) keys=yes ;;
        --all) data=yes; keys=yes; everything=yes ;;
        *) echo "uninstall.sh: it reads --data, --keys and --all only. Nothing was removed." >&2; exit 2 ;;
    esac
done
[ "$(id -u)" -eq 0 ] || { echo "uninstall.sh: run it as root." >&2; exit 1; }

if [ -e /etc/systemd/system/messages-server.service ]; then
    echo "Stopping Messages Server (pending sends finish first; up to 60 s)..."
    systemctl disable --now messages-server 2>/dev/null || true
    rm -f /etc/systemd/system/messages-server.service
    systemctl daemon-reload
fi
rm -rf /opt/messages-server /opt/messages-server.new /opt/messages-server.old
echo "Removed the service and /opt/messages-server."

[ "$data" = yes ] && { rm -rf /var/lib/messages-server/data; echo "Removed the data folder."; }
[ "$keys" = yes ] && { rm -rf /var/lib/messages-server/keys; echo "Removed the keys."; }
if [ "$data" = yes ] && [ "$keys" = yes ]; then rmdir /var/lib/messages-server 2>/dev/null || true; fi
if [ "$everything" = yes ]; then
    rm -rf /etc/messages-server
    getent passwd messages-server >/dev/null 2>&1 && userdel messages-server 2>/dev/null || true
    getent group messages-server >/dev/null 2>&1 && groupdel messages-server 2>/dev/null || true
    echo "Removed the settings and the account."
fi
[ "$data" = no ] && echo "Kept /var/lib/messages-server and /etc/messages-server; install.sh picks them up again."
exit 0
