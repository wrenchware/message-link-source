#!/usr/bin/env bash
# Messages Server in a container of its own on Proxmox VE. Run it in the Proxmox host's shell, as root (the node's
# Shell button in the web interface is root):
#
#   bash -c "$(curl -fsSL <address of this script>)"
#   bash -c "$(curl -fsSL <address of this script>)" _ --tarball /root/messages-server-<version>-linux-x64.tar.gz
#   bash install-proxmox.sh --tarball /root/messages-server-<version>-linux-x64.tar.gz
#
# It asks a few questions, each with a default (Enter takes it), then:
#   - makes an unprivileged Debian 13 container (nesting on; see below), started at boot;
#   - installs the Linux package in it (install.sh): the server, its helpers and its tailnet door, as a systemd service;
#   - with Google (the default), official Google Chrome from Google's own repository, Xvfb and Chrome's policy, so Google
#     Messages and Google Voice can be signed in on the server's page;
#   - optionally mounts a folder of this host into the container for the server's backups;
#   - turns on the nightly update (03:00): a newer release when one is published, and the newest stable Chrome;
#   - ends by printing the server page's address and the setup code.
#
# Options:
#   --tarball <file or https address>   the package to install (messages-server-<version>-linux-x64.tar.gz). Needed until
#                                       a release is published at RELEASE_URL below. An address needs its .sha256 beside it;
#                                       a file is checked against a .sha256 beside it when there is one.
#   --ctid <number>                     the container's id (default: the next free one, or the existing Messages Server's)
#   --yes                               take every default without asking (no page password, no auth key)
#   --no-google                         no Chrome: the page can't connect any account, so accounts only arrive by a move from a PC
#   --help                              this text
#
# Running it again finds the container it made and updates the server in it: data, keys, sign-ins and paired devices
# stay. To remove everything: pct stop <id> && pct destroy <id> (a backups folder on this host stays).
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

# The newest release's package, once one is published: an https:// address of messages-server-linux-x64.tar.gz with its
# .sha256 at the same address plus .sha256 (tools\Build-ServerTarball.ps1 writes both). Empty while the package is
# private: use --tarball, and the nightly update stays off for the server (Chrome still updates). A GitHub release's
# "latest" address has this form: https://github.com/<owner>/<repository>/releases/latest/download/messages-server-linux-x64.tar.gz
RELEASE_URL=""

hostname_default="messages-server"
template_series="debian-13-standard"
port=8787

say() { printf '%s\n' "$*"; }
fail() { printf 'install-proxmox.sh: %s\n' "$*" >&2; exit 1; }

tarball=""; ctid=""; assume=no; google=yes
while [ $# -gt 0 ]; do
    case "$1" in
        --tarball) [ $# -ge 2 ] || fail "--tarball needs a file or an https address."; tarball="$2"; shift 2 ;;
        --ctid) [ $# -ge 2 ] || fail "--ctid needs a number."; ctid="$2"; shift 2 ;;
        --yes) assume=yes; shift ;;
        --no-google) google=no; shift ;;
        --help|-h) sed -n '2,37p' "${BASH_SOURCE[0]:-/dev/null}" 2>/dev/null || true; exit 0 ;;
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
interactive=yes
[ -t 0 ] || interactive=no
if [ "$assume" = no ] && [ "$interactive" = no ]; then
    fail "it asks questions, but there is no terminal to ask in. Run it in a shell, or add --yes to take every default."
fi

work=$(mktemp -d /tmp/messages-proxmox.XXXXXX)
chmod 700 "$work"
trap 'rm -rf "$work"' EXIT

ask() { # ask <question> <default> -> answer on stdout
    local answer
    if [ "$assume" = yes ]; then printf '%s' "$2"; return; fi
    read -r -p "$1 [$2]: " answer </dev/tty
    printf '%s' "${answer:-$2}"
}
ask_yes() { # ask_yes <question> <y|n>
    local answer
    answer=$(ask "$1 (y/n)" "$2")
    case "$answer" in [Yy]*) return 0 ;; *) return 1 ;; esac
}
ask_secret() { # ask_secret <question> -> answer on stdout, never shown
    local answer=""
    if [ "$assume" = yes ]; then return; fi
    read -r -s -p "$1: " answer </dev/tty
    printf '\n' >/dev/tty
    printf '%s' "$answer"
}

