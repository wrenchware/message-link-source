#!/bin/sh
# Copyright (c) 2026 Wrenchware. MIT License; see LICENSE.
#
# Sets up Message Link reports for one Hermes profile, on the computer that runs Hermes. Run by the command the
# Message Link server's page shows, which checks this file's SHA-256 before running it:
#
#   MLSETUP=<setup code> sh install.sh [--port <port>] [--add] <profile> <sha256> <address>...
#
# With --add it also adds the profile to Message Link as an assistant: it gives the profile an API key when it has none
# and tells the Python part where Message Link's server might reach Hermes's API server.
#
# With Hermes in Docker, it finds Hermes's container (asking when there are several) and runs the Python part, after
# the marker line at the end of this file, inside it: as the owner of the profile's folder and with Hermes's own Python.
# With Hermes installed directly, it runs the Python part here. The setup code reaches Python through the environment
# only, never a command line. POSIX sh.

set -u
code=${MLSETUP:-}
unset MLSETUP

fail() {
    printf '%s\n' "$1" >&2
    exit "${2:-1}"
}

port=
if [ "${1:-}" = --port ]; then
    port=${2:-}
    shift 2 || fail "Run the command the Message Link server's page shows." 2
    case $port in '' | *[!0-9]*) fail "Run the command the Message Link server's page shows." 2 ;; esac
fi
# --add: add the profile as an assistant (its API key too), not only its reports.
add=
if [ "${1:-}" = --add ]; then
    add=--add
    shift
fi
[ $# -ge 3 ] || fail "Run the command the Message Link server's page shows." 2
profile=$1
case $profile in '' | [!a-z0-9]* | *[!a-z0-9_-]*) fail "That isn't a Hermes profile name." 2 ;; esac
[ -n "$code" ] || fail "The setup code is missing. Run the command the Message Link server's page shows." 2
[ -r "$0" ] || fail "Run the command the Message Link server's page shows: it saves this installer to a file first." 2

# The Python part: everything after the marker line.
python_part() {
    sed '1,/^#__ML_SETUP_PY__$/d' "$0"
}

# Hermes's own Python, given Hermes's command: the HERMES_PYTHON its Docker image sets, the interpreter the command's
# first line names when that is Python (the image puts a sh wrapper there), Hermes's venv, then python3. Each is run
# once to be sure it is Python 3.8 or later. Defined as text, so the same lines run inside Hermes's container too.
pick_python='
named_python() {
    [ -n "$1" ] || return 0
    line=$(head -n 1 "$1" 2>/dev/null) || return 0
    case $line in "#!"*) ;; *) return 0 ;; esac
    set -- ${line#??}
    if [ "${1##*/}" = env ] && [ $# -ge 2 ]; then set -- "$(command -v "$2" 2>/dev/null)"; fi
    case ${1##*/} in python*) printf "%s\n" "$1" ;; esac
}
pick_python() {
    for candidate in "${HERMES_PYTHON:-}" "$(named_python "$1")" /opt/hermes/.venv/bin/python "$(command -v python3 2>/dev/null)"; do
        if [ -n "$candidate" ] && [ -x "$candidate" ] \
            && "$candidate" -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" >/dev/null 2>&1; then
            printf "%s\n" "$candidate"
            return 0
        fi
    done
    return 1
}
'
eval "$pick_python"

# ---- Hermes in Docker ----

container=
list=
if command -v docker >/dev/null 2>&1; then
    if ! list=$(docker ps --format '{{.Names}}	{{.Image}}' 2>&1); then
        case $list in
        *ermission*) fail "Hermes may run in Docker here, and using Docker needs more rights. Run the command again as root, or as a user in the docker group." 2 ;;
        esac
        list=
    fi
fi
found=$(printf '%s\n' "$list" | awk -F '\t' 'NF == 2 && tolower($2) ~ /hermes/ { print $1 }')
count=$(printf '%s\n' "$found" | grep -c .)
if [ "$count" -eq 1 ]; then
    container=$found
elif [ "$count" -gt 1 ]; then
    printf 'Hermes runs in more than one container here:\n'
    printf '%s\n' "$found" | awk '{ printf "  %d. %s\n", NR, $0 }'
    if ! { : </dev/tty; } 2>/dev/null; then
        fail "Run the command in a terminal to choose one." 2
    fi
    printf 'Which one has the profile %s? Enter its number: ' "$profile" >/dev/tty
    read -r pick </dev/tty || pick=
    case $pick in '' | *[!0-9]*) fail "That isn't one of the numbers. Nothing changed." 2 ;; esac
    container=$(printf '%s\n' "$found" | sed -n "${pick}p")
    [ -n "$container" ] || fail "That isn't one of the numbers. Nothing changed." 2
fi

