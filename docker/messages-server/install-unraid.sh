#!/bin/bash
# Builds and (re)starts the Message Link Server container on Unraid from the bundle beside this script by default.
# --ghcr pulls the private image instead, using Docker's saved read:packages login (README, Private GHCR images).
# (tools\Publish-ServerBundle.ps1 puts both in a share). Run it from Unraid's terminal:
#
#   bash /mnt/user/tmp/messages-server/install-unraid.sh
#   bash /mnt/user/tmp/messages-server/install-unraid.sh --chrome-sandbox   # with Chrome's own sandbox on (README, Chrome's sandbox)
#   bash /mnt/user/tmp/messages-server/install-unraid.sh --ghcr --chrome-sandbox
#
# The one image is Debian with official Google Chrome (SWITCHBOARD.md stage 11; 4.3, 5.4): always the newest stable at build time, which the server keeps running, signed in by hand, to renew Google's sign-in) and turns Google
# Messages on. Chromium runs without its own sandbox there, chosen for the 5.4 test (2026-10-09):
# the container drops every capability, so the sandbox cannot start, and the browser loads only Google's own sites
# (docs/research/google-linux-renewal.md). It also runs Chromium headed, with a window on a virtual display (Xvfb, one
# per browser run, inside the container): the headless browser loaded Google's page signed in but Google never rotated
# its sign-in cookie (5.4, 2026-10-10). The log says headed=yes on `server google enabled` and each `google browser`
# line, and the keeper's `browser early=` lines now appear in `docker logs`.
#
# It unpacks the source to /tmp, builds the image there, keeps the server's sealed data in appdata and its keys in the
# Docker named volume messages-server-keys (inside Docker's own storage, outside every share), and replaces a running
# container gently (a 60 s stop, so sends finish). Nothing else on the box is touched.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
bundle="$here/messages-source.tar.gz"
# Settings written by the publisher: MESSAGES_HOSTNAMES (the names and addresses devices reach this server by) and
# MESSAGES_BIND_IP (the LAN address to publish the port on). The server page's password is not kept here (this file is
# rewritten by every publish, and sits on a share): a reinstall keeps the running container's (see below).
[ -f "$here/install.env" ] && . "$here/install.env"

chrome_sandbox=0
ghcr=0
for arg in "$@"; do
    case "$arg" in
        --chrome-sandbox) chrome_sandbox=1 ;;
        --ghcr) ghcr=1 ;;
        *) echo "Unknown option: $arg (the options are --chrome-sandbox and --ghcr)." >&2; exit 1 ;;
    esac
done
if [ "$ghcr" = 0 ]; then
    [ -f "$bundle" ] || { echo "No messages-source.tar.gz beside this script." >&2; exit 1; }
fi
image="messages-server:local"
dockerfile="Dockerfile"
# MESSAGES_NETWORK_ISOLATED=1: this container is published with -p on a bridge network, so it has a network namespace of its own; the remote Google sign-in is offered only on that proof.
extra_env=(-e MESSAGES_GOOGLE=1 -e MESSAGES_GOOGLE_CHROME=1 -e MESSAGES_CHROMIUM_NO_SANDBOX=1 -e MESSAGES_CHROMIUM_HEADED=1 -e MESSAGES_NETWORK_ISOLATED=1)
# The image always takes the newest stable Chrome: the build time is an argument the Chrome layer names, so Docker never reuses a cached Chrome. It is also how old the server says Chrome is.
build_args=(--build-arg "CHROME_REFRESH=$(date -u +%Y%m%dT%H%M%SZ)")
if [ "$ghcr" = 1 ]; then
    # Docker's login stays on the host, never in install.env, a share, the image, or either account volume.
    repository="${MESSAGES_GHCR_IMAGE:-ghcr.io/wrenchware/messages-server}"
    tag="${MESSAGES_GHCR_TAG:-latest}"
    if [[ ! "$repository" =~ ^ghcr\.io/[a-z0-9][a-z0-9._-]*/messages-server$ ]] || [[ ! "$tag" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,120}$ ]]; then
        echo "Use MESSAGES_GHCR_IMAGE=ghcr.io/<owner>/messages-server and MESSAGES_GHCR_TAG=latest (or an app version or exact build tag)." >&2
        exit 1
    fi
    image="$repository:$tag"
