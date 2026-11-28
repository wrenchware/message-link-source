#!/bin/bash
# Removes what install-unraid.sh put on Unraid. With no options it stops and removes the container and its image and
# keeps the server's data and keys, so a later install picks up where it left off. The rest only when asked:
#
#   bash /mnt/user/tmp/messages-server/uninstall-unraid.sh                 # container and image only
#   bash /mnt/user/tmp/messages-server/uninstall-unraid.sh --all           # everything below as well
#
#   --keys         the Docker volume messages-server-keys (the keys, the certificate and the Messenger device and
#                  history): the data folder is useless without it, and every device must pair again
#   --data         the data folder (default /mnt/user/appdata/messages-server)
#   --bundle       the source bundle and these scripts in the folder this script runs from
#   --build-cache  Docker's build cache and the .NET and Go build images the image was built from
#   --all          all of the above
#
# Before removing the keys, sign out of Messenger in the app that uses this server (Settings > Accounts), so Facebook
# forgets the server's device; removing files does not tell Facebook anything.
set -euo pipefail

name="messages-server"
image="messages-server:local"
keys="messages-server-keys"
appdata="${MESSAGES_APPDATA:-/mnt/user/appdata/messages-server}"
here="$(cd "$(dirname "$0")" && pwd)"
want_keys=0; want_data=0; want_bundle=0; want_cache=0
for arg in "$@"; do
    case "$arg" in
        --keys) want_keys=1 ;;
        --data) want_data=1 ;;
        --bundle) want_bundle=1 ;;
        --build-cache) want_cache=1 ;;
        --all) want_keys=1; want_data=1; want_bundle=1; want_cache=1 ;;
        *) echo "Unknown option: $arg" >&2; exit 1 ;;
    esac
done

confirm() {
    printf '%s Type "delete" to go on: ' "$1"
    read -r answer
    [ "$answer" = "delete" ]
}

installed_image="$(docker inspect -f '{{.Config.Image}}' "$name" 2>/dev/null || true)"
if docker inspect "$name" >/dev/null 2>&1; then
    echo "Stopping $name (up to 60 s, so anything sending finishes)..."
    docker stop -t 60 "$name" >/dev/null || true
    docker rm "$name" >/dev/null
fi
# The icon install-unraid.sh put in Unraid's icon folder for the Docker page, and the page's own copy of it.
rm -f "/var/lib/docker/unraid/images/$name-icon.png" "/usr/local/emhttp/state/plugins/dynamix.docker.manager/images/$name-icon.png"
# The saved registry template, public sandbox profile and kept icon, never Docker's login (other containers may use it).
rm -f -- "/boot/config/plugins/dockerMan/templates-user/my-messages-server.xml" "/boot/config/plugins/dockerMan/messages-server/chrome-seccomp.json" \
    "/boot/config/plugins/dockerMan/messages-server/icon.png"
rmdir /boot/config/plugins/dockerMan/messages-server 2>/dev/null || true
registry_images=()
case "$installed_image" in ghcr.io/*/messages-server:*) registry_images=("$installed_image") ;; esac
for built in "$image" "$image-google" ${registry_images[@]+"${registry_images[@]}"}; do
    if docker image inspect "$built" >/dev/null 2>&1; then docker image rm "$built" >/dev/null && echo "Removed the image $built."; fi
done

if [ "$want_keys" = 1 ] && docker volume inspect "$keys" >/dev/null 2>&1; then
    if confirm "This removes the keys volume $keys: every device must pair again and the data folder becomes unreadable."; then
        docker volume rm "$keys" >/dev/null && echo "Removed the volume $keys."
    else echo "Kept $keys."; fi
fi
if [ "$want_data" = 1 ] && [ -d "$appdata" ]; then
    if confirm "This removes the data folder $appdata."; then
        rm -rf -- "$appdata" && echo "Removed $appdata."
    else echo "Kept $appdata."; fi
fi
if [ "$want_cache" = 1 ]; then
    # Only what nothing uses: the build cache and untagged layers. The .NET and Go images the build started from stay,
    # since another container may use the same ones; remove them by hand if not (docker image ls).
    docker builder prune -f >/dev/null 2>&1 || true
    docker image prune -f >/dev/null 2>&1 || true
    echo "Pruned the build cache and unused layers. The build's base images are listed by: docker image ls"
fi
if [ "$want_bundle" = 1 ]; then
    rm -f -- "$here/messages-source.tar.gz" "$here/install.env" "$here/install-unraid.sh"
    echo "Removed the bundle from $here. Remove this script and the folder by hand: rm -rf \"$here\""
fi
echo "Done."
