#!/bin/sh
# Installs or updates Messages Server from this folder (the extracted messages-server-<version>-linux-x64.tar.gz) on a
# Linux host with systemd and no Docker: Ubuntu, Debian, a Proxmox LXC. Run it as root from anywhere:
#
#   sudo ./install.sh
#
# What it does, and nothing else:
#   - makes the system account messages-server (no login, no home of its own) if there is none;
#   - puts the server and its helpers in /opt/messages-server, owned by root so the account cannot change them;
#   - makes /etc/messages-server/environment (root only) for the settings, unless it is already there;
#   - installs messages-server.service and starts it. systemd makes /var/lib/messages-server/data and .../keys, private
#     to the account, the way the container's two volumes are (README.md, "Where things live").
# An update stops the running server first (it finishes pending sends; up to 60 s), replaces /opt/messages-server and
# starts it again, with the same data, keys, sign-ins and paired devices. uninstall.sh takes it away again.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
app=/opt/messages-server
settings=/etc/messages-server

fail() { echo "install.sh: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "run it as root (sudo ./install.sh)."
command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ] || fail "this host does not run systemd; use the container instead (README.md)."
[ "$(uname -m)" = x86_64 ] || fail "this package is for x86-64 Linux; this host is $(uname -m)."
[ -x "$here/messages-server" ] || fail "run it from the extracted package: $here has no messages-server."
for helper in messenger-helper instagram-helper; do
    [ -x "$here/$helper" ] || fail "$helper is missing from $here; extract the package again."
done

# .NET is in the package, but it uses the host's ICU, OpenSSL 3, CA certificates and time zones, as the images install
# them (Dockerfile.google). A minimal Debian, Fedora or LXC template can lack ICU, and the server then stops at its
# first line. What is missing is installed with the host's own package manager, and named.
missing=""
ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so' || missing="$missing icu"
ldconfig -p 2>/dev/null | grep -q 'libssl\.so\.3' || missing="$missing openssl"
# A bundle (Debian, Ubuntu) or OpenSSL's hashed links (Fedora 44 has only these).
[ -s /etc/ssl/certs/ca-certificates.crt ] || [ -s /etc/pki/tls/certs/ca-bundle.crt ] || ls /etc/ssl/certs/*.0 >/dev/null 2>&1 || missing="$missing certificates"
[ -d /usr/share/zoneinfo ] || missing="$missing tzdata"
if [ -n "$missing" ]; then
    echo "Installing what the server needs from the system:$missing"
    if command -v apt-get >/dev/null 2>&1; then
        apt-get update -qq
        packages="ca-certificates tzdata"
        # The ICU and OpenSSL packages carry their version in their names (libicu72 to 76, libssl3 or libssl3t64).
        case "$missing" in *icu*) icu=$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+(t64)?$' | sort -V | tail -n 1)
            [ -n "$icu" ] || fail "no ICU package (libicuNN) is offered here; install ICU, then run install.sh again."
            packages="$packages $icu" ;; esac
        case "$missing" in *openssl*) packages="$packages libssl3" ;; esac
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends $packages
    elif command -v dnf >/dev/null 2>&1; then
        dnf install -y -q libicu openssl-libs ca-certificates tzdata
    else
        fail "install ICU, OpenSSL 3, CA certificates and time zone data with this system's package manager, then run install.sh again."
    fi
fi

if ! getent passwd messages-server >/dev/null 2>&1; then
    # The container's account is 2787:2787; take the same numbers when they are free, so a data folder carried over from
    # the container keeps its owner. Otherwise the system picks.
    if getent passwd 2787 >/dev/null 2>&1 || getent group 2787 >/dev/null 2>&1; then
        useradd --system --user-group --no-create-home --home-dir /var/lib/messages-server/data --shell /usr/sbin/nologin messages-server
    else
        groupadd --system --gid 2787 messages-server
        useradd --system --uid 2787 --gid 2787 --no-create-home --home-dir /var/lib/messages-server/data --shell /usr/sbin/nologin messages-server
    fi
    echo "Made the account messages-server."
fi

running=no
if systemctl is-active --quiet messages-server 2>/dev/null; then
    running=yes
    echo "Stopping the running server (pending sends finish first; up to 60 s)..."
    systemctl stop messages-server
fi

# The new copy goes in beside the old one and takes its place in one rename, so a failed copy leaves the old one whole.
rm -rf "$app.new"
mkdir -p "$app.new"
( cd "$here" && tar -cf - --exclude=./install.sh --exclude=./uninstall.sh --exclude=./messages-server.service . ) | ( cd "$app.new" && tar -xf - --no-same-owner )
chown -R root:root "$app.new"
chmod -R u=rwX,go=rX "$app.new"
chmod 0755 "$app.new/messages-server" "$app.new/messenger-helper" "$app.new/instagram-helper"
rm -rf "$app.old"
[ -d "$app" ] && mv "$app" "$app.old"
mv "$app.new" "$app"
rm -rf "$app.old"

if [ ! -e "$settings/environment" ]; then
    mkdir -p "$settings"
    chmod 0755 "$settings"
    umask 077
    cat > "$settings/environment" <<'EOF'
# Messages Server's settings, read by messages-server.service when it starts (systemctl restart messages-server after a
# change). Root only: the page's password is in it. Remove the # to use a line.

# The names and addresses devices reach this server by, written into its certificate on the first start (what a device
# trusts is the fingerprint, not these names).
#MESSAGES_HOSTNAMES=server.lan,192.168.1.20

# The one HTTPS port (default 8787).
#MESSAGES_PORT=8787

# The password of the server's own page (https://<this server>:8787/ in a browser): 12 characters or more, used nowhere
# else. Empty leaves the page off.
#MESSAGES_ADMIN_PASSWORD=

# Time zone for log timestamps.
#TZ=Etc/UTC
EOF
    chmod 0600 "$settings/environment"
    echo "Made $settings/environment for the settings (all off; see the comments in it)."
fi

install -m 0644 "$here/messages-server.service" /etc/systemd/system/messages-server.service
cp "$here/uninstall.sh" "$app/uninstall.sh"
chmod 0755 "$app/uninstall.sh"
systemctl daemon-reload
systemctl enable --quiet messages-server
systemctl start messages-server

sleep 3
if systemctl is-active --quiet messages-server; then
    [ "$running" = yes ] && echo "Messages Server updated and running." || echo "Messages Server installed and running."
else
    echo "Messages Server did not stay running. Its last lines:" >&2
    journalctl -u messages-server -n 20 --no-pager >&2 || true
    exit 1
fi
echo
echo "Pairing code and fingerprint (the code only while no device is paired; it works once, for 10 minutes):"
echo "  journalctl -u messages-server --no-pager | grep -E 'pairing code=|tls fingerprint'"
echo "The server's log: /var/lib/messages-server/data/server.log, or journalctl -u messages-server -f"
