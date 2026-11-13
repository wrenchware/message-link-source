#!/usr/bin/env bash
# Message Link Server in a container of its own on Proxmox VE. Run it in the Proxmox host's shell, as root (the node's
# Shell button in the web interface is root):
#
#   bash -c "$(curl -fsSL https://github.com/wrenchware/message-link-source/releases/latest/download/install-proxmox.sh)"
#
# What it does and its options are in usage() below (--help prints it).
#
# Why nesting=1: the server runs as a systemd service with systemd's own isolation (a private /tmp, a read-only system,
# no capabilities), which sets up mount namespaces, and Google Chrome's sandbox is built from user namespaces. Inside an
# unprivileged container both need Proxmox's nesting feature, which lets the container's own processes make namespaces
# inside it; it gives the container nothing on the host (it stays unprivileged, with no device or host mount but the
# backups folder you choose). Proxmox turns nesting on by default for new unprivileged containers made in its web
# interface. The server never runs Chrome without its sandbox: Google refuses browsers that look automated or altered,
# and a Chrome without its sandbox is a weaker one, so if the sandbox cannot start the log says sandbox_unavailable.
# Tailscale needs nothing from the container: the door is userspace (tsnet), with no tun device.
set -euo pipefail

# The newest stable release's package: messages-server-linux-x64.tar.gz with its .sha256 at the same address plus
# .sha256. Promoting a release on GitHub moves this address; the container's nightly update reads the same one.
RELEASE_URL="https://github.com/wrenchware/message-link-source/releases/latest/download/messages-server-linux-x64.tar.gz"
GUIDE_URL="https://wrenchware.github.io/message-link/proxmox/"

hostname_default="messages-server"
template_series="debian-13-standard"
port=8787
backtitle="Message Link Server for Proxmox VE"

# Printed by --help. Kept here rather than read from this file, which has no path when it runs as bash -c "$(curl ...)".
usage() {
    cat <<'EOF'
Message Link Server in a container of its own on Proxmox VE. Run it in the Proxmox host's shell, as root:

  bash -c "$(curl -fsSL https://github.com/wrenchware/message-link-source/releases/latest/download/install-proxmox.sh)"
  bash install-proxmox.sh --tarball /root/messages-server-<version>-linux-x64.tar.gz

It offers Default settings or Advanced settings, then:
  - makes an unprivileged Debian 13 container (nesting on), started at boot;
  - installs Message Link Server in it as a systemd service, with its helpers and its tailnet door;
  - with Google (the default), official Google Chrome from Google's own repository, so Google Messages and
    Google Voice can be signed in on the server's page;
  - optionally mounts a folder of this host into the container for the server's backups;
  - turns on the nightly update (about 03:00): the newest stable release, and the newest stable Chrome;
  - ends by printing the server page's address and the setup code.

Options:
  --tarball <file or https address>   install this package (messages-server-<version>-linux-x64.tar.gz) instead of
                                      the newest stable release. An address needs its .sha256 beside it; a file is
                                      checked against a .sha256 beside it when there is one.
  --ctid <number>                     the container's id (default: the next free one, or the existing server's)
  --yes                               take every default without asking (no page password, no auth key)
  --no-google                         no Chrome: the page can't connect any account, so accounts only arrive by a
                                      move from a PC
  --help                              this text

Running it again finds the container it made and updates the server in it: data, keys, sign-ins and paired devices
stay. In the container, update runs the nightly update now (pct exec <id> -- update).
To remove everything: pct stop <id> && pct destroy <id> (a backups folder on this host stays).
EOF
}

# ---- Progress ----
# A line while a step runs, a check mark when it is done. On a terminal the check mark replaces the line.
if [ -t 1 ]; then
    on_terminal=yes; green=$'\033[32m'; red=$'\033[31m'; dim=$'\033[2m'; bold=$'\033[1m'; plain=$'\033[0m'
else
    on_terminal=no; green=""; red=""; dim=""; bold=""; plain=""
fi
current=""
log=""
log_mark=0
say() { printf '%s\n' "$*"; }
step() { # step <what is being done>
    current="$1"
    if [ -n "$log" ] && [ -f "$log" ]; then log_mark=$(wc -l < "$log"); fi
    if [ "$on_terminal" = yes ]; then printf '  %s-%s %s...' "$dim" "$plain" "$1"; else printf '  - %s...\n' "$1"; fi
}
done_step() { # done_step [what was done; the step's own words when left out]
    local text="${1:-$current}"
    if [ "$on_terminal" = yes ]; then printf '\r\033[K'; fi
    printf '  %s\xe2\x9c\x93%s %s\n' "$green" "$plain" "$text"
    current=""
}
fail() {
    if [ -n "$current" ]; then
        if [ "$on_terminal" = yes ]; then printf '\r\033[K'; fi
        printf '  %s\xe2\x9c\x97%s %s\n' "$red" "$plain" "$current"
        # What the quiet commands of this step printed.
        if [ -n "$log" ] && [ -s "$log" ] && [ "$(wc -l < "$log")" -gt "$log_mark" ]; then
            printf '%s\n' "${dim}Its output:${plain}" >&2
            tail -n +"$((log_mark + 1))" "$log" | tail -n 40 | sed 's/^/    /' >&2
        fi
        current=""
    fi
    printf 'install-proxmox.sh: %s\n' "$*" >&2
    exit 1
}
quiet() { # quiet <command...>: its output goes to the log, shown only if the step fails
    "$@" >>"$log" 2>&1
}