fi
# --chrome-sandbox: Chrome with its own sandbox on, not the owner's 2026-10-09 choice of no sandbox (docs/research/google-linux-renewal.md). Chrome runs as the account that can read the keys
# volume, so a page's code that broke out of a renderer would have everything; the sandbox is what stops that. It needs unprivileged user namespaces, which Docker's default seccomp profile
# refuses a container with no capability, so the container is run with chrome-seccomp.json (Docker's default profile plus chroot, clone, setns and unshare) instead of getting
# SYS_ADMIN or --privileged, and MESSAGES_CHROMIUM_NO_SANDBOX=1 is left out. The server then checks it can make a user namespace before it starts Chrome, and the log says
# `google chrome sandbox=active` once a page's renderers are seen filtered. Without the option nothing changes. Checked here first, so a kernel that cannot do it is said before anything is built.
seccomp_args=()
if [ "$chrome_sandbox" = 1 ]; then
    userns="$(cat /proc/sys/user/max_user_namespaces 2>/dev/null || echo 0)"
    case "$userns" in ''|*[!0-9]*) userns=0 ;; esac
    if [ "$userns" -le 0 ] || [ "$(cat /proc/sys/kernel/unprivileged_userns_clone 2>/dev/null || echo 1)" = 0 ]; then
        echo "This server's kernel does not allow unprivileged user namespaces (user.max_user_namespaces=$userns), which Chrome's sandbox is built from." >&2
        echo "Run without --chrome-sandbox to keep Chrome without its sandbox, or enable them in the kernel first (README, Chrome's sandbox)." >&2
        exit 1
    fi
    # The same environment without the switch that turns the sandbox off.
    kept=()
    for word in "${extra_env[@]}"; do
        if [ "$word" = "-e" ]; then pending="$word"; continue; fi
        if [ -n "${pending:-}" ]; then
            if [ "$word" != "MESSAGES_CHROMIUM_NO_SANDBOX=1" ]; then kept+=("$pending" "$word"); fi
            pending=""
            continue
        fi
        kept+=("$word")
    done
    extra_env=("${kept[@]}")
fi
# What the container may use, so a flood of requests, a runaway browser or a leak cannot take the whole box (external security review, 2026-10-11).
#   memory   Memory and swap together (no swap beyond it). Chrome (a window on a virtual display, one page, plus its renderer,
#            GPU-less compositor and network processes) takes 0.6 to 1.2 GB by itself, so it is 4g; Chrome is the thing that gets killed first if it leaks,
#            not the box.
#   pids     Threads count as processes here. The server and four Go helpers hold some hundreds of threads between them; Chrome alone holds a few hundred.
#            2048: a fork bomb is stopped, normal use never gets near.
#   cpus     Not limited by default (the number of cores is not known here, and Docker refuses more than the box has); set MESSAGES_CPUS=2 to cap it.
# Each can be changed with MESSAGES_MEMORY, MESSAGES_PIDS_LIMIT, MESSAGES_CPUS in install.env or the environment.
default_memory="4g"; default_pids=2048
memory="${MESSAGES_MEMORY:-$default_memory}"
pids="${MESSAGES_PIDS_LIMIT:-$default_pids}"
cpus="${MESSAGES_CPUS:-}"
case "$memory" in [1-9]*[mMgG]) ;; *) echo "MESSAGES_MEMORY is not a size such as 2g or 1500m." >&2; exit 1 ;; esac
case "$pids" in ''|*[!0-9]*) echo "MESSAGES_PIDS_LIMIT is not a number." >&2; exit 1 ;; esac
cpu_args=()
if [ -n "$cpus" ]; then
    case "$cpus" in *[!0-9.]*) echo "MESSAGES_CPUS is not a number such as 2 or 1.5." >&2; exit 1 ;; esac
    cpu_args=(--cpus "$cpus")
