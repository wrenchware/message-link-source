#!/bin/sh
# Installs or updates Message Link Server from this folder (the extracted messages-server-<version>-linux-x64.tar.gz) on a
# Linux host with systemd and no Docker: Ubuntu, Debian, a Proxmox LXC. Run it as root from anywhere:
#
#   sudo ./install.sh              Messenger and Instagram (and Google, if an earlier install turned it on)
#   sudo ./install.sh --google     with official Google Chrome (Debian, Ubuntu), in which the page connects accounts: without it, accounts only arrive by a move from a PC
#   sudo ./install.sh --no-google  turns Google off again; Chrome stays installed
#   sudo ./install.sh --google --allow-host-sign-in  permits sign-in on a trusted Linux host's shared network
#
# What it does, and nothing else:
#   - makes the system account messages-server (no login, no home of its own) if there is none;
#   - puts the server, its helpers and its tailnet door in /opt/messages-server, owned by root so the account cannot
#     change them, and links mlink (Message Link in a terminal) into /usr/local/bin;
#   - makes /etc/messages-server/environment (root only) for the settings, unless it is already there, and
#     /etc/messages-server/update for the nightly update's release address (empty: off);
#   - with --google: installs Google Chrome from Google's own repository (its signing key checked against a pinned
#     fingerprint), Xvfb, and Chrome's policy, and adds messages-server.service.d/google.conf;
#   - installs messages-server.service and the nightly messages-server-update.timer, and starts the server. systemd makes
#     /var/lib/messages-server/data, .../keys and .../backups, private to the account, the way the container's volumes
#     are (README.md, "Where things live").
# An update stops the running server first (it finishes pending sends; up to 60 s), replaces /opt/messages-server and
# starts it again, with the same data, keys, sign-ins and paired devices. uninstall.sh takes it away again.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
app=/opt/messages-server
settings=/etc/messages-server
units=/etc/systemd/system
dropin=$units/messages-server.service.d/google.conf
policy=/etc/opt/chrome/policies/managed/messages-server.json
# Google's Linux signing key, as the Google image pins it (Dockerfile.google, CHROME_SIGNER_FPRS).
chrome_signer=EB4C1BFD4F042F6DDDCCEC917721F63BD38B4796

fail() { echo "install.sh: $*" >&2; exit 1; }

google=keep
host_sign_in=no
for argument in "$@"; do
    case "$argument" in
        --google) google=on ;;
        --no-google) google=off ;;
        --allow-host-sign-in) host_sign_in=yes ;;
        *) fail "it reads --google, --no-google and --allow-host-sign-in only. Nothing was changed." ;;
    esac
done
google_choice=$google
# An update keeps what the last install chose.
if [ "$google" = keep ]; then
    if [ -e "$dropin" ]; then google=on; else google=off; fi
fi

[ "$(id -u)" -eq 0 ] || fail "run it as root (sudo ./install.sh)."
command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ] || fail "this host does not run systemd; use the container instead (README.md)."
[ "$(uname -m)" = x86_64 ] || fail "this package is for x86-64 Linux; this host is $(uname -m)."
[ -x "$here/messages-server" ] || fail "run it from the extracted package: $here has no messages-server."
for helper in messenger-helper instagram-helper messages-tailnet; do
    [ -x "$here/$helper" ] || fail "$helper is missing from $here; extract the package again."
done
if [ "$google" = on ]; then
    for helper in gmessages-helper gvoice-helper; do
        [ -x "$here/$helper" ] || fail "$helper is missing from $here: this package has no Google. Use a newer package, or install without --google."
    done
    [ -s "$here/chrome-policy.json" ] || fail "chrome-policy.json is missing from $here; extract the package again."
    command -v apt-get >/dev/null 2>&1 || fail "--google installs Google Chrome from Google's apt repository, so it needs Debian or Ubuntu."
fi