tarball=""; ctid=""; assume=no; google=yes
while [ $# -gt 0 ]; do
    case "$1" in
        --tarball) [ $# -ge 2 ] || fail "--tarball needs a file or an https address."; tarball="$2"; shift 2 ;;
        --ctid) [ $# -ge 2 ] || fail "--ctid needs a number."; ctid="$2"; shift 2 ;;
        --yes) assume=yes; shift ;;
        --no-google) google=no; shift ;;
        --help|-h) usage; exit 0 ;;
        *) fail "unknown option $1 (the options are --tarball, --ctid, --yes, --no-google and --help)." ;;
    esac
done

# ---- Where this runs ----
[ "$(id -u)" -eq 0 ] || fail "run it as root in the Proxmox host's shell."
for tool in pct pveam pvesm pvesh pveversion; do
    command -v "$tool" >/dev/null 2>&1 || fail "this is not a Proxmox VE host ($tool is missing). Run it in the Proxmox node's shell, not inside a container or VM."
done
[ -d /etc/pve ] || fail "/etc/pve is missing: this is not a running Proxmox VE node."
[ "$(uname -m)" = x86_64 ] || fail "the package is for x86-64; this host is $(uname -m)."
# Questions go to the terminal itself, so they work when the script came in through bash -c "$(curl ...)".
if [ "$assume" = yes ]; then
    ui=none
elif ! (: </dev/tty) 2>/dev/null; then
    fail "it asks questions, but there is no terminal to ask in. Run it in a shell, or add --yes to take every default."
elif command -v whiptail >/dev/null 2>&1; then
    ui=whiptail
else
    ui=plain
fi

work=$(mktemp -d /tmp/messages-proxmox.XXXXXX)
chmod 700 "$work"
trap 'rm -rf "$work"' EXIT
log="$work/log"
: > "$log"

# ---- Questions ----
# Each sets REPLY. Whiptail draws on its stdout and answers on stderr, so the two are swapped (3>&1 1>&2 2>&3) to take
# the answer; Cancel or Esc anywhere ends the run before anything is made.
cancelled() { say ""; say "Nothing was changed."; exit 0; }
whip() { whiptail --backtitle "$backtitle" "$@" 3>&1 1>&2 2>&3 </dev/tty; }
input() { # input <title> <question> <default> [explanation]
    case "$ui" in
        none) REPLY="$3" ;;
        whiptail) REPLY=$(whip --title "$1" --inputbox "${4:+$4

}$2" 14 76 "$3") || cancelled ;;
        plain)
            if [ -n "${4:-}" ]; then printf '%s\n' "$4" | fold -s -w 110 | sed 's/^/  /' >/dev/tty; fi
            read -r -p "$2 [$3]: " REPLY </dev/tty || cancelled
            REPLY="${REPLY:-$3}" ;;
    esac
}
secret() { # secret <title> <question> [explanation]: never shown, never on a command line
    case "$ui" in
        none) REPLY="" ;;
        whiptail) REPLY=$(whip --title "$1" --passwordbox "${3:+$3

}$2" 14 76) || cancelled ;;
        plain)
            if [ -n "${3:-}" ]; then printf '%s\n' "$3" | fold -s -w 110 | sed 's/^/  /' >/dev/tty; fi
            read -r -s -p "$2: " REPLY </dev/tty || cancelled
            printf '\n' >/dev/tty ;;
    esac
}
yes_no() { # yes_no <title> <question> <y|n> [yes button] [no button]: yes or no; Esc cancels
    local status=0 answer
    case "$ui" in
        none) [ "$3" = y ] ;;
        whiptail)
            local options=(--title "$1")
            [ "$3" = n ] && options+=(--defaultno)
            [ -n "${4:-}" ] && options+=(--yes-button "$4")
            [ -n "${5:-}" ] && options+=(--no-button "$5")
            whip "${options[@]}" --yesno "$2" 20 76 || status=$?
            case "$status" in 0) return 0 ;; 1) return 1 ;; *) cancelled ;; esac ;;
        plain)
            read -r -p "$2 (y/n) [$3]: " answer </dev/tty || cancelled
            case "${answer:-$3}" in [Yy]*) return 0 ;; *) return 1 ;; esac ;;
    esac
}
choose() { # choose <title> <question> <default> <choice>...: one of the choices, from a menu
    local title="$1" question="$2" default="$3" items=() choice
    shift 3
    if [ "$ui" = whiptail ]; then
        for choice in "$@"; do items+=("$choice" ""); done
        REPLY=$(whip --title "$title" --default-item "$default" --menu "$question" 18 76 8 "${items[@]}") || cancelled
    else
        input "$title" "$question ($*)" "$default"
    fi
}
invalid() { # invalid <message>: with whiptail, says so and the question is asked again; otherwise the run ends
    if [ "$ui" = whiptail ]; then
        whip --title "Check this value" --msgbox "$1" 10 76 || true
    else
        fail "${1,}"
    fi
}

