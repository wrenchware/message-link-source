#!/bin/sh
# The nightly update of Message Link Server installed by install.sh, run by messages-server-update.timer (or by hand as root:
# systemctl start messages-server-update, then journalctl -u messages-server-update). Two things, each only when it
# applies, and the server is stopped and started at most once for both:
#
#   - a newer stable release, from the address in /etc/messages-server/update (MESSAGES_RELEASE_URL=https://.../
#     messages-server-linux-x64.tar.gz, with its .sha256 at the same address plus .sha256). install.sh writes the
#     stable release's address there; an empty one downloads nothing. The package is
#     checked against its .sha256 before anything is stopped, and installed by its own install.sh, which keeps the
#     data, keys, sign-ins, paired devices and Google's setting.
#   - a newer stable Google Chrome, when Google is on here (install.sh --google): Chrome is replaced only while the
#     server is stopped, since a running Chrome whose files were replaced cannot start its next renderer.
#
# A failed download or check changes nothing. The server's stop lets pending sends finish (up to 60 s), and its start
# resumes the same accounts, paced as after any restart.
set -eu

app=/opt/messages-server
settings=/etc/messages-server/update
dropin=/etc/systemd/system/messages-server.service.d/google.conf

say() { echo "update: $*"; }
fail() { echo "update: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "run it as root."
[ -x "$app/messages-server" ] || fail "Message Link Server is not installed in $app."

url=""
if [ -r "$settings" ]; then
    url=$(sed -n 's/^MESSAGES_RELEASE_URL=//p' "$settings" | tail -n 1 | tr -d '"'"'"' \r')
fi

work=$(mktemp -d /var/tmp/messages-server-update.XXXXXX)
# Whatever ends this script (a failed Chrome upgrade under set -e, a signal), a server it stopped is started again:
# Restart=on-failure does not cover a deliberate stop, and the next night skips a server that is not running. stopped
# is cleared once the server is started below, or install.sh has taken it over (the file `took` it writes once its own
# recovery is armed), so an operator's later stop is left alone.
stopped=no
took="$work/install-took-over"
finish() {
    if [ "$stopped" = yes ] && [ ! -e "$took" ] && ! systemctl is-active --quiet messages-server; then
        echo "update: starting the server again." >&2
        systemctl start messages-server || echo "update: the server did not start; see journalctl -u messages-server." >&2
    fi
    rm -rf "$work" || true
}
trap finish EXIT
trap 'exit 1' HUP INT TERM

# ---- A newer release, downloaded and checked before anything is stopped. ----
package=""
want=""
if [ -n "$url" ]; then
    case "$url" in
        https://*) ;;
        *) fail "MESSAGES_RELEASE_URL in $settings is not an https:// address; nothing was updated." ;;
    esac
    command -v curl >/dev/null 2>&1 || fail "curl is missing; install it (apt-get install curl) for updates."
    curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --max-time 120 -o "$work/package.sha256" "$url.sha256" \
        || fail "could not fetch $url.sha256; nothing was updated."
    want=$(cut -d ' ' -f 1 "$work/package.sha256" | head -n 1 | tr 'A-F' 'a-f')
    case "$want" in
        *[!0-9a-f]*|'') fail "$url.sha256 does not hold a SHA-256; nothing was updated." ;;
    esac
    [ "${#want}" -eq 64 ] || fail "$url.sha256 does not hold a SHA-256; nothing was updated."
    have=$(cat "$app/.package-sha256" 2>/dev/null || true)
    if [ "$want" = "$have" ]; then
        say "the installed release is the newest."
    else
        curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --max-time 1800 -o "$work/package.tar.gz" "$url" \
            || fail "could not download $url; nothing was updated."
        echo "$want  $work/package.tar.gz" | sha256sum -c --status - || fail "the download does not match its .sha256; nothing was updated."
        mkdir "$work/package"
        tar -xzf "$work/package.tar.gz" -C "$work/package" --no-same-owner
        for folder in "$work/package"/messages-server-*-linux-x64; do
            [ -x "$folder/install.sh" ] && [ -x "$folder/messages-server" ] && package="$folder"
        done
        [ -n "$package" ] || fail "the download is not a Message Link Server package; nothing was updated."
        say "a newer release is ready: $(basename "$package")."
    fi
else
    say "no release address in $settings; the server itself is not updated (see the comments in that file)."
fi

# ---- A newer Google Chrome, when Google is on. ----
chrome=no
if [ -e "$dropin" ] && command -v apt-get >/dev/null 2>&1 && dpkg -s google-chrome-stable >/dev/null 2>&1; then
    apt-get update -qq -o Dir::Etc::sourcelist=sources.list.d/google-chrome.list -o Dir::Etc::sourceparts=- -o APT::Get::List-Cleanup=0 \
        || fail "could not reach Google's Chrome repository; nothing was updated."
    if apt-get -s install --only-upgrade google-chrome-stable 2>/dev/null | grep -q '^Inst google-chrome-stable'; then
        chrome=yes
        say "a newer Google Chrome is ready."
    else
        say "Google Chrome is the newest stable."
    fi
fi

if [ -z "$package" ] && [ "$chrome" = no ]; then
    say "nothing to do."
    exit 0
fi

running=no
if systemctl is-active --quiet messages-server; then
    running=yes
    say "stopping the server (pending sends finish first; up to 60 s)..."
    stopped=yes
    systemctl stop messages-server
fi
chromefailed=no
if [ "$chrome" = yes ]; then
    # The lock wait covers apt-daily or unattended-upgrades holding dpkg at the same hour. A failed upgrade keeps the
    # Chrome that was installed, and the release and the start below still run.
    if DEBIAN_FRONTEND=noninteractive apt-get install -y -qq -o DPkg::Lock::Timeout=300 --only-upgrade google-chrome-stable; then
        say "Google Chrome is now $(google-chrome-stable --version 2>/dev/null || echo unknown)."
    else
        chromefailed=yes
        echo "update: Google Chrome did not upgrade; it keeps the version it had." >&2
    fi
fi
if [ -n "$package" ]; then
    # install.sh starts the server, Google's setting kept (it reads the drop-in). Once it says it has taken over, it owns
    # that start: one that fails, or whose release does not stay running, puts the release that was there back, starts it
    # if this script stopped it, and says above what runs. Until then the start stays this script's: an install.sh that
    # never ran (a staging folder mounted noexec, a broken interpreter line) left nobody to start the server. Run by sh,
    # so neither stops it.
    handover=0
    if [ "$stopped" = yes ]; then handover=1; fi
    if ! MESSAGES_INSTALL_RESTART=$handover MESSAGES_INSTALL_ACK="$took" sh "$package/install.sh"; then
        fail "the new release was not installed."
    fi
    stopped=no
    echo "$want" > "$app/.package-sha256"
    chmod 0644 "$app/.package-sha256"
    say "installed $(basename "$package")."
elif [ "$running" = yes ]; then
    stopped=no
    systemctl start messages-server || fail "the server did not start; see journalctl -u messages-server."
    say "the server is running again."
fi
[ "$chromefailed" = no ] || exit 1