# A bare host has no private network namespace. Do not silently claim it does just to offer sign-in.
# Read only these non-secret settings, never source the environment file (which can hold passwords).
network_choice() {
    sed -n "s/^$1=//p" "$settings/environment" 2>/dev/null | tail -n 1 | tr -d '\"'
}
save_host_sign_in=no
isolated=$(network_choice MESSAGES_NETWORK_ISOLATED)
if [ -z "$isolated" ] && grep -qx 'Environment=MESSAGES_NETWORK_ISOLATED=1' "$units/messages-server.service.d/proxmox.conf" 2>/dev/null; then
    isolated=1
fi
if [ "$google" = on ] && { [ "$google_choice" = on ] || [ "$host_sign_in" = yes ]; } &&
    [ "$(network_choice MESSAGES_REMOTE_SIGNIN_HOST_NETWORK)" != 1 ] &&
    [ "$isolated" != 1 ]; then
    if [ "$host_sign_in" != yes ] && [ -t 0 ]; then
        echo "Sign-in on this server's page uses Chrome on this computer."
        echo "This host shares its network with other local programs. Enable sign-in only if you trust the other users and programs on it."
        printf 'Allow sign-in on this host? [y/N] '
        read -r answer || answer=
        case "$answer" in y|Y|yes|YES) host_sign_in=yes ;; esac
    fi
    [ "$host_sign_in" = yes ] || fail "sign-in needs a private container or your approval for this host. On a trusted host, run again with --google --allow-host-sign-in. Nothing was changed."
    save_host_sign_in=yes
fi