# ---- The package ----
package="$work/messages-server.tar.gz"
if [ -z "$tarball" ]; then tarball="$RELEASE_URL"; fi
# A package given with --tarball (a test build, usually newer than stable) is kept: the nightly update would put the
# stable release over it, so it leaves the server alone until a run without --tarball turns it back on.
if [ "$tarball" = "$RELEASE_URL" ]; then follow=yes; else follow=no; fi
case "$tarball" in
    https://*)
        step "Downloading Message Link Server"
        curl -fsSL --proto '=https' --tlsv1.2 --retry 3 -o "$package" "$tarball" 2>>"$log" || fail "could not download $tarball."
        curl -fsSL --proto '=https' --tlsv1.2 --retry 3 -o "$work/package.sha256" "$tarball.sha256" 2>>"$log" || fail "could not download $tarball.sha256, which the package is checked against."
        ;;
    http://*|ftp://*) fail "the package must come over https." ;;
    *)
        step "Reading $tarball"
        [ -f "$tarball" ] || fail "no file $tarball."
        cp "$tarball" "$package"
        if [ -f "$tarball.sha256" ]; then cp "$tarball.sha256" "$work/package.sha256"; fi
        ;;
esac
sha=$(sha256sum "$package" | cut -d ' ' -f 1)
checked=""
if [ -f "$work/package.sha256" ]; then
    want=$(cut -d ' ' -f 1 "$work/package.sha256" | head -n 1 | tr 'A-F' 'a-f')
    [ "$want" = "$sha" ] || fail "the package does not match its .sha256; nothing was changed."
    checked=", checked against its .sha256"
fi
top=$(tar -tzf "$package" 2>/dev/null | head -n 1 | cut -d / -f 1) || true
[[ "$top" =~ ^messages-server-[0-9A-Za-z.+-]+-linux-x64$ ]] || fail "$tarball is not a Message Link Server package (messages-server-<version>-linux-x64.tar.gz)."
for member in install.sh messages-server messages-tailnet; do
    tar -tzf "$package" "$top/$member" >/dev/null 2>&1 || fail "$tarball has no $member: use a package from 10.11 or later."
done
if [ "$google" = yes ] && ! tar -tzf "$package" "$top/gmessages-helper" >/dev/null 2>&1; then
    fail "$tarball has no Google helpers; use a newer package, or add --no-google."
fi
version=${top#messages-server-}; version=${version%-linux-x64}
done_step "Message Link Server $version$checked"

# ---- An existing container: update it ----
existing=""
if [ -z "$ctid" ]; then
    while read -r id _; do
        [ -n "$id" ] || continue
        if pct config "$id" 2>/dev/null | grep -Eq '^tags:.*messages-server'; then existing="$id"; break; fi
    done < <(pct list 2>/dev/null | tail -n +2)
    ctid="${existing:-$(pvesh get /cluster/nextid)}"
fi
case "$ctid" in ''|*[!0-9]*) fail "the container id must be a number." ;; esac