fi
name="messages-server"
keys="messages-server-keys"
# The server's own account: nothing else on Unraid uses it (not nobody:users, 99:100, the Samba guest and most
# containers' PUID).
owner="2787:2787"
appdata="${MESSAGES_APPDATA:-/mnt/user/appdata/messages-server}"
# The server's own backups (README, Backups) go to a backups share, outside appdata, which the server sees as /backups.
backups="${MESSAGES_BACKUPS_DIR:-/mnt/user/backups/messages}"
port="${MESSAGES_PORT:-8787}"
bind="${MESSAGES_BIND_IP:-}"
case "$port" in ''|*[!0-9]*) echo "MESSAGES_PORT is not a port number." >&2; exit 1 ;; esac
if [ -n "$bind" ]; then
    case "$bind" in
        *[!0-9A-Fa-f.:]*) echo "MESSAGES_BIND_IP is not an IP address." >&2; exit 1 ;;
        *:*) publish="[$bind]:$port:8787" ;;
        *) publish="$bind:$port:8787" ;;
    esac
else
    # IPv4 only: Docker carries an IPv6 connection to a container through its userland proxy, so the server would see the
    # bridge's address instead of the device's, and tell no device on IPv6 apart (its pacing, and the page's home-network
    # rule). An IPv6 MESSAGES_BIND_IP is allowed, with that cost (README, "The server's page").
    publish="0.0.0.0:$port:8787"
fi
# Unraid's WebUI button opens the server's page. It names the server by the first of its certificate's names, so the
# browser meets the name the certificate was made for (Unraid's [IP] when there is none); Unraid fills [PORT:8787] with
# the port published for it.
webui_host="${MESSAGES_HOSTNAMES:-}"; webui_host="${webui_host%%,*}"; webui_host="${webui_host// /}"
case "$webui_host" in '') webui_host="[IP]" ;; *:*) webui_host="[$webui_host]" ;; esac
webui="https://$webui_host:[PORT:8787]/"
# Refuse before building or stopping anything when something other than this server already holds the port; otherwise
# the old container would be stopped and the new one fail to start. Another container publishing it is found from
# Docker itself (a port published without Docker's userland proxy has no listener ss can see); anything else from ss,
# unless it is this server's own container on this same port (a reinstall on a new port is checked like any other).
port_held() {
    local others ours mapping host lo hi
    others="$(docker ps --format '{{.Names}}|{{.Ports}}' | grep -v "^$name|" | cut -d'|' -f2- || true)"
    # Each published mapping is "<ip>:<host port or range>-><container>/<proto>", comma-separated; a range such as
    # 8786-8788->8786-8788/tcp holds every port in it (code review, 2026-10-09).
    while IFS= read -r mapping; do
        case "$mapping" in *'->'*) ;; *) continue ;; esac
        host="${mapping%%->*}"; host="${host##*:}"
        lo="${host%-*}"; hi="${host#*-}"
        case "$lo$hi" in ''|*[!0-9]*) continue ;; esac
        [ "$port" -ge "$lo" ] && [ "$port" -le "$hi" ] && return 0
    done < <(tr ',' '\n' <<<"$others")
    command -v ss >/dev/null 2>&1 && [ -n "$(ss -Hltn "sport = :$port" 2>/dev/null)" ] || return 1
    ours="$(docker port "$name" 8787/tcp 2>/dev/null || true)"
    grep -Eq ":$port\$" <<<"$ours" && return 1
    return 0
}
if port_held; then
    echo "Port $port is already in use on this server by something else. Set MESSAGES_PORT to a free port in install.env, then run this again." >&2
    exit 1
