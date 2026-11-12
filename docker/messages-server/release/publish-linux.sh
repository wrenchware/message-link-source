#!/usr/bin/env bash
# The GitHub release of a promoted server image's Linux package, for messages-server-promote.yml (docs/release.md, "Server
# image channels"; chosen 2026-11-12). The release is on the public source repository, at the commit's src-<12 hex> tag,
# so its "latest" address is the newest promotion's package:
#
#   https://github.com/<repository>/releases/latest/download/messages-server-linux-x64.tar.gz (and .sha256, install-proxmox.sh)
#
#   publish-linux.sh check                                      before anything moves: the token is there and can write releases
#   publish-linux.sh publish --tag src-<12 hex> --version <version> --image <name>@sha256:<64 hex> --assets <folder from package-linux.sh>
#
# Publishing makes the release when there is none, adds any of its files it lacks (a run stopped half way, run again) and marks it
# latest; files it has are never replaced, so a release promoted again, or rolled back to, keeps its bytes and servers comparing
# the .sha256 do not reinstall. The release is marked latest only once it holds every file.
#
# Needs gh, GH_TOKEN (MESSAGES_SOURCE_RELEASE_TOKEN: Contents read and write on that repository only) and SOURCE_REPOSITORY.
set -euo pipefail

fail() { echo "publish-linux: $*" >&2; exit 1; }
say() { echo "publish-linux: $*"; }

repo=${SOURCE_REPOSITORY:-}
[[ "$repo" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "SOURCE_REPOSITORY must be <owner>/<repository>."
token_help="Add a fine-grained token with Contents read and write on $repo as the Actions secret MESSAGES_SOURCE_RELEASE_TOKEN (docs/release.md, \"Server image channels\")."

check() {
    [ -n "${GH_TOKEN:-}" ] || fail "MESSAGES_SOURCE_RELEASE_TOKEN is missing, so the Linux package cannot be published and nothing was moved. $token_help"
    gh api "repos/$repo" --jq .full_name >/dev/null 2>"$work/error" \
        || fail "MESSAGES_SOURCE_RELEASE_TOKEN cannot read $repo (expired, revoked or for another repository: $(tail -n 1 "$work/error")); nothing was moved. $token_help"
    # Only a write shows the token may write: a draft release, never published and seen only by the repository's owners, made and
    # removed again. Its name is not a tag, and a draft makes no tag.
    local id
    id=$(gh api -X POST "repos/$repo/releases" -f tag_name="release-write-check-${GITHUB_RUN_ID:-local}" -f name="Write check" \
         -F draft=true --jq .id 2>"$work/error") \
        || fail "MESSAGES_SOURCE_RELEASE_TOKEN cannot make releases on $repo ($(tail -n 1 "$work/error")); nothing was moved. $token_help"
    [[ "$id" =~ ^[0-9]+$ ]] || fail "the write check's draft release has no id; nothing was moved."
    gh api -X DELETE "repos/$repo/releases/$id" >/dev/null 2>"$work/error" \
        || fail "the write check's draft release $id could not be removed ($(tail -n 1 "$work/error")); remove it on GitHub. Nothing was moved."
    say "MESSAGES_SOURCE_RELEASE_TOKEN can make releases on $repo."
}

publish() {
    local tag="" version="" image="" assets=""
    while [ $# -gt 0 ]; do
        case "$1" in
            --tag|--version|--image|--assets)
                [ $# -ge 2 ] || fail "$1 needs a value."
                case "$1" in --tag) tag=$2 ;; --version) version=$2 ;; --image) image=$2 ;; --assets) assets=$2 ;; esac
                shift 2 ;;
            *) fail "unknown option $1." ;;
        esac
    done
    [[ "$tag" =~ ^src-[0-9a-f]{12}$ ]] || fail "--tag must be src-<12 hex>."
    [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "--version must be three numbers."
    [[ "$image" =~ ^[a-z0-9][a-z0-9./_-]*@sha256:[0-9a-f]{64}$ ]] || fail "--image must name the image by its digest."
    [ -n "$assets" ] && [ -d "$assets" ] || fail "--assets must be package-linux.sh's folder."
    local files=("messages-server-$version-linux-x64.tar.gz" "messages-server-$version-linux-x64.tar.gz.sha256"
                 messages-server-linux-x64.tar.gz messages-server-linux-x64.tar.gz.sha256 install-proxmox.sh)
    local file
    for file in "${files[@]}"; do [ -s "$assets/$file" ] || fail "$assets has no $file."; done

    # The release at the tag, when there is one: its files and GitHub's digest of each.
    if gh api "repos/$repo/releases/tags/$tag" --jq '.assets[] | .name + " " + (.digest // "")' >"$work/existing" 2>"$work/error"; then
        say "the release at $tag exists."
    elif grep -q 'HTTP 404' "$work/error"; then
        local notes="The Linux package of Message Link Server $version (linux-x64), for install-proxmox.sh and the nightly update."$'\n'"Made from $image, promoted to stable."
        gh release create "$tag" --repo "$repo" --verify-tag --title "Message Link Server $version" --notes "$notes" --latest=false >/dev/null
        : > "$work/existing"
        say "made the release at $tag."
    else
        fail "could not read the release at $tag ($(tail -n 1 "$work/error"))."
    fi

    local missing=() name digest have
    for file in "${files[@]}"; do
        if ! awk -v want="$file" '$1 == want { found = 1 } END { exit !found }' "$work/existing"; then missing+=("$file"); fi
    done
    if [ ${#missing[@]} -gt 0 ]; then
        # Files an earlier attempt left must be these same bytes (the package is reproducible), or the release would pair one
        # attempt's package with another's .sha256.
        while read -r name digest; do
            for file in "${files[@]}"; do
                [ "$name" = "$file" ] || continue
                [ -n "$digest" ] || continue
                have="sha256:$(sha256sum "$assets/$file" | cut -d ' ' -f 1)"
                [ "$digest" = "$have" ] || fail "the release at $tag already holds a different $file. Delete the release's files on GitHub and run this again."
            done
        done < "$work/existing"
        # The packages before their .sha256, so a .sha256 never names a file not yet there.
        local ordered=()
        for file in "${missing[@]}"; do case "$file" in *.sha256) ;; *) ordered+=("$assets/$file") ;; esac; done
        for file in "${missing[@]}"; do case "$file" in *.sha256) ordered+=("$assets/$file") ;; esac; done
        gh release upload "$tag" --repo "$repo" "${ordered[@]}"
        say "added ${missing[*]}."
    else
        say "the release holds every file; nothing was added."
    fi

    gh release edit "$tag" --repo "$repo" --latest >/dev/null
    local latest
    latest=$(gh api "repos/$repo/releases/latest" --jq .tag_name)
    [ "$latest" = "$tag" ] || fail "the latest release is $latest, not $tag."
    say "the latest release is $tag: https://github.com/$repo/releases/latest/download/messages-server-linux-x64.tar.gz"
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        echo "- The Linux package $version is the latest release of $repo ($tag)." >> "$GITHUB_STEP_SUMMARY"
    fi
}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
case "${1:-}" in
    check) check ;;
    publish) shift; publish "$@" ;;
    *) fail "give check or publish." ;;
esac