# ---- The package ----
package="$work/messages-server.tar.gz"
if [ -z "$tarball" ]; then
    [ -n "$RELEASE_URL" ] || fail "no release is published yet. Copy messages-server-<version>-linux-x64.tar.gz to this host (for example to /root) and run this again with --tarball /root/messages-server-<version>-linux-x64.tar.gz."
    tarball="$RELEASE_URL"
fi
case "$tarball" in
    https://*)
        say "Downloading $tarball..."
        curl -fsSL --proto '=https' --tlsv1.2 --retry 3 -o "$package" "$tarball" || fail "could not download $tarball."
        curl -fsSL --proto '=https' --tlsv1.2 --retry 3 -o "$work/package.sha256" "$tarball.sha256" || fail "could not download $tarball.sha256, which the package is checked against."
        ;;
    http://*|ftp://*) fail "the package must come over https." ;;
    *)
        [ -f "$tarball" ] || fail "no file $tarball."
        cp "$tarball" "$package"
        if [ -f "$tarball.sha256" ]; then cp "$tarball.sha256" "$work/package.sha256"; fi
        ;;
esac
sha=$(sha256sum "$package" | cut -d ' ' -f 1)
if [ -f "$work/package.sha256" ]; then
    want=$(cut -d ' ' -f 1 "$work/package.sha256" | head -n 1 | tr 'A-F' 'a-f')
    [ "$want" = "$sha" ] || fail "the package does not match its .sha256; nothing was changed."
    say "The package matches its .sha256."
fi
top=$(tar -tzf "$package" 2>/dev/null | head -n 1 | cut -d / -f 1) || true
[[ "$top" =~ ^messages-server-[0-9A-Za-z.+-]+-linux-x64$ ]] || fail "$tarball is not a Messages Server package (messages-server-<version>-linux-x64.tar.gz)."
for member in install.sh messages-server messages-tailnet; do
    tar -tzf "$package" "$top/$member" >/dev/null 2>&1 || fail "$tarball has no $member: use a package from 10.11 or later."
done
if [ "$google" = yes ] && ! tar -tzf "$package" "$top/gmessages-helper" >/dev/null 2>&1; then
    fail "$tarball has no Google helpers; use a newer package, or add --no-google."
fi
version=${top#messages-server-}; version=${version%-linux-x64}
say "Messages Server $version."

# ---- An existing container: update it ----
existing=""
if [ -z "$ctid" ]; then
    while read -r id _; do
        [ -n "$id" ] || continue
        if pct config "$id" 2>/dev/null | grep -Eq '^tags:.*messages-server'; then existing="$id"; break; fi
    done < <(pct list 2>/dev/null | tail -n +2)
    ctid="${existing:-$(pvesh get /cluster/nextid)}"
    if [ -z "$existing" ]; then ctid=$(ask "Container id" "$ctid"); fi
fi
case "$ctid" in ''|*[!0-9]*) fail "the container id must be a number." ;; esac

in_ct() { pct exec "$ctid" -- "$@"; }
wait_for_network() {
    local tries
    for tries in $(seq 1 60); do
        if in_ct sh -c 'getent hosts deb.debian.org >/dev/null 2>&1'; then return 0; fi
        sleep 2
    done
    fail "container $ctid has no network after 2 minutes. Check its bridge and address (pct config $ctid), then run this again."
}

update=no
if pct status "$ctid" >/dev/null 2>&1; then
    pct config "$ctid" | grep -Eq '^tags:.*messages-server' \
        || fail "container $ctid exists and is not one this script made. Choose another id (--ctid), or remove it first."
    say "Container $ctid is the Messages Server container this script made earlier."
    if ! ask_yes "Update Messages Server in it to $version? Its data, keys, sign-ins and paired devices stay" y; then
        say "Nothing was changed."; exit 0
    fi
    update=yes
fi