# .NET is in the package, but it uses the host's ICU, OpenSSL 3, CA certificates and time zones, as the images install
# them (Dockerfile.google). A minimal Debian, Fedora or LXC template can lack ICU, and the server then stops at its
# first line. What is missing is installed with the host's own package manager, and named.
missing=""
ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so' || missing="$missing icu"
ldconfig -p 2>/dev/null | grep -q 'libssl\.so\.3' || missing="$missing openssl"
# A bundle (Debian, Ubuntu) or OpenSSL's hashed links (Fedora 44 has only these).
[ -s /etc/ssl/certs/ca-certificates.crt ] || [ -s /etc/pki/tls/certs/ca-bundle.crt ] || ls /etc/ssl/certs/*.0 >/dev/null 2>&1 || missing="$missing certificates"
[ -d /usr/share/zoneinfo ] || missing="$missing tzdata"
apt_updated=no
apt_update() { [ "$apt_updated" = yes ] || { apt-get update -qq; apt_updated=yes; }; }
if [ -n "$missing" ]; then
    echo "Installing what the server needs from the system:$missing"
    if command -v apt-get >/dev/null 2>&1; then
        apt_update
        packages="ca-certificates tzdata"
        # The ICU and OpenSSL packages carry their version in their names (libicu72 to 76, libssl3 or libssl3t64).
        case "$missing" in *icu*) icu=$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+(t64)?$' | sort -V | tail -n 1)
            [ -n "$icu" ] || fail "no ICU package (libicuNN) is offered here; install ICU, then run install.sh again."
            packages="$packages $icu" ;; esac
        case "$missing" in *openssl*) packages="$packages libssl3" ;; esac
        # shellcheck disable=SC2086 # one word per package
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends $packages
    elif command -v dnf >/dev/null 2>&1; then
        dnf install -y -q libicu openssl-libs ca-certificates tzdata
    else
        fail "install ICU, OpenSSL 3, CA certificates and time zone data with this system's package manager, then run install.sh again."
    fi
fi

# Google: official Google Chrome (never Chromium, never a flag that hides what it is: PROVIDERS.md), the virtual display
# it draws on, and the policy for Google, Facebook and Instagram sign-in, as the image has it (Dockerfile.google).
if [ "$google" = on ]; then
    apt_update
    echo "Installing Xvfb and what Chrome needs..."
    # xvfb: Chrome's virtual display. libxcb-xtest0: real X input, through which the server types and clicks there.
    # util-linux: unshare, with which the server checks Chrome's sandbox can start. curl and gnupg: Google's key.
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends xvfb libxcb-xtest0 util-linux curl gnupg fonts-liberation
    if ! dpkg -s google-chrome-stable >/dev/null 2>&1; then
        echo "Installing Google Chrome from Google's repository..."
        keys=$(mktemp -d)
        curl -fsSL --proto '=https' --tlsv1.2 https://dl.google.com/linux/linux_signing_key.pub -o "$keys/signing.pub"
        gpg --batch --quiet --no-default-keyring --keyring "$keys/all.gpg" --import "$keys/signing.pub" 2>/dev/null
        gpg --batch --quiet --no-default-keyring --keyring "$keys/all.gpg" --export "$chrome_signer" > "$keys/chrome.gpg" 2>/dev/null || true
        if [ ! -s "$keys/chrome.gpg" ]; then
            rm -rf "$keys"
            fail "Google's signing key file does not hold the pinned key ($chrome_signer); Chrome was not installed."
        fi
        install -m 0644 "$keys/chrome.gpg" /usr/share/keyrings/google-chrome.gpg
        rm -rf "$keys"
        echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] https://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list
        # Chrome's package otherwise adds a repository line of its own, without the pinned keyring.
        mkdir -p /etc/default
        echo 'repo_add_once="false"' > /etc/default/google-chrome
        apt-get update -qq
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends google-chrome-stable
    fi
    echo "Google Chrome: $(google-chrome-stable --version 2>/dev/null || echo unknown)"
    mkdir -p "$(dirname "$policy")"
    install -m 0644 "$here/chrome-policy.json" "$policy"
    # Chrome's sandbox is made of user namespaces. The server checks again before every start of Chrome and says why
    # when it cannot (sandbox_unavailable); this only says it early.
    userns=$(cat /proc/sys/user/max_user_namespaces 2>/dev/null || echo 0)
    case "$userns" in ''|*[!0-9]*) userns=0 ;; esac
    if [ "$userns" -le 0 ] || [ "$(cat /proc/sys/kernel/unprivileged_userns_clone 2>/dev/null || echo 1)" = 0 ] \
        || [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null || echo 0)" = 1 ]; then
        echo "WARNING: this kernel does not let an ordinary account make user namespaces, which Chrome's sandbox is built from." >&2
        echo "         The server will not start Chrome without its sandbox, so Google stays off until they are allowed" >&2
        echo "         (README.md, Google). Messenger and Instagram are not affected." >&2
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
( cd "$here" && tar -cf - --exclude=./install.sh --exclude=./uninstall.sh --exclude='./*.service' --exclude='./*.timer' --exclude=./google.conf . ) \
    | ( cd "$app.new" && tar -xf - --no-same-owner )
chown -R root:root "$app.new"
chmod -R u=rwX,go=rX "$app.new"
chmod 0755 "$app.new/messages-server" "$app.new/messenger-helper" "$app.new/instagram-helper" "$app.new/messages-tailnet" "$app.new/update.sh"
for helper in gmessages-helper gvoice-helper mlink; do
    if [ -e "$app.new/$helper" ]; then chmod 0755 "$app.new/$helper"; fi
done
# What the nightly update last installed, kept across an install by hand.
if [ -e "$app/.package-sha256" ]; then cp "$app/.package-sha256" "$app.new/.package-sha256"; fi
rm -rf "$app.old"
[ -d "$app" ] && mv "$app" "$app.old"
mv "$app.new" "$app"
rm -rf "$app.old"

mkdir -p "$settings"
chmod 0755 "$settings"
if [ ! -e "$settings/environment" ]; then
    (
    umask 077
    cat > "$settings/environment" <<'EOF'
# Message Link Server's settings, read by messages-server.service when it starts (systemctl restart messages-server after a
# change). Root only: the page's password and a Tailscale key can be in it. Remove the # to use a line.

# The names and addresses devices reach this server by, written into its certificate on the first start (what a device
# trusts is the fingerprint, not these names).
#MESSAGES_HOSTNAMES=server.lan,192.168.1.20

# The one HTTPS port (default 8787).
#MESSAGES_PORT=8787

# The password of the server's own page (https://<this server>:8787/ in a browser): 12 characters or more, used nowhere
# else. Empty asks for a setup code on the first visit.
#MESSAGES_ADMIN_PASSWORD=

# Time zone for log timestamps.
#TZ=Etc/UTC
EOF
    )
    chmod 0600 "$settings/environment"
    echo "Made $settings/environment for the settings (all off; see the comments in it)."
fi
if [ "$save_host_sign_in" = yes ]; then
    printf '\n# Approved during install: sign-in on this trusted host shares its network with local programs.\nMESSAGES_REMOTE_SIGNIN_HOST_NETWORK=1\n' >> "$settings/environment"
fi
# The tailnet's settings, added once to a settings file from before they existed.
if ! grep -q 'MESSAGES_TAILNET' "$settings/environment"; then
    cat >> "$settings/environment" <<'EOF'

# 1 puts the server on your Tailscale network as a machine of its own, so Message Link on a phone reaches it from anywhere
# (README.md, "Your tailnet"). The server's page then walks you through signing it in.
#MESSAGES_TAILNET=1
# Its name there: lowercase letters, digits and dashes.
#MESSAGES_TAILNET_NAME=messagelink-server
# For a machine with no browser at hand: a Tailscale auth key (tskey-auth-...) signs the server in by itself the first
# time. It is handed to the tailnet door alone. A tagged key makes a tagged machine, and the server then cannot tell
# its owner: it lets in every device your access rules let through, not only your own.
#MESSAGES_TAILNET_AUTHKEY=
EOF
fi
if [ ! -e "$settings/update" ]; then
    (
    umask 077
    cat > "$settings/update" <<'EOF'
# The nightly update (messages-server-update.timer, about 03:00). Off until the line below names the newest release's
# package: an https:// address of messages-server-linux-x64.tar.gz with its .sha256 at the same address plus .sha256.
# Each night the .sha256 is read; a new package is downloaded, checked and installed with its own install.sh.
# With Google on, the newest stable Google Chrome is taken each night either way.
#MESSAGES_RELEASE_URL=
EOF
    )
    chmod 0600 "$settings/update"
fi

install -m 0644 "$here/messages-server.service" "$units/messages-server.service"
install -m 0644 "$here/messages-server-update.service" "$units/messages-server-update.service"
install -m 0644 "$here/messages-server-update.timer" "$units/messages-server-update.timer"
if [ "$google" = on ]; then
    mkdir -p "$(dirname "$dropin")"
    install -m 0644 "$here/google.conf" "$dropin"
else
    rm -f "$dropin"
    rmdir "$(dirname "$dropin")" 2>/dev/null || true
fi
cp "$here/uninstall.sh" "$app/uninstall.sh"
chmod 0755 "$app/uninstall.sh"
# mlink, Message Link in a terminal, on every user's PATH. A file there that is not this link is someone else's and stays.
if [ -x "$app/mlink" ] && { [ -L /usr/local/bin/mlink ] || [ ! -e /usr/local/bin/mlink ]; }; then
    mkdir -p /usr/local/bin
    ln -sfn "$app/mlink" /usr/local/bin/mlink
fi
systemctl daemon-reload
systemctl enable --quiet messages-server
systemctl enable --quiet --now messages-server-update.timer
systemctl start messages-server

sleep 3
if systemctl is-active --quiet messages-server; then
    if [ "$running" = yes ]; then echo "Message Link Server updated and running."; else echo "Message Link Server installed and running."; fi
else
    echo "Message Link Server did not stay running. Its last lines:" >&2
    journalctl -u messages-server -n 20 --no-pager >&2 || true
    exit 1
fi
if [ "$google" = on ]; then echo "Google Messages and Google Voice are on: sign them in on the server's page."; fi
echo
echo "The server's page, its setup code, and the Tailscale sign-in when the tailnet is on, are in its log:"
echo "  journalctl -u messages-server --no-pager | grep -Ei 'setup code|pairing code=|tls fingerprint|tailnet login'"
echo "Pairing code and fingerprint (the code only while no device is paired; it works once, for 10 minutes) are there too."
echo "The server's log: /var/lib/messages-server/data/server.log, or journalctl -u messages-server -f"