in_ct() { pct exec "$ctid" -- "$@"; }
wait_for_network() {
    local tries
    step "Waiting for the container's network"
    for tries in $(seq 1 60); do
        if in_ct sh -c 'getent hosts deb.debian.org >/dev/null 2>&1'; then done_step "The container's network is up"; return 0; fi
        sleep 2
    done
    fail "container $ctid has no network after 2 minutes. Check its bridge and address (pct config $ctid), then run this again."
}
container_address() {
    in_ct sh -c "hostname -I 2>/dev/null" | tr ' ' '\n' | grep -E '^[0-9]+(\.[0-9]+){3}$' | head -n 1 || true
}
# The nightly update's settings: the stable release's address, or none for a --tarball package. Read by update.sh.
write_update_settings() {
    {
        printf '# The nightly update (messages-server-update.timer, about 03:00): the newest stable release of Message Link\n'
        printf '# Server from the address below, checked against its .sha256 before anything is stopped. Empty: the server\n'
        printf '# itself is not updated (install-proxmox.sh leaves it empty after --tarball, so a test package stays). With\n'
        printf '# Google on, the newest stable Google Chrome is taken each night either way.\n'
        printf 'MESSAGES_RELEASE_URL=%s\n' "$([ "$follow" = yes ] && printf '%s' "$RELEASE_URL")"
    } > "$work/update"
}
updates_line() { # what the nightly update does here, for the closing lines
    if [ "$follow" = yes ]; then
        printf 'each night the server takes the newest stable release%s; to update now: pct exec %s -- update' \
            "$([ "$google" = yes ] && printf ' and Chrome')" "$ctid"
    else
        printf 'off for the server, which keeps the package given with --tarball%s. Run this script again without --tarball to follow stable releases.' \
            "$([ "$google" = yes ] && printf ' (Chrome still updates each night)')"
    fi
}
# update in the container: the nightly update now, then what it did.
write_update_command() {
    cat > "$work/update-command" <<'EOF'
#!/bin/sh
# Runs Message Link Server's nightly update now (install-proxmox.sh put this here), then shows what it did.
set -eu
[ "$(id -u)" -eq 0 ] || { echo "update: run it as root." >&2; exit 1; }
echo "Checking for a newer Message Link Server, and a newer Google Chrome when Google is on..."
since=$(date '+%Y-%m-%d %H:%M:%S')
status=0
systemctl start messages-server-update.service || status=$?
run=$(systemctl show -p InvocationID --value messages-server-update.service 2>/dev/null || true)
if [ -n "$run" ]; then
    journalctl --no-pager -o cat _SYSTEMD_INVOCATION_ID="$run"
else
    journalctl --no-pager -o cat -u messages-server-update.service --since "$since"
fi
exit "$status"
EOF
}
install_update_command() {
    write_update_command
    pct push "$ctid" "$work/update-command" /usr/local/bin/update --perms 0755
}
# The container's notes on its Summary page, which Proxmox shows as Markdown.
write_notes() { # write_notes <address or empty>
    local where="$1" page notes
    # Not a ${1:-...} default: an apostrophe inside one is a bad substitution in bash.
    [ -n "$where" ] || where="<the container's address>"
    page="https://$where:$port/"
    notes="## Message Link Server

The server's page: $([ -n "$1" ] && printf '[%s](%s)' "$page" "$page" || printf '%s' "$page")

Guide: [Message Link Server on Proxmox VE]($GUIDE_URL)

Update now, in this host's shell: \`pct exec $ctid -- update\`

The server's log: \`pct exec $ctid -- journalctl -u messages-server -f\`"
    quiet pct set "$ctid" --description "$notes" || fail "could not write container $ctid's notes."
}
# Installs the package in the container with its own install.sh. The last command there leaves a marker this run named,
# and the marker, not pct exec's exit code, says the install finished: on Proxmox VE 9.2 pct exec once exited non-zero
# after an install that had finished, with nothing printed (2026-11-12).
install_status=0
install_note=""
install_in_container() { # install_in_container <install.sh's flag, or empty>
    local marker="/root/.messages-server-installed-$$-$RANDOM"
    install_status=0
    pct push "$ctid" "$package" /root/messages-server.tar.gz
    quiet in_ct bash -c "set -e; rm -rf /root/messages-server-install; mkdir -p /root/messages-server-install
        tar -xzf /root/messages-server.tar.gz -C /root/messages-server-install --no-same-owner
        /root/messages-server-install/$top/install.sh $1
        echo $sha > /opt/messages-server/.package-sha256
        rm -rf /root/messages-server-install /root/messages-server.tar.gz
        touch $marker" || install_status=$?
    if in_ct test -e "$marker"; then
        in_ct rm -f "$marker" || true
        if [ "$install_status" -ne 0 ]; then
            printf 'pct exec exited %s after the install had finished.\n' "$install_status" >>"$log"
            install_note="pct exec reported exit $install_status after the install had finished; the install is complete."
        fi
        return 0
    fi
    if [ "$install_status" -eq 0 ]; then install_status=1; fi
    return 1
}
# The container's clock now, for read_server_log; empty when it can't be read.
container_time() {
    local now
    now=$(in_ct date '+%Y-%m-%d %H:%M:%S' 2>/dev/null || true)
    if [[ "$now" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}\ [0-9]{2}:[0-9]{2}:[0-9]{2}$ ]]; then printf '%s' "$now"; fi
}
# Waits up to a minute for the server to listen, then reads what the closing lines show. With a time, only the log
# since then counts, so an update's earlier starts don't.
read_server_log() { # read_server_log <container time, or empty>
    local window=""
    if [ -n "$1" ]; then window="--since '$1'"; fi
    listening=no
    for _ in $(seq 1 60); do
        if in_ct sh -c "journalctl -u messages-server --no-pager -o cat $window 2>/dev/null | grep -q 'server listening'"; then listening=yes; break; fi
        sleep 1
    done
    server_log=$(in_ct sh -c "journalctl -u messages-server --no-pager -o cat $window 2>/dev/null" || true)
    setup=$(printf '%s\n' "$server_log" | sed -n 's/.*server setup code=\([^ ]*\).*/\1/p' | tail -n 1)
    pairing=$(printf '%s\n' "$server_log" | sed -n 's/.*server pairing code=\([^ ]*\).*/\1/p' | tail -n 1)
    fingerprint=$(printf '%s\n' "$server_log" | sed -n 's/.*server tls fingerprint sha256=\([0-9a-f]*\).*/\1/p' | tail -n 1)
    login=$(printf '%s\n' "$server_log" | sed -n 's/.*tailnet login url=\([^ ]*\).*/\1/p' | tail -n 1)
}

update=no
if pct status "$ctid" >/dev/null 2>&1; then
    pct config "$ctid" | grep -Eq '^tags:.*messages-server' \
        || fail "container $ctid exists and is not one this script made. Choose another id (--ctid), or remove it first."
    if ! yes_no "Update Message Link Server" "Container $ctid holds the Message Link Server this script made earlier.

