#!/usr/bin/env bash
# The Linux package of one published server image, for messages-server-promote.yml (docs/release.md, "Server image channels";
# chosen 2026-11-12). Writes into --out:
#
#   messages-server-<version>-linux-x64.tar.gz and its .sha256
#   messages-server-linux-x64.tar.gz and its .sha256: the same package under the name a release's "latest" address gives,
#     which install-proxmox.sh and the nightly update (update.sh) fetch
#   install-proxmox.sh, from the same commit
#
# The programs are the image's own /app, not a rebuild, so a stable package holds exactly what ran as that nightly: the
# server published self-contained, mlink, the four helpers, the tailnet door and the licences. The image's install folder
# (what install-unraid.sh copies out of a stopped container) is left out. The install files come from a checkout of the
# image's commit. The archive is laid out as tools/Build-ServerTarball.ps1 lays it out: one top folder, every entry
# root:root, the programs and scripts 0755 and the rest 0644, one modification time (the commit's), name order, and gzip
# without a timestamp, so the same image and commit always give the same bytes.
#
#   package-linux.sh --image ghcr.io/<owner>/messages-server@sha256:<64 hex> --commit <40 hex> \
#                    --time <the commit's time, Unix seconds> --source <checkout of that commit> --out <new folder>
#
# The image is public: it is pulled with no login, as a server pulls it.
set -euo pipefail

fail() { echo "package-linux: $*" >&2; exit 1; }

image="" commit="" when="" source="" out=""
while [ $# -gt 0 ]; do
    case "$1" in
        --image|--commit|--time|--source|--out)
            [ $# -ge 2 ] || fail "$1 needs a value."
            case "$1" in
                --image) image=$2 ;;
                --commit) commit=$2 ;;
                --time) when=$2 ;;
                --source) source=$2 ;;
                --out) out=$2 ;;
            esac
            shift 2 ;;
        *) fail "unknown option $1." ;;
    esac
done

[[ "$image" =~ ^[a-z0-9][a-z0-9./_-]*@sha256:[0-9a-f]{64}$ ]] || fail "--image must name the image by its digest (<name>@sha256:<64 hex>)."
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] || fail "--commit must be the image's commit (40 hex)."
[[ "$when" =~ ^[0-9]{1,12}$ ]] || fail "--time must be the commit's time in Unix seconds."
[ -n "$source" ] && [ -d "$source" ] || fail "--source must be a checkout of the image's commit."
[ -n "$out" ] || fail "--out is needed."
if [ -e "$out" ] && [ -n "$(ls -A "$out")" ]; then fail "$out is not empty."; fi

project="$source/src/NativeBeeper.App/NativeBeeper.App.csproj"
[ -f "$project" ] || fail "the checkout has no src/NativeBeeper.App/NativeBeeper.App.csproj."
version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$project" | head -n 1 | tr -d ' \r')
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "the app version at $commit is not three numbers."
name="messages-server-$version-linux-x64"
# The address scripts and install-proxmox.sh accept only this form.
[[ "$name" =~ ^messages-server-[0-9A-Za-z.+-]+-linux-x64$ ]] || fail "$name is not a package name install-proxmox.sh accepts."
proxmox="$source/docker/messages-server/proxmox/install-proxmox.sh"
[ -f "$proxmox" ] || fail "the checkout has no docker/messages-server/proxmox/install-proxmox.sh."

# The programs and scripts; nothing else is executable (as in Build-ServerTarball.ps1).
executable=(messages-server mlink messenger-helper instagram-helper gmessages-helper gvoice-helper messages-tailnet install.sh uninstall.sh update.sh createdump)
# What every package must hold: install-proxmox.sh requires install.sh, messages-server, messages-tailnet and gmessages-helper.
required=(messages-server mlink messenger-helper instagram-helper gmessages-helper gvoice-helper messages-tailnet
          install.sh uninstall.sh update.sh messages-server.service messages-server-update.service messages-server-update.timer
          google.conf README.md chrome-policy.json licenses/THIRD_PARTY_NOTICES.md)
install_files=(install.sh uninstall.sh update.sh messages-server.service messages-server-update.service messages-server-update.timer google.conf README.md)

work=$(mktemp -d)
container=""
cleanup() {
    if [ -n "$container" ]; then docker rm -f -v "$container" >/dev/null 2>&1 || true; fi
    rm -rf "$work"
}
trap cleanup EXIT
stage="$work/stage/$name"
mkdir -p "$stage"