if [ "$update" = yes ]; then
    if ! pct status "$ctid" | grep -q running; then
        say "Starting container $ctid..."
        pct start "$ctid"
    fi
    wait_for_network
    if ! in_ct test -x /opt/messages-server/messages-server; then
        fail "container $ctid was made by an earlier run that did not finish. Remove it (pct stop $ctid; pct destroy $ctid) and run this again."
    fi
    flag=""
    # The container keeps Google's setting (install.sh reads it) unless this run said --no-google.
    if [ "$google" = no ]; then flag="--no-google"; fi
    pct push "$ctid" "$package" /root/messages-server.tar.gz
    in_ct bash -c "set -e; rm -rf /root/messages-server-install; mkdir -p /root/messages-server-install
        tar -xzf /root/messages-server.tar.gz -C /root/messages-server-install --no-same-owner
        /root/messages-server-install/$top/install.sh $flag
        echo $sha > /opt/messages-server/.package-sha256
        rm -rf /root/messages-server-install /root/messages-server.tar.gz"
    say ""
    say "Messages Server in container $ctid is now $version."
    exit 0
fi

# ---- A new container: the questions ----
say ""
say "A new container for Messages Server. Press Enter to take the value in brackets."
host=$(ask "Hostname" "$hostname_default")
[[ "$host" =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] || fail "the hostname must be lowercase letters, digits and dashes."

mapfile -t template_stores < <(pvesm status -content vztmpl 2>/dev/null | tail -n +2 | awk '$3 == "active" {print $1}')
mapfile -t disk_stores < <(pvesm status -content rootdir 2>/dev/null | tail -n +2 | awk '$3 == "active" {print $1}')
[ "${#template_stores[@]}" -gt 0 ] || fail "no active storage holds container templates (content vztmpl). Enable it on a storage in Datacenter, Storage."
[ "${#disk_stores[@]}" -gt 0 ] || fail "no active storage holds container disks (content rootdir). Enable it on a storage in Datacenter, Storage."
default_tstore="${template_stores[0]}"; for s in "${template_stores[@]}"; do [ "$s" = local ] && default_tstore=local; done
default_dstore="${disk_stores[0]}"; for s in "${disk_stores[@]}"; do [ "$s" = local-lvm ] && default_dstore=local-lvm; done
tstore=$(ask "Storage for the Debian template (${template_stores[*]})" "$default_tstore")
dstore=$(ask "Storage for the container's disk (${disk_stores[*]})" "$default_dstore")
printf '%s\n' "${template_stores[@]}" | grep -qx "$tstore" || fail "$tstore is not one of: ${template_stores[*]}."
printf '%s\n' "${disk_stores[@]}" | grep -qx "$dstore" || fail "$dstore is not one of: ${disk_stores[*]}."

if [ "$google" = yes ]; then memory_default=4096; else memory_default=2048; fi
disk=$(ask "Disk size in GB" 8)
memory=$(ask "Memory in MB" "$memory_default")
cores=$(ask "Processor cores" 2)
for value in "$disk" "$memory" "$cores"; do case "$value" in ''|*[!0-9]*) fail "sizes are whole numbers." ;; esac; done
[ "$disk" -ge 4 ] || fail "4 GB of disk at least (Chrome and the history need room)."
[ "$memory" -ge 1024 ] || fail "1024 MB of memory at least."

mapfile -t bridges < <(ip -o link show type bridge 2>/dev/null | awk -F': ' '{print $2}' | cut -d @ -f 1)
default_bridge=vmbr0; [ "${#bridges[@]}" -gt 0 ] && ! printf '%s\n' "${bridges[@]}" | grep -qx vmbr0 && default_bridge="${bridges[0]}"
bridge=$(ask "Network bridge (${bridges[*]:-none found})" "$default_bridge")
address=$(ask "Address: dhcp, or a fixed one such as 192.168.1.50/24" dhcp)
net="name=eth0,bridge=$bridge"
if [ "$address" = dhcp ]; then
    net="$net,ip=dhcp"