Update it to $version? Its data, keys, sign-ins and paired devices stay." y "Update" "Cancel"; then
        cancelled
    fi
    update=yes
fi

if [ "$update" = yes ]; then
    say ""
    if ! pct status "$ctid" | grep -q running; then
        step "Starting container $ctid"
        quiet pct start "$ctid" || fail "could not start container $ctid."
        done_step "Container $ctid is running"
    fi
    wait_for_network
    if ! in_ct test -x /opt/messages-server/messages-server; then
        fail "container $ctid was made by an earlier run that did not finish. Remove it (pct stop $ctid; pct destroy $ctid) and run this again."
    fi
    flag=""
    # The container keeps Google's setting (install.sh reads it) unless this run said --no-google.
    if [ "$google" = no ]; then flag="--no-google"; fi
    step "Updating Message Link Server to $version"
    since=$(container_time)
    install_in_container "$flag" \
        || fail "the update did not install (pct exec exited $install_status); the server keeps the version it had."
    done_step "Message Link Server is $version"
    if [ -n "$install_note" ]; then say "    ${dim}$install_note${plain}"; fi

    step "Setting the nightly update"
    # Without --tarball, an empty address (a container made before the release existed, or after a --tarball run) becomes
    # the stable release's. With --tarball, the stable address is emptied so the test package stays. An address of the
    # owner's own is kept either way.
    rm -f "$work/update.current"
    pct pull "$ctid" /etc/messages-server/update "$work/update.current" >>"$log" 2>&1 || true
    release=$(sed -n 's/^MESSAGES_RELEASE_URL=//p' "$work/update.current" 2>/dev/null | tail -n 1 | tr -d '"'"'"' \r' || true)
    rm -f "$work/update.current"
    if { [ "$follow" = yes ] && [ -z "$release" ]; } || { [ "$follow" = no ] && [ "$release" = "$RELEASE_URL" ]; }; then
        write_update_settings
        pct push "$ctid" "$work/update" /etc/messages-server/update --perms 0600
        release=""
        if [ "$follow" = yes ]; then release="$RELEASE_URL"; fi
    fi
    install_update_command
    if [ -n "$release" ]; then done_step "The nightly update is on (update runs it now)"
    else done_step "The nightly update leaves the server as it is"; fi

    # Notes this script wrote, or none, are replaced; the owner's own notes stay.
    ip=$(container_address)
    description=$(pct config "$ctid" 2>/dev/null | sed -n 's/^description: //p' || true)
    if [ -z "$description" ] || printf '%s' "$description" | grep -q 'Message Link Server'; then
        step "Writing the container's notes"
        write_notes "$ip"
        done_step "The container's notes name its page, the guide and the update command"
    fi
    step "Starting the server"
    read_server_log "$since"
    if [ "$listening" = yes ]; then done_step "The server is running"
    else done_step "The server is still starting (its log: pct exec $ctid -- journalctl -u messages-server -f)"; fi
    say ""
    say "${bold}Message Link Server in container $ctid is now $version.${plain}"
    where="$ip"
    [ -n "$where" ] || where="<the container's address>"
    say "  Its page:      https://$where:$port/"
    # Shown while the page has no password yet, as after a first run that stopped early.
    if [ -n "$setup" ]; then say "  Setup code:    $setup"; fi
    if [ -n "$pairing" ]; then say "  Pairing code:  $pairing (works once, for 10 minutes; a PC can also ask to join from the page)"; fi
    if [ "$follow" = no ] && [ -z "$release" ]; then say "  Updates:       $(updates_line)"
    else say "  Update now:    pct exec $ctid -- update"; fi
    say "  Its log:       pct exec $ctid -- journalctl -u messages-server -f"
    exit 0
fi

# ---- A new container: the settings ----
mapfile -t template_stores < <(pvesm status -content vztmpl 2>/dev/null | tail -n +2 | awk '$3 == "active" {print $1}')
mapfile -t disk_stores < <(pvesm status -content rootdir 2>/dev/null | tail -n +2 | awk '$3 == "active" {print $1}')
[ "${#template_stores[@]}" -gt 0 ] || fail "no active storage holds container templates (content vztmpl). Enable it on a storage in Datacenter, Storage."
[ "${#disk_stores[@]}" -gt 0 ] || fail "no active storage holds container disks (content rootdir). Enable it on a storage in Datacenter, Storage."
tstore="${template_stores[0]}"; for s in "${template_stores[@]}"; do [ "$s" = local ] && tstore=local; done
dstore="${disk_stores[0]}"; for s in "${disk_stores[@]}"; do [ "$s" = local-lvm ] && dstore=local-lvm; done
mapfile -t bridges < <(ip -o link show type bridge 2>/dev/null | awk -F': ' '{print $2}' | cut -d @ -f 1)
bridge=vmbr0; [ "${#bridges[@]}" -gt 0 ] && ! printf '%s\n' "${bridges[@]}" | grep -qx vmbr0 && bridge="${bridges[0]}"