if [ -n "$container" ]; then
    # Hermes's home and the profile's folder, its owner, Hermes's command and its Python, as the container sees them.
    info=$(docker exec "$container" sh -c "$pick_python"'
        profile=$1
        home=${HERMES_HOME:-$HOME/.hermes}
        if [ "$profile" = default ]; then folder=$home; else folder=$home/profiles/$profile; fi
        [ -d "$folder" ] || { printf "missing\n%s\n" "$folder"; exit 0; }
        owner=$(stat -c %u:%g "$folder" 2>/dev/null) || owner=
        hermes=$(command -v hermes 2>/dev/null) || hermes=
        python=$(pick_python "$hermes") || python=
        # The API port as Hermes takes it when it starts: its own .env over the environment.
        api=$(sed -n "s/^[[:space:]]*\(export[[:space:]][[:space:]]*\)\{0,1\}API_SERVER_PORT[[:space:]]*=[[:space:]]*[\"'\'']\{0,1\}\([0-9][0-9]*\).*/\2/p" "$home/.env" 2>/dev/null | tail -n 1)
        printf "ok\n%s\n%s\n%s\n%s\n%s\n" "$home" "$owner" "$python" "$hermes" "${api:-${API_SERVER_PORT:-}}"
    ' sh "$profile" 2>/dev/null) || fail "Couldn't look inside the container $container. Nothing changed."
    state=$(printf '%s\n' "$info" | sed -n 1p)
    if [ "$state" = missing ]; then
        fail "The container $container has no Hermes profile named $profile ($(printf '%s\n' "$info" | sed -n 2p) isn't there). Make it in Hermes first (hermes profile create $profile), then run the command again. Nothing changed." 2
    fi
    home=$(printf '%s\n' "$info" | sed -n 2p)
    owner=$(printf '%s\n' "$info" | sed -n 3p)
    python=$(printf '%s\n' "$info" | sed -n 4p)
    hermes=$(printf '%s\n' "$info" | sed -n 5p)
    [ "$state" = ok ] && [ -n "$owner" ] || fail "Couldn't read the profile's folder in the container $container. Nothing changed."
    [ -n "$python" ] || fail "The container $container has no Python for the setup to run with. Nothing changed." 2

    # Message Link's own container, when it runs on this computer too: on a Docker network it shares with Hermes's,
    # Hermes reaches it by its container name. The page can't know that name; Docker does.
    pin=$2
    shift 2
    first=
    network=
    server=$(printf '%s\n' "$list" | awk -F '\t' 'NF == 2 && tolower($2) ~ /messages-server/ { print $1; exit }')
    if [ -n "$server" ]; then
        networks='{{range $name, $value := .NetworkSettings.Networks}}{{$name}} {{end}}'
        theirs=$(docker inspect -f "$networks" "$container" 2>/dev/null) || theirs=
        ours=$(docker inspect -f "$networks" "$server" 2>/dev/null) || ours=
        for name in $theirs; do
            case $name in bridge | host | none) continue ;; esac
            case " $ours " in *" $name "*) network=$name ;; esac
        done
        if [ -n "$network" ] && [ -n "$port" ]; then
            first=$server:$port
        fi
    fi
    printf 'Hermes container: %s\n' "$container"
    # The addresses to try: the one found here first, then the page's.
    if [ -n "$first" ]; then set -- "$first" "$@"; fi
    # Told when Message Link's container shares no network with Hermes's, so a setup no address works for can say so.
    apart=
    if [ -n "$server" ] && [ -z "$network" ]; then apart=--no-shared-network; fi
    # Adding an assistant: where Message Link's server might reach Hermes's API server. On a network both containers share, by Hermes's container name
    # (Docker's own names resolve there) at the port inside it; otherwise at the port Docker publishes it on, at this computer's address, which the server
    # sees the setup come from.
    reach=
    if [ -n "$add" ]; then
        api=$(printf '%s\n' "$info" | sed -n 6p)
        case $api in '' | *[!0-9]*) api=8642 ;; esac
        if [ -n "$network" ]; then
            reach="--reach $container:$api"
        else
            published=$(docker port "$container" "$api/tcp" 2>/dev/null | sed -n '1s/.*://p')
            # 0: Docker publishes it nowhere, so the installer names that fix if the server can't reach Hermes.
            case $published in '' | *[!0-9]*) reach="--published 0" ;; *) reach="--published $published" ;; esac
        fi
    fi
    python_part | MLSETUP=$code docker exec -i -u "$owner" -e MLSETUP -e HERMES_HOME="$home" "$container" \
        "$python" -I - --hermes "$hermes" $apart $add $reach "$profile" "$pin" "$@"
    exit $?
fi

# ---- Hermes installed here ----

hermes=$(command -v hermes 2>/dev/null) || hermes=
if [ -z "$hermes" ]; then
    fail "No Hermes found here: no Hermes container in Docker and no hermes command. Run this on the computer that runs Hermes." 2
fi
python=$(pick_python "$hermes") || python=
[ -n "$python" ] || fail "There's no Python here for the setup to run with. Nothing changed." 2
python_part | MLSETUP=$code "$python" -I - --hermes "$hermes" $add "$@"
exit $?
#__ML_SETUP_PY__