fi
src="$(mktemp -d /tmp/messages-server-src.XXXXXX)"
asset_container=""
cleanup() {
    if [ -n "$asset_container" ]; then docker rm -v "$asset_container" >/dev/null 2>&1 || true; fi
    rm -rf "$src"
}
trap cleanup EXIT

if [ "$ghcr" = 1 ]; then
    echo "Pulling $image (log in to ghcr.io with a read:packages token first; see the README)..."
    # Pull and check the install assets before touching the running server. Never fall back to a stale image on failure.
    docker pull "$image"
    # create does not run the entrypoint or mount the real data/keys. Remove its anonymous image volumes too.
    asset_container="$(docker create --network none --entrypoint /bin/true "$image")"
    docker cp "$asset_container:/app/install/." "$src/"
    docker rm -v "$asset_container" >/dev/null
    asset_container=""
else
    echo "Unpacking the source..."
    tar -xzf "$bundle" -C "$src"
    echo "Building $image (the first build downloads the .NET and Go images and takes a few minutes)..."
    docker build ${build_args[@]+"${build_args[@]}"} -f "$src/docker/messages-server/$dockerfile" -t "$image" "$src"
fi
[ -f "$src/assets/app-icon.png" ] || { echo "The image or bundle has no install icon." >&2; exit 1; }
if [ "$chrome_sandbox" = 1 ] && [ ! -f "$src/docker/messages-server/chrome-seccomp.json" ]; then
    echo "The image or bundle has no Chrome sandbox profile." >&2; exit 1
fi
if [ "$chrome_sandbox" = 1 ]; then seccomp_args=(--security-opt "seccomp=$src/docker/messages-server/chrome-seccomp.json"); fi

# The server page's password (README, "The server's page"): this environment's whenever it is set at all (set empty, it
# turns the page off), else the running container's whenever the container has the variable (empty included), else the
# saved template's (a run that removed the container and then failed to start the new one), so a reinstall keeps one set
# in Unraid's editor and never brings back one that was cleared. One line. Never printed; docker is given the variable's
# name only, so the value is in no command line here (Unraid's own editor shows it in its docker command; README).
saved_template="/boot/config/plugins/dockerMan/templates-user/my-messages-server.xml"
if [ -z "${MESSAGES_ADMIN_PASSWORD+set}" ]; then
    container_env=""
    if docker inspect "$name" >/dev/null 2>&1; then
        container_env="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$name")"
    fi
    if grep -q '^MESSAGES_ADMIN_PASSWORD=' <<<"$container_env"; then
        MESSAGES_ADMIN_PASSWORD="$(sed -n 's/^MESSAGES_ADMIN_PASSWORD=//p' <<<"$container_env" | head -n 1)"
    elif [ -f "$saved_template" ]; then
        MESSAGES_ADMIN_PASSWORD="$(sed -n 's/.*Target="MESSAGES_ADMIN_PASSWORD"[^>]*>\([^<]*\)<\/Config>.*/\1/p' "$saved_template" | head -n 1 \
            | sed -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&quot;/"/g' -e "s/&apos;/'/g" -e 's/&amp;/\&/g')"
        # A character Unraid saved as a numeric reference is not decoded here: rather than keep a wrong password, the
        # page stays off until it is entered again.
        case "$MESSAGES_ADMIN_PASSWORD" in
            *'&#'*)
                MESSAGES_ADMIN_PASSWORD=""
                echo "The server page's password in the saved template could not be read exactly; enter it again in Unraid's editor (the page stays off until then)." >&2
                ;;
        esac
    fi
    unset container_env
fi
export MESSAGES_ADMIN_PASSWORD="${MESSAGES_ADMIN_PASSWORD:-}"