# ---- The image's /app, from linux/amd64 of the index, without starting it. ----
docker pull --quiet --platform linux/amd64 "$image" >/dev/null
container=$(docker create --platform linux/amd64 "$image")
[ -n "$container" ] || fail "docker create gave no container."
revision=$(docker container inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$container")
[ "$revision" = "$commit" ] || fail "the image was built from ${revision:-an unnamed commit}, not $commit."
docker cp "$container:/app/." "$stage"
docker rm -f -v "$container" >/dev/null
container=""

rm -rf "$stage/install"
odd=$(find "$stage" ! -type f ! -type d | head -n 5)
[ -z "$odd" ] || fail "the image's /app holds something other than files and folders: ${odd//$stage\//}"
for file in messages-server mlink messenger-helper instagram-helper gmessages-helper gvoice-helper messages-tailnet licenses/THIRD_PARTY_NOTICES.md; do
    [ -f "$stage/$file" ] || fail "the image's /app has no $file."
done
[ -d "$stage/licenses/tailnet" ] || fail "the image's /app has no licenses/tailnet."

# ---- Install, uninstall, the update, the units, the README and Chrome's policy, from the commit, with Unix line endings: sh and
# systemd refuse a CR at the end of a line. Never over a file of the image's. ----
for file in "${install_files[@]}" chrome-policy.json; do
    if [ "$file" = chrome-policy.json ]; then from="$source/docker/messages-server/$file"; else from="$source/docker/messages-server/systemd/$file"; fi
    [ -f "$from" ] || fail "the checkout has no ${from#"$source"/}."
    [ ! -e "$stage/$file" ] || fail "the image's /app already holds $file."
    sed -e '1s/^\xEF\xBB\xBF//' -e 's/\r$//' "$from" > "$stage/$file"
done
for file in "${required[@]}"; do
    [ -f "$stage/$file" ] || fail "the package would have no $file."
done

find "$stage" -type d -exec chmod 0755 {} +
find "$stage" -type f -exec chmod 0644 {} +
for file in "${executable[@]}"; do
    if [ -f "$stage/$file" ]; then chmod 0755 "$stage/$file"; fi
done

# ---- The archive: name order (bytes), root:root, the commit's time, no access or change times, no process id in long-name headers. ----
mkdir -p "$out"
archive="$out/$name.tar.gz"
(cd "$work/stage" && find "$name" -print0 | LC_ALL=C sort -z) > "$work/list"
LC_ALL=C tar --create --format=pax --no-recursion --null -C "$work/stage" --files-from="$work/list" \
    --owner=root:0 --group=root:0 --mtime="@$when" \
    --pax-option='exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime' \
    | gzip -9 -n > "$archive"

# ---- What install-proxmox.sh, update.sh and install.sh rely on, read back from the archive itself. ----
python3 -I - "$archive" "$name" "$when" "${executable[*]}" "${required[*]}" <<'PY'
import sys
import tarfile

archive, name, when = sys.argv[1], sys.argv[2], int(sys.argv[3])
executable, required = set(sys.argv[4].split()), set(sys.argv[5].split())
with open(archive, 'rb') as file:
    header = file.read(10)
if header[:2] != b'\x1f\x8b' or header[4:8] != b'\0\0\0\0' or header[3] & 0x08:
    raise SystemExit('package-linux: the archive is not gzip without a timestamp or a file name.')
problems = []
with tarfile.open(archive, 'r:gz') as tar:
    members = tar.getmembers()
names = [member.name for member in members]
if not members or names[0] != name or not members[0].isdir():
    problems.append('the first entry is not the folder ' + name)
if names != sorted(names, key=lambda text: text.encode()):
    problems.append('the entries are not in name order')
if len(set(names)) != len(names):
    problems.append('an entry appears twice')
for member in members:
    relative = member.name[len(name) + 1:] if member.name.startswith(name + '/') else None
    if member.name != name and relative is None:
        problems.append(member.name + ' is outside ' + name)
        continue
    if not (member.isdir() or member.isfile()):
        problems.append(member.name + ' is neither a file nor a folder')
    if (member.uid, member.gid, member.uname, member.gname) != (0, 0, 'root', 'root'):
        problems.append(member.name + ' is not root:root')
    if member.mtime != when:
        problems.append(member.name + ' does not carry the commit time')
    want = 0o755 if member.isdir() or relative in executable else 0o644
    if member.mode != want:
        problems.append(member.name + ' is ' + oct(member.mode) + ', not ' + oct(want))
missing = sorted(file for file in required if name + '/' + file not in names)
if missing:
    problems.append('missing ' + ', '.join(missing))
if any(entry.startswith(name + '/install/') or entry == name + '/install' for entry in names):
    problems.append("the image's install folder is in the package")
if problems:
    raise SystemExit('package-linux: ' + '; '.join(problems[:10]) + '.')
print('package-linux: ' + str(len(members)) + ' entries checked.')
PY

hash=$(sha256sum "$archive" | cut -d ' ' -f 1)
printf '%s  %s\n' "$hash" "$name.tar.gz" > "$archive.sha256"
cp "$archive" "$out/messages-server-linux-x64.tar.gz"
printf '%s  %s\n' "$hash" "messages-server-linux-x64.tar.gz" > "$out/messages-server-linux-x64.tar.gz.sha256"
sed -e '1s/^\xEF\xBB\xBF//' -e 's/\r$//' "$proxmox" > "$out/install-proxmox.sh"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
    printf 'version=%s\npackage=%s\nsha256=%s\n' "$version" "$name" "$hash" >> "$GITHUB_OUTPUT"
fi
echo "package-linux: $name.tar.gz ($(du -k "$archive" | cut -f 1) KB, sha256 $hash) from $image."