# The defaults.
host="$hostname_default"
disk=8
if [ "$google" = yes ]; then memory=4096; else memory=2048; fi
cores=2
address=dhcp
gateway=""
backups_host=""
tailnet=yes
authkey=""
password=""

# Chrome's sandbox needs the kernel to let ordinary accounts make user namespaces, in the container as on the host.
userns_warning=""
if [ "$google" = yes ]; then
    userns=$(cat /proc/sys/user/max_user_namespaces 2>/dev/null || echo 0)
    if [ "${userns:-0}" -le 0 ] 2>/dev/null || [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null || echo 0)" = 1 ]; then
        userns_warning="This host's kernel does not let ordinary accounts make user namespaces, which Chrome's sandbox is built from. Google stays off (the server's log says sandbox_unavailable); Messenger and Instagram are not affected."
    fi
fi

settings=default
if [ "$ui" = whiptail ]; then
    settings=$(whip --title "Message Link Server $version" --default-item Default \
        --menu "Make a new container for Message Link Server on this host." 14 76 2 \
        Default "Recommended settings, shown once before anything is made" \
        Advanced "Choose each setting") || cancelled
elif [ "$ui" = plain ]; then
    settings=advanced
    say ""
    say "A new container for Message Link Server. Press Enter to take the value in brackets."
fi