# The tailnet door (README, "Your tailnet"): this environment's MESSAGES_TAILNET and MESSAGES_TAILNET_NAME when set, else the running container's, so a
# reinstall neither takes the server off the owner's tailnet nor renames it there (its devices reach it by that name). Only 1 turns it on.
for variable in MESSAGES_TAILNET MESSAGES_TAILNET_NAME; do
    if [ -z "${!variable+set}" ] && docker inspect "$name" >/dev/null 2>&1; then
        printf -v "$variable" '%s' "$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$name" | sed -n "s/^$variable=//p" | head -n 1)"
    fi
done
case "${MESSAGES_TAILNET:-0}" in 1) MESSAGES_TAILNET=1 ;; *) MESSAGES_TAILNET=0 ;; esac
# One DNS label, as the server takes it; anything else is the default name.
[[ "${MESSAGES_TAILNET_NAME:-}" =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] || MESSAGES_TAILNET_NAME="messages-server"
# A Tailscale auth key for a headless sign-in, only when this environment gives one (README, Your tailnet): passed to docker by name, so the value is in no
# command line, and never written to install.env or the template. The door uses it only while it has no identity yet, which the keys volume keeps after.
authkey_env=()
if [ -n "${MESSAGES_TAILNET_AUTHKEY:-}" ]; then
    export MESSAGES_TAILNET_AUTHKEY
    authkey_env=(-e MESSAGES_TAILNET_AUTHKEY)
fi

# DockerMan recreates updates from a saved user template, not from our docker run command. Keep its sandbox profile
# on the flash drive (no secrets in it), since the source/asset scratch directory is removed when this script exits.
unraid_args=()
template=""
if [ "$ghcr" = 1 ]; then
    unraid_config="/boot/config/plugins/dockerMan"
    mkdir -p "$unraid_config/templates-user" "$unraid_config/messages-server"
    if [ "$chrome_sandbox" = 1 ]; then
        sandbox_profile="$unraid_config/messages-server/chrome-seccomp.json"
        install -m 0644 "$src/docker/messages-server/chrome-seccomp.json" "$sandbox_profile"
        seccomp_args=(--security-opt "seccomp=$sandbox_profile")
    fi
    unraid_args=(--label net.unraid.docker.managed=dockerman -e "TZ=${TZ:-Etc/UTC}")
    template="$unraid_config/templates-user/my-messages-server.xml"

    xml_escape() {
        local value="$1"
        value="${value//&/\&amp;}"; value="${value//</\&lt;}"; value="${value//>/\&gt;}"
        value="${value//\"/\&quot;}"; value="${value//\'/\&apos;}"
        printf '%s' "$value"
    }
    # Values passed inside ExtraParams need shell quoting as well as XML escaping. Bash's %q survives spaces and
    # quotes in a path, while XML Config values are handled as values by DockerMan itself.
    parameters="--init --user=$owner --stop-timeout=60 --restart=unless-stopped --security-opt=no-new-privileges:true --cap-drop=ALL --memory=$memory --memory-swap=$memory --pids-limit=$pids -v $keys:/keys"
    if [ -n "$cpus" ]; then parameters+=" --cpus=$cpus"; fi
    if [ "$chrome_sandbox" = 1 ]; then
        printf -v quoted_profile '%q' "seccomp=$sandbox_profile"
        parameters+=" --security-opt=$quoted_profile"
    fi
    # ExtraParams owns the port mapping so DockerMan preserves the explicit LAN bind, including IPv6.
    parameters+=" -p $publish"
    # Every Config carries Default, Mode, Description, Required and Mask, even empty. Unraid's editor stops drawing
    # fields at the first Variable without a Default (a script error), and its Apply then saves only the fields it drew,
    # dropping every variable from the container (seen 2026-10-13: the editor showed Data alone, with "{4}" under it).
    cat > "$src/unraid-template.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<Container version="2">
  <Name>$name</Name>
  <Repository>$(xml_escape "$image")</Repository>
  <Registry>https://ghcr.io</Registry>
  <Network>bridge</Network>
  <Shell>bash</Shell>
  <Privileged>false</Privileged>
  <Overview>Message Link Server. Keeps your messaging accounts connected on this server so Message Link on your PCs can use them over your home network. Keep the keys volume and the data folder across updates.</Overview>
  <Category>Network:Messenger</Category>
  <Icon>file:///boot/config/plugins/dockerMan/messages-server/icon.png</Icon>
  <WebUI>$(xml_escape "$webui")</WebUI>
  <ExtraParams>$(xml_escape "$parameters")</ExtraParams>
  <Config Name="Data" Target="/data" Default="$(xml_escape "$appdata")" Mode="rw" Description="Sealed sessions, history and paired devices, useless without the keys volume." Type="Path" Display="always" Required="true" Mask="false">$(xml_escape "$appdata")</Config>
  <Config Name="Backups" Target="/backups" Default="/mnt/user/backups/messages" Mode="rw" Description="Where the server writes its own backups (README, Backups). Owned by 2787:2787, mode 700." Type="Path" Display="always" Required="false" Mask="false">$(xml_escape "$backups")</Config>
  <Config Name="Data folder" Target="MESSAGES_DATA" Default="/data" Mode="" Description="Leave as /data, the folder mapped above." Type="Variable" Display="advanced" Required="true" Mask="false">/data</Config>
  <Config Name="Keys folder" Target="MESSAGES_KEYS" Default="/keys" Mode="" Description="Leave as /keys, the volume in Extra Parameters." Type="Variable" Display="advanced" Required="true" Mask="false">/keys</Config>
  <Config Name="Resume sign-ins" Target="MESSAGES_RESTORE" Default="1" Mode="" Description="1 resumes the accounts signed in on this server when it starts. 0 starts with none connected." Type="Variable" Display="always" Required="false" Mask="false">1</Config>
  <Config Name="Server page password" Target="MESSAGES_ADMIN_PASSWORD" Default="" Mode="" Description="Turns on the server's page at https://(this server):8787/. 12 to 128 characters, used nowhere else; no double spaces, no space at either end, no ampersand. Empty leaves the page off." Type="Variable" Display="always" Required="false" Mask="true">$(xml_escape "${MESSAGES_ADMIN_PASSWORD:-}")</Config>
  <Config Name="Reach from your tailnet" Target="MESSAGES_TAILNET" Default="0" Mode="" Description="1 puts the server on your Tailscale network as a machine of its own (messages-server); the port stays on the home network. The first start prints a sign-in link in the log (README, Your tailnet)." Type="Variable" Display="always" Required="false" Mask="false">$MESSAGES_TAILNET</Config>
  <Config Name="Tailnet name" Target="MESSAGES_TAILNET_NAME" Default="messages-server" Mode="" Description="The server's name on your tailnet: lowercase letters, digits and dashes." Type="Variable" Display="advanced" Required="false" Mask="false">$MESSAGES_TAILNET_NAME</Config>
  <Config Name="Server names" Target="MESSAGES_HOSTNAMES" Default="" Mode="" Description="Names and addresses devices reach the server by, comma-separated, written into its certificate when it is first made." Type="Variable" Display="advanced" Required="false" Mask="false">$(xml_escape "${MESSAGES_HOSTNAMES:-}")</Config>
  <Config Name="Time zone" Target="TZ" Default="Etc/UTC" Mode="" Description="" Type="Variable" Display="advanced" Required="false" Mask="false">$(xml_escape "${TZ:-Etc/UTC}")</Config>
EOF
    for ((i=1; i<${#extra_env[@]}; i+=2)); do
        key="${extra_env[i]%%=*}"; value="${extra_env[i]#*=}"
        printf '  <Config Name="%s" Target="%s" Default="" Mode="" Description="" Type="Variable" Display="advanced" Required="false" Mask="false">%s</Config>\n' "$key" "$key" "$(xml_escape "$value")" >> "$src/unraid-template.xml"
    done
    echo '</Container>' >> "$src/unraid-template.xml"
fi

# The data folder: the server's account owns everything in it (an earlier install ran as 99:100), and nobody else may
# even list it.
mkdir -p "$appdata"
chown -R "$owner" "$appdata"
chmod 700 "$appdata"
# The backups folder, the same way: the server's account alone. Its contents are left as they are (they are the
# server's own, written as that account).
mkdir -p "$backups"
chown "$owner" "$backups"
chmod 700 "$backups"
# The keys volume: made once and kept across reinstalls, owned by the server's account and private to it (set here
# rather than left to Docker's copy of the image's /keys). An earlier install's keys in the data folder are moved into
# it by the server at its first start.
docker volume inspect "$keys" >/dev/null 2>&1 || docker volume create "$keys" >/dev/null
docker run --rm --user 0:0 --network none --entrypoint /bin/sh -v "$keys:/keys" "$image" \
    -c "chown -R $owner /keys && chmod 700 /keys"

if docker inspect "$name" >/dev/null 2>&1; then
    echo "Stopping the running $name (up to 60 s, so anything sending finishes)..."
    docker stop -t 60 "$name" >/dev/null || true
    docker rm "$name" >/dev/null
fi

# Unraid's Docker page shows the icon a container's net.unraid.docker.icon label names, fetched once into its own icon
# folder. A container without one falls back to question.png, which this Unraid lacks: the page then asked for it many
# times a second, the flicker I saw, and filled nginx's error log and with it /var/log (2026-10-10). So the app's
# own icon is put in that folder, and the label names it by a file:// address, which Unraid's fetch reads locally.
# That folder is Unraid's cache, not storage: DockerMan deletes its copy when the template's icon changes or the
# container is removed with its image, and refills it only from the address named. The address once named the cached
# copy itself, so a cleared cache left nothing to fetch and the icon stayed gone after updates through Unraid
# (2026-10-13). The kept copy sits on the flash beside the sandbox profile.
icon_store="/boot/config/plugins/dockerMan/messages-server"
icons="/var/lib/docker/unraid/images"
mkdir -p "$icon_store" "$icons"
install -m 0644 "$src/assets/app-icon.png" "$icon_store/icon.png"
cp "$src/assets/app-icon.png" "$icons/$name-icon.png"
rm -f "/usr/local/emhttp/state/plugins/dynamix.docker.manager/images/$name-icon.png"

echo "Starting $name..."
docker run -d --name "$name" --restart unless-stopped --init --stop-timeout 60 \
    ${unraid_args[@]+"${unraid_args[@]}"} \
    --label "net.unraid.docker.icon=file://$icon_store/icon.png" \
    --label "net.unraid.docker.webui=$webui" \
    --user "$owner" --security-opt no-new-privileges:true --cap-drop ALL ${seccomp_args[@]+"${seccomp_args[@]}"} \
    --memory "$memory" --memory-swap "$memory" --pids-limit "$pids" ${cpu_args[@]+"${cpu_args[@]}"} \
    -p "$publish" \
    -v "$appdata:/data" \
    -v "$keys:/keys" \
    -v "$backups:/backups" \
    -e MESSAGES_DATA=/data \
    -e MESSAGES_KEYS=/keys \
    -e MESSAGES_RESTORE=1 \
    -e MESSAGES_ADMIN_PASSWORD \
    -e MESSAGES_HOSTNAMES="${MESSAGES_HOSTNAMES:-}" \
    -e MESSAGES_TAILNET="$MESSAGES_TAILNET" \
    -e MESSAGES_TAILNET_NAME="$MESSAGES_TAILNET_NAME" \
    ${authkey_env[@]+"${authkey_env[@]}"} \
    ${extra_env[@]+"${extra_env[@]}"} \
    "$image" >/dev/null

if [ -n "$template" ]; then
    # Replace the saved template only after Docker accepted the new container, so an unsuccessful pull/start keeps it.
    install -m 0644 "$src/unraid-template.xml" "$template"
    echo "Saved the matching Unraid template: $template"
    echo "Enable messages-server in Auto Update Applications, with its Docker stop timeout set to at least 60 seconds."
fi

# Wait for the server to say it is listening, then show what a device needs to pair.
for _ in $(seq 1 30); do
    docker logs "$name" 2>&1 | grep -q "server listening transport=network" && break
    sleep 1
done
echo
logs=$(docker logs "$name" 2>&1)
if printf '%s\n' "$logs" | grep -q "Server didn't start"; then
    printf '%s\n' "$logs" | grep "Server didn't start" | tail -n 1
else
    # What a PC needs to join, in plain words; the lines they come from stay in the log.
    fingerprint=$(printf '%s\n' "$logs" | sed -n 's/.*server tls fingerprint sha256=\([0-9a-f]*\).*/\1/p' | tail -n 1)
    code=$(printf '%s\n' "$logs" | sed -n 's/.*server pairing code=\([^ ]*\) .*/\1/p' | tail -n 1)
    # Named here, not as a ${bind:-...} default: an apostrophe inside one is a bad substitution in bash.
    where="$bind"
    [ -n "$where" ] || where="this server's address"
    echo "Message Link Server is running. To use it from Message Link on a PC: Settings, Message Link Server."
    echo "  Address:      $where:$port"
    [ -n "$fingerprint" ] && echo "  Fingerprint:  $fingerprint"
    [ -n "$code" ] && echo "  Pairing code: $code (works once, for 10 minutes)"
    echo "  With a page password set, a PC can also ask to join and you approve it on https://$where:$port"
    if [ "$MESSAGES_TAILNET" = 1 ]; then
        # The door's sign-in link, while it still needs one: shown in the container's log only, as the pairing code is.
        login=$(printf '%s\n' "$logs" | sed -n 's/.*tailnet login url=\([^ ]*\).*/\1/p' | tail -n 1)
        if [ -n "$login" ]; then echo "  Tailnet:      sign in to Tailscale to add this server to your tailnet: $login"
        else echo "  Tailnet:      on (docker logs $name 2>&1 | grep tailnet)"; fi
    fi
fi
echo
if [ -z "$bind" ]; then
    echo "WARNING: port $port is published on every interface of this server, a VPN's (Tailscale, WireGuard) included."
    echo "         Set MESSAGES_BIND_IP to this server's LAN address in install.env and run this again to limit it to the home network;"
    echo "         MESSAGES_TAILNET=1 reaches it from your tailnet without the port (README, Your tailnet)."
fi
echo "Keys: the Docker volume $keys, outside every share. It holds the keys and this server's Messenger history."
echo "      Losing it (for example if docker.img is recreated) loses that history and means signing in and pairing"
echo "      again; the data folder is sealed and useless without it. Back it up: see the README's Backups."
echo "Backups: see docker/messages-server/README.md (the keys go in a private 0600 file, never on an exported share)."
echo "Logs: docker logs -f $name    Stop: docker stop -t 60 $name"
echo "Fresh start for a move from a PC (empties the server's shared choices only; type RESET when asked; keep every other device closed until the move is done):"
echo "      docker stop -t 60 $name; docker run --rm -it --network none --user $owner -v $appdata:/data -e MESSAGES_DATA=/data \"\$(docker inspect -f '{{.Config.Image}}' $name)\" --reset-space; docker start $name"
echo "After a move back the server will not sign Messenger or Instagram in (it holds a note that they moved away). To let it again (type CLEAR when asked):"
echo "      docker stop -t 60 $name; docker run --rm -it --network none --user $owner -v $appdata:/data -v $keys:/keys -e MESSAGES_DATA=/data -e MESSAGES_KEYS=/keys \"\$(docker inspect -f '{{.Config.Image}}' $name)\" --clear-held; docker start $name"