else
    [[ "$address" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2}$ ]] || fail "the address must be dhcp or IPv4 with its prefix, such as 192.168.1.50/24."
    gateway=$(ask "Gateway" "$(ip -4 route show default 2>/dev/null | awk '{print $3; exit}')")
    [[ "$gateway" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || fail "the gateway must be an IPv4 address."
    net="$net,ip=$address,gw=$gateway"
fi

backups_host=$(ask "A folder on this host for the server's backups (empty: inside the container)" "")
if [ -n "$backups_host" ]; then
    case "$backups_host" in /*) ;; *) fail "the backups folder must be a full path, such as /mnt/backups/messages." ;; esac
    case "$backups_host" in /|/etc*|/root|/root/*|/var/lib/vz|/var/lib/vz/*|/proc*|/sys*|/dev*|/boot*) fail "choose a folder of its own for the backups, not $backups_host." ;; esac
fi

tailnet=no
if ask_yes "Reach the server from anywhere through Tailscale (recommended for the phone)" y; then tailnet=yes; fi
authkey=""
if [ "$tailnet" = yes ] && [ "$assume" = no ]; then
    say "  The server's page signs it in to Tailscale with a link. For a machine you will not open a browser for, paste a"
    say "  Tailscale auth key instead (Tailscale's admin console, Settings, Keys). A tagged key lets in every device your"
    say "  access rules allow, not only your own. Press Enter to use the link."
    authkey=$(ask_secret "Tailscale auth key (hidden; Enter for none)")
    if [ -n "$authkey" ] && ! [[ "$authkey" =~ ^tskey-[A-Za-z0-9_-]{8,500}$ ]]; then fail "that is not a Tailscale auth key (tskey-...)."; fi
fi

password=""
if [ "$assume" = no ]; then
    say "  The server's page needs a password. Set one now, or press Enter and set it on the page's first visit with the"
    say "  setup code this script prints at the end."
    password=$(ask_secret "Page password (hidden; 12 characters or more; Enter to set it on the page)")
    if [ -n "$password" ]; then
        [ "${#password}" -ge 12 ] && [ "${#password}" -le 128 ] || fail "the page password must be 12 to 128 characters."
        case "$password" in *'"'*|*'\'*|*'$'*|*'`'*) fail "use a page password without \", \\, \$ or \`, or set it on the page instead." ;; esac
        again=$(ask_secret "The same password again")
        [ "$again" = "$password" ] || fail "the two passwords differ."
    fi
fi

# Chrome's sandbox needs the kernel to let ordinary accounts make user namespaces, in the container as on the host.
if [ "$google" = yes ]; then
    userns=$(cat /proc/sys/user/max_user_namespaces 2>/dev/null || echo 0)
    if [ "${userns:-0}" -le 0 ] 2>/dev/null || [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null || echo 0)" = 1 ]; then
        say "WARNING: this host's kernel does not let ordinary accounts make user namespaces, which Chrome's sandbox is built from."
        say "         Google will stay off (the server's log says sandbox_unavailable); Messenger and Instagram are not affected."
    fi
fi

say ""
say "About to make container $ctid:"
say "  $host, unprivileged Debian 13, nesting on, ${disk} GB on $dstore, ${memory} MB, $cores cores, $bridge ($address), starts at boot"
say "  Google: $google    Tailscale: $tailnet$([ -n "$authkey" ] && printf ' (auth key given)')    Backups: ${backups_host:-inside the container}"
if [ "$assume" = no ] && ! ask_yes "Go ahead" y; then say "Nothing was changed."; exit 0; fi

# ---- The template ----
template=$(pveam list "$tstore" 2>/dev/null | awk '{print $1}' | sed -n "s#^$tstore:vztmpl/\($template_series[^ ]*\)#\1#p" | sort -V | tail -n 1)
if [ -z "$template" ]; then
    say "Fetching the list of container templates..."
    pveam update >/dev/null
    template=$(pveam available --section system 2>/dev/null | awk '{print $2}' | grep "^$template_series" | sort -V | tail -n 1)
    [ -n "$template" ] || fail "Proxmox offers no $template_series template; update Proxmox VE (Debian 13 needs 8.4 or later)."
    say "Downloading $template to $tstore..."
    pveam download "$tstore" "$template" >/dev/null
fi
say "Template: $template"

# ---- The container ----
options=(--hostname "$host" --unprivileged 1 --features nesting=1 --ostype debian --cores "$cores" --memory "$memory" --swap 512
    --rootfs "$dstore:$disk" --net0 "$net" --onboot 1 --tags messages-server --timezone host
    --description "Messages Server (install-proxmox.sh). Its settings: /etc/messages-server/environment inside the container.")
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
say "Making container $ctid..."
pct create "$ctid" "$tstore:vztmpl/$template" "${options[@]}" >/dev/null
pct start "$ctid"
wait_for_network
say "Container $ctid is running. Installing Debian's updates..."
in_ct bash -c 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq && apt-get upgrade -y -qq -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold >/dev/null
    apt-get install -y -qq --no-install-recommends ca-certificates curl unattended-upgrades >/dev/null'

ip=$(in_ct sh -c "hostname -I 2>/dev/null" | tr ' ' '\n' | grep -E '^[0-9]+(\.[0-9]+){3}$' | head -n 1 || true)

# The settings, written before the first start (install.sh keeps a settings file it finds). Root only, in the container
# and in this script's private folder until it is pushed; the secrets are never on a command line.
{
    printf '# Messages Server'"'"'s settings (install-proxmox.sh). systemctl restart messages-server after a change.\n'
    printf 'MESSAGES_HOSTNAMES=%s\n' "$host${ip:+,$ip}"
    printf 'MESSAGES_TAILNET=%s\n' "$([ "$tailnet" = yes ] && echo 1 || echo 0)"
    printf 'MESSAGES_TAILNET_NAME=%s\n' "$host"
    if [ -n "$authkey" ]; then printf 'MESSAGES_TAILNET_AUTHKEY=%s\n' "$authkey"; fi
    if [ -n "$password" ]; then printf 'MESSAGES_ADMIN_PASSWORD="%s"\n' "$password"; fi
} > "$work/environment"
chmod 600 "$work/environment"
unset password again authkey
{
    printf '# The nightly update (install.sh explains it). Empty: the server is not updated, Chrome still is.\n'
    printf 'MESSAGES_RELEASE_URL=%s\n' "$RELEASE_URL"
} > "$work/update"
# This container has a network of its own (its own veth on $bridge): the server's word for offering Google's sign-in
# in its own browser (the Unraid script says the same of its bridge network).
printf '[Service]\nEnvironment=MESSAGES_NETWORK_ISOLATED=1\n' > "$work/proxmox.conf"
in_ct mkdir -p /etc/messages-server /etc/systemd/system/messages-server.service.d
in_ct chmod 755 /etc/messages-server
pct push "$ctid" "$work/environment" /etc/messages-server/environment --perms 0600
pct push "$ctid" "$work/update" /etc/messages-server/update --perms 0600
pct push "$ctid" "$work/proxmox.conf" /etc/systemd/system/messages-server.service.d/proxmox.conf --perms 0644
rm -f "$work/environment"

say "Installing Messages Server $version$([ "$google" = yes ] && printf ' with Google Chrome')..."
flag=""; [ "$google" = yes ] && flag="--google"
pct push "$ctid" "$package" /root/messages-server.tar.gz
in_ct bash -c "set -e; rm -rf /root/messages-server-install; mkdir -p /root/messages-server-install
    tar -xzf /root/messages-server.tar.gz -C /root/messages-server-install --no-same-owner
    /root/messages-server-install/$top/install.sh $flag
    echo $sha > /opt/messages-server/.package-sha256
    rm -rf /root/messages-server-install /root/messages-server.tar.gz"

# ---- What to do next ----
for _ in $(seq 1 60); do
    in_ct sh -c 'journalctl -u messages-server --no-pager -o cat 2>/dev/null | grep -q "server listening"' && break
    sleep 1
done
log=$(in_ct sh -c 'journalctl -u messages-server --no-pager -o cat 2>/dev/null' || true)
setup=$(printf '%s\n' "$log" | sed -n 's/.*server setup code=\([^ ]*\).*/\1/p' | tail -n 1)
pairing=$(printf '%s\n' "$log" | sed -n 's/.*server pairing code=\([^ ]*\).*/\1/p' | tail -n 1)
fingerprint=$(printf '%s\n' "$log" | sed -n 's/.*server tls fingerprint sha256=\([0-9a-f]*\).*/\1/p' | tail -n 1)
login=$(printf '%s\n' "$log" | sed -n 's/.*tailnet login url=\([^ ]*\).*/\1/p' | tail -n 1)

# Named here, not as a ${ip:-...} default: an apostrophe inside one is a bad substitution in bash.
where="$ip"
[ -n "$where" ] || where="<the container's address>"
say ""
say "Messages Server $version is running in container $ctid ($host)."
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
if [ -n "$RELEASE_URL" ]; then say "Updates:              each night at about 03:00, from $RELEASE_URL"
else say "Updates:              run this script again with a newer --tarball (Chrome updates itself each night)"; fi
say "To remove it:         pct stop $ctid && pct destroy $ctid$([ -n "$backups_host" ] && printf ' (the backups in %s stay)' "$backups_host")"