if [ "$settings" != Default ] && [ "$settings" != default ]; then
    while :; do
        input "Container id" "Container id" "$ctid"; ctid="$REPLY"
        case "$ctid" in ''|*[!0-9]*) invalid "The container id must be a number."; continue ;; esac
        if pct status "$ctid" >/dev/null 2>&1; then invalid "Container $ctid exists. Choose another id."; continue; fi
        break
    done
    while :; do
        input "Hostname" "Hostname" "$host"; host="$REPLY"
        [[ "$host" =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] && break
        invalid "The hostname must be lowercase letters, digits and dashes."
    done
    while :; do
        choose "Template storage" "Storage for the Debian template" "$tstore" "${template_stores[@]}"; tstore="$REPLY"
        printf '%s\n' "${template_stores[@]}" | grep -qx -- "$tstore" && break
        invalid "$tstore is not one of: ${template_stores[*]}."
    done
    while :; do
        choose "Disk storage" "Storage for the container's disk" "$dstore" "${disk_stores[@]}"; dstore="$REPLY"
        printf '%s\n' "${disk_stores[@]}" | grep -qx -- "$dstore" && break
        invalid "$dstore is not one of: ${disk_stores[*]}."
    done
    while :; do
        input "Disk size" "Disk size in GB" "$disk"; disk="$REPLY"
        case "$disk" in ''|*[!0-9]*) invalid "Sizes are whole numbers."; continue ;; esac
        [ "$disk" -ge 4 ] && break
        invalid "4 GB of disk at least (Chrome and the history need room)."
    done
    while :; do
        input "Memory" "Memory in MB" "$memory"; memory="$REPLY"
        case "$memory" in ''|*[!0-9]*) invalid "Sizes are whole numbers."; continue ;; esac
        [ "$memory" -ge 1024 ] && break
        invalid "1024 MB of memory at least."
    done
    while :; do
        input "Processor cores" "Processor cores" "$cores"; cores="$REPLY"
        case "$cores" in ''|*[!0-9]*) invalid "Sizes are whole numbers."; continue ;; esac
        break
    done
    if [ "${#bridges[@]}" -gt 0 ]; then
        choose "Network bridge" "Network bridge" "$bridge" "${bridges[@]}"; bridge="$REPLY"
    else
        input "Network bridge" "Network bridge (none found)" "$bridge"; bridge="$REPLY"
    fi
    while :; do
        input "Address" "Address: dhcp, or a fixed one such as 192.168.1.50/24" "$address"; address="$REPLY"
        [ "$address" = dhcp ] && break
        [[ "$address" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2}$ ]] && break
        invalid "The address must be dhcp or IPv4 with its prefix, such as 192.168.1.50/24."
    done
    if [ "$address" != dhcp ]; then
        gateway="$(ip -4 route show default 2>/dev/null | awk '{print $3; exit}')"
        while :; do
            input "Gateway" "Gateway" "$gateway"; gateway="$REPLY"
            [[ "$gateway" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] && break
            invalid "The gateway must be an IPv4 address."
        done
    fi
    while :; do
        input "Backups" "A folder on this host for the server's backups (empty: inside the container)" "$backups_host"
        backups_host="$REPLY"
        [ -n "$backups_host" ] || break
        case "$backups_host" in /*) ;; *) invalid "The backups folder must be a full path, such as /mnt/backups/messages."; continue ;; esac
        case "$backups_host" in
            /|/etc*|/root|/root/*|/var/lib/vz|/var/lib/vz/*|/proc*|/sys*|/dev*|/boot*)
                invalid "Choose a folder of its own for the backups, not $backups_host."; continue ;;
        esac
        break
    done
    if yes_no "Tailscale" "Reach the server from anywhere through Tailscale (recommended for the phone)?" y; then
        tailnet=yes
    else
        tailnet=no
    fi
    if [ "$tailnet" = yes ]; then
        while :; do
            secret "Tailscale auth key" "Tailscale auth key (hidden; Enter for none)" \
                "The server's page signs it in to Tailscale with a link. For a machine you will not open a browser for, paste a Tailscale auth key instead (Tailscale's admin console, Settings, Keys). A tagged key lets in every device your access rules allow, not only your own. Press Enter to use the link."
            authkey="$REPLY"
            if [ -z "$authkey" ] || [[ "$authkey" =~ ^tskey-[A-Za-z0-9_-]{8,500}$ ]]; then break; fi
            invalid "That is not a Tailscale auth key (tskey-...)."
        done
    fi
    while :; do
        secret "Page password" "Page password (hidden; 12 characters or more; Enter to set it on the page)" \
            "The server's page needs a password. Set one now, or press Enter and set it on the page's first visit with the setup code this script prints at the end."
        password="$REPLY"
        [ -n "$password" ] || break
        if [ "${#password}" -lt 12 ] || [ "${#password}" -gt 128 ]; then invalid "The page password must be 12 to 128 characters."; continue; fi
        case "$password" in *'"'*|*'\'*|*'$'*|*'`'*) invalid "Use a page password without \", \\, \$ or \`, or set it on the page instead."; continue ;; esac
        secret "Page password" "The same password again"
        [ "$REPLY" = "$password" ] && break
        invalid "The two passwords differ."
    done
    REPLY=""
fi

net="name=eth0,bridge=$bridge"
if [ "$address" = dhcp ]; then net="$net,ip=dhcp"; else net="$net,ip=$address,gw=$gateway"; fi

summary="Container $ctid, $host: unprivileged Debian 13, nesting on, started at boot
Disk: $disk GB on $dstore (template on $tstore)
Memory: $memory MB, $cores cores
Network: $([ "$address" = dhcp ] && printf 'DHCP' || printf '%s, gateway %s' "$address" "$gateway") on $bridge
Google Messages and Google Voice: $([ "$google" = yes ] && printf 'on, with Google Chrome' || printf 'off')
Tailscale: $([ "$tailnet" = yes ] && { [ -n "$authkey" ] && printf 'on, with the auth key given' || printf 'on, signed in on the server'"'"'s page'; } || printf 'off')
Backups: ${backups_host:-inside the container}
Page password: $([ -n "$password" ] && printf 'the one given' || printf 'set on the page with the setup code')
Nightly update: $([ "$follow" = yes ] && printf 'on, the newest stable release' || printf 'off for the server, which keeps this --tarball package')${userns_warning:+

$userns_warning}"

case "$ui" in
    whiptail)
        yes_no "Make the container" "$summary" y "Make it" "Cancel" || cancelled ;;
    plain)
        say ""
        say "About to make container $ctid:"
        printf '%s\n' "$summary" | fold -s -w 110 | sed 's/^/  /'
        yes_no "" "Go ahead" y || cancelled ;;
    none)
        if [ -n "$userns_warning" ]; then say "WARNING: $userns_warning"; fi ;;
esac
say ""

# ---- The template ----
step "Finding the Debian 13 template"
template=$(pveam list "$tstore" 2>/dev/null | awk '{print $1}' | sed -n "s#^$tstore:vztmpl/\($template_series[^ ]*\)#\1#p" | sort -V | tail -n 1)
if [ -z "$template" ]; then
    quiet pveam update || fail "could not fetch the list of container templates."
    template=$(pveam available --section system 2>/dev/null | awk '{print $2}' | grep "^$template_series" | sort -V | tail -n 1)
    [ -n "$template" ] || fail "Proxmox offers no $template_series template; update Proxmox VE (Debian 13 needs 8.4 or later)."
    done_step "Found $template"
    step "Downloading $template to $tstore"
    quiet pveam download "$tstore" "$template" || fail "could not download $template."
fi
done_step "Template: $template"

# ---- The container ----
options=(--hostname "$host" --unprivileged 1 --features nesting=1 --ostype debian --cores "$cores" --memory "$memory" --swap 512
    --rootfs "$dstore:$disk" --net0 "$net" --onboot 1 --tags messages-server --timezone host
    --description "Message Link Server (install-proxmox.sh), being installed.")
step "Making container $ctid"
if [ -n "$backups_host" ]; then
    mkdir -p "$backups_host"
    # The server's account is 2787 inside the container (install.sh takes that number in a fresh container), which an
    # unprivileged container maps to 100000 + 2787 here (root's range in /etc/subuid). Only that account may read or
    # write the backups. Given before the first start, so systemd finds the folder already the account's.
    base=$(sed -n 's/^root:\([0-9]*\):.*/\1/p' /etc/subuid 2>/dev/null | head -n 1)
    base=${base:-100000}
    chown "$((base + 2787)):$((base + 2787))" "$backups_host" && chmod 700 "$backups_host" \
        || fail "could not give $backups_host to the container's account ($((base + 2787))); a network share may not allow it. Choose a local folder, or none."
    options+=(--mp0 "$backups_host,mp=/var/lib/messages-server/backups,backup=0")
fi
quiet pct create "$ctid" "$tstore:vztmpl/$template" "${options[@]}" || fail "could not make container $ctid."
done_step "Made container $ctid"
step "Starting container $ctid"
quiet pct start "$ctid" || fail "could not start container $ctid."
done_step "Container $ctid is running"
wait_for_network
step "Installing Debian's updates"
quiet in_ct bash -c 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq && apt-get upgrade -y -qq -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold
    apt-get install -y -qq --no-install-recommends ca-certificates curl unattended-upgrades' \
    || fail "Debian's updates did not install."
done_step "Debian is up to date"

ip=$(container_address)

# The settings, written before the first start (install.sh keeps a settings file it finds). Root only, in the container
# and in this script's private folder until it is pushed; the secrets are never on a command line.
{
    printf '# Message Link Server'"'"'s settings (install-proxmox.sh). systemctl restart messages-server after a change.\n'
    printf 'MESSAGES_HOSTNAMES=%s\n' "$host${ip:+,$ip}"
    printf 'MESSAGES_TAILNET=%s\n' "$([ "$tailnet" = yes ] && echo 1 || echo 0)"
    printf 'MESSAGES_TAILNET_NAME=%s\n' "$host"
    if [ -n "$authkey" ]; then printf 'MESSAGES_TAILNET_AUTHKEY=%s\n' "$authkey"; fi
    if [ -n "$password" ]; then printf 'MESSAGES_ADMIN_PASSWORD="%s"\n' "$password"; fi
} > "$work/environment"
chmod 600 "$work/environment"
unset password authkey
write_update_settings
# This container has a network of its own (its own veth on $bridge): the server's word for offering Google's sign-in
# in its own browser (the Unraid script says the same of its bridge network).
printf '[Service]\nEnvironment=MESSAGES_NETWORK_ISOLATED=1\n' > "$work/proxmox.conf"
in_ct mkdir -p /etc/messages-server /etc/systemd/system/messages-server.service.d
in_ct chmod 755 /etc/messages-server
pct push "$ctid" "$work/environment" /etc/messages-server/environment --perms 0600
pct push "$ctid" "$work/update" /etc/messages-server/update --perms 0600
pct push "$ctid" "$work/proxmox.conf" /etc/systemd/system/messages-server.service.d/proxmox.conf --perms 0644
rm -f "$work/environment"

step "Installing Message Link Server $version$([ "$google" = yes ] && printf ' and Google Chrome')"
flag=""; [ "$google" = yes ] && flag="--google"
install_in_container "$flag" \
    || fail "Message Link Server did not install (pct exec exited $install_status). Remove the container (pct stop $ctid; pct destroy $ctid) and run this again."
install_update_command
done_step "Installed Message Link Server $version$([ "$google" = yes ] && printf ' and Google Chrome')"
if [ -n "$install_note" ]; then say "    ${dim}$install_note${plain}"; fi

# ---- What to do next ----
step "Starting the server"
read_server_log ""
if [ "$listening" = yes ]; then done_step "The server is running"
else done_step "The server is still starting (its log: pct exec $ctid -- journalctl -u messages-server -f)"; fi
step "Writing the container's notes"
write_notes "$ip"
done_step "The container's notes name its page, the guide and the update command"

# Named here, not as a ${ip:-...} default: an apostrophe inside one is a bad substitution in bash.
where="$ip"
[ -n "$where" ] || where="<the container's address>"
say ""
say "${bold}Message Link Server $version is running in container $ctid ($host).${plain}"
say "  Its page:      https://$where:$port/   (your browser warns about the certificate once: the server made it itself)"
if [ -n "$setup" ]; then say "  Setup code:    $setup"
else say "  Setup code:    pct exec $ctid -- journalctl -u messages-server --no-pager | grep -i 'setup code'"; fi
[ -n "$fingerprint" ] && say "  Fingerprint:   $fingerprint"
[ -n "$pairing" ] && say "  Pairing code:  $pairing (works once, for 10 minutes; a PC can also ask to join from the page)"
if [ "$tailnet" = yes ]; then
    if [ -n "$login" ]; then say "  Tailscale:     the page walks you through it, or sign in here: $login"
    else say "  Tailscale:     the page walks you through it (pct exec $ctid -- journalctl -u messages-server | grep tailnet)"; fi
fi
say ""
say "The server's log:     pct exec $ctid -- journalctl -u messages-server -f"
say "Its settings:         pct exec $ctid -- nano /etc/messages-server/environment   (then: pct exec $ctid -- systemctl restart messages-server)"
if [ -n "$backups_host" ]; then say "Backups:              $backups_host on this host"; else say "Backups:              /var/lib/messages-server/backups in the container"; fi
say "Updates:              $(updates_line)"
say "The guide:            $GUIDE_URL"
say "To remove it:         pct stop $ctid && pct destroy $ctid$([ -n "$backups_host" ] && printf ' (the backups in %s stay)' "$backups_host")"
