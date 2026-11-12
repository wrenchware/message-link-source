# Message Link Server's one image (SWITCHBOARD.md stage 11; 4.3, 5.4): the server, its helpers and official Google Chrome, in which it shows every sign-in and which it keeps running, signed in
# by hand, to renew Google's sign-in the way the PC's own browser profile does. Debian with Google's own Chrome package, because Ubuntu offers browsers only as snaps, which a container cannot
# run; .NET is published self-contained into it. Build from the repository root (install-unraid.sh does, with the current time as CHROME_REFRESH):
#
#   docker build -f docker/messages-server/Dockerfile --build-arg CHROME_REFRESH=$(date -u +%Y%m%dT%H%M%SZ) -t messages-server:local .
#
# Chrome and Google stay off until the container is given MESSAGES_GOOGLE=1 and MESSAGES_GOOGLE_CHROME=1 (the install script, compose and the Unraid templates give both), and it needs
# MESSAGES_RESTORE=1 (only the restore starts the renewal). Under Docker's default seccomp profile, which refuses user namespaces to a container with every capability dropped,
# Chrome also needs MESSAGES_CHROMIUM_NO_SANDBOX=1, unless the container is given chrome-seccomp.json or seccomp=unconfined (see the ENV below). Chrome always runs headed on its own Xvfb. Written for the classic builder as well as BuildKit.

# ---- Google Messages helper (Go). The version is experiments/GoogleMessages/helper/go.mod's "go" line. -------------------------------------------
FROM golang:1.27.4-bookworm@sha256:4fa7c968ba93f1635670522dbb01a2c78150443e1385746924566079f36570ea AS gmessages-helper
ENV CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
# go.mod replaces go.mau.fi/mautrix-gmessages with ../../../third_party/mautrix-gmessages, so the tree keeps the repo's layout.
COPY third_party/mautrix-gmessages/ /source/third_party/mautrix-gmessages/
WORKDIR /source/experiments/GoogleMessages/helper
COPY experiments/GoogleMessages/helper/go.mod experiments/GoogleMessages/helper/go.sum ./
RUN go mod download && go mod verify
COPY experiments/GoogleMessages/helper/ ./
# The same tests experiments/GoogleMessages/Build.ps1 runs before it builds.
RUN go test ./... && go build -trimpath -o /out/gmessages-helper .

# ---- Messenger helper (Go), built from its own source. ----------------------------------------------------------------------------------------
FROM golang:1.27.4-bookworm@sha256:4fa7c968ba93f1635670522dbb01a2c78150443e1385746924566079f36570ea AS messenger-helper
ENV CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
COPY third_party/mautrix-meta/ /source/third_party/mautrix-meta/
WORKDIR /source/experiments/Messenger/helper
COPY experiments/Messenger/helper/go.mod experiments/Messenger/helper/go.sum ./
RUN go mod download && go mod verify
COPY experiments/Messenger/helper/ ./
RUN go test ./... go.mau.fi/mautrix-meta/pkg/messagix \
 && go build -trimpath -o /out/messenger-helper .

# ---- Instagram helper (Go), built from its own source. ----------------------------------------------------------------------------------------
FROM golang:1.27.4-bookworm@sha256:4fa7c968ba93f1635670522dbb01a2c78150443e1385746924566079f36570ea AS instagram-helper
ENV CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
COPY third_party/mautrix-meta/ /source/third_party/mautrix-meta/
WORKDIR /source/experiments/Instagram/helper
COPY experiments/Instagram/helper/go.mod experiments/Instagram/helper/go.sum ./
RUN go mod download && go mod verify
COPY experiments/Instagram/helper/ ./
RUN go test ./... \
 && go build -trimpath -o /out/instagram-helper .

# ---- Google Voice helper (Go). It has no replace directive: its one library (go.mau.fi/mautrix-gvoice) is fetched and verified from go.sum, like the others' dependencies. ---------
# Started only beside the server's own Chrome (SWITCHBOARD.md 5.4, Google Voice on the server), which signs Voice in and renews it; one process per Voice account.
FROM golang:1.27.4-bookworm@sha256:4fa7c968ba93f1635670522dbb01a2c78150443e1385746924566079f36570ea AS gvoice-helper
ENV CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
WORKDIR /source/experiments/GoogleVoice/helper
COPY experiments/GoogleVoice/helper/go.mod experiments/GoogleVoice/helper/go.sum ./
RUN go mod download && go mod verify
COPY experiments/GoogleVoice/helper/ ./
# The same tests experiments/GoogleVoice/Build.ps1 runs before it builds.
RUN go test ./... && go build -trimpath -o /out/gvoice-helper .

# ---- Tailnet door (Go): the server's own node on the owner's tailnet (SWITCHBOARD.md 4.7, TailnetDoor.cs), run only with MESSAGES_TAILNET=1. -------
# Tailscale's tsnet: userspace networking, so the container needs no capability or tun device for it. The licence files of every module it links (and
# Go's own) are copied beside it, as the app's THIRD_PARTY_NOTICES.md lists only what the PC installer ships; a module without one stops the build.
FROM golang:1.27.4-bookworm@sha256:4fa7c968ba93f1635670522dbb01a2c78150443e1385746924566079f36570ea AS tailnet-door
ENV CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
WORKDIR /source/experiments/Tailnet/door
COPY experiments/Tailnet/door/go.mod experiments/Tailnet/door/go.sum ./
RUN go mod download && go mod verify
COPY experiments/Tailnet/door/ ./
# The tests experiments/Tailnet/Build-Linux.ps1 compiles; here they run.
RUN go test ./... \
 && go build -trimpath -ldflags="-s -w" -o /out/messages-tailnet . \
 && mkdir -p /out/licenses/go && cp "$(go env GOROOT)/LICENSE" /out/licenses/go/ \
 && go list -deps -f '{{with .Module}}{{.Path}}@{{.Version}} {{.Dir}}{{end}}' . > /tmp/modules \
 && sort -u /tmp/modules | while read -r module dir; do \
      if [ -z "$dir" ] || [ "${module%@*}" = "nativebeeper.local/tailnet-door" ]; then continue; fi; \
      found=0; \
      for file in "$dir"/LICENSE* "$dir"/LICENCE* "$dir"/COPYING* "$dir"/NOTICE* "$dir"/PATENTS*; do \
        if [ -f "$file" ]; then mkdir -p "/out/licenses/$module" && cp "$file" "/out/licenses/$module/" || exit 1; found=1; fi; \
      done; \
      if [ "$found" != 1 ]; then echo "Tailnet door: no licence file in $module" >&2; exit 1; fi; \
    done

# ---- Server (.NET), self-contained: the Debian base below has no .NET of its own. ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS server
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets ./
COPY src/ src/
COPY experiments/ experiments/
# Locked: the project lists its Linux runtimes (RuntimeIdentifiers), which is what lets a self-contained restore stay
# inside its lock file on a Linux SDK.
RUN dotnet restore src/NativeBeeper.Server.Linux/NativeBeeper.Server.Linux.csproj --locked-mode -p:SelfContained=true
# SMBLibrary (LGPL-3.0) ships with its licence texts, replacement notice and exact source: NativeBeeper.Host.csproj
# copies them from here to licenses/SMBLibrary. A wildcard that matches nothing is no error, so the publish is checked below.
COPY third_party/SMBLibrary/ third_party/SMBLibrary/
RUN dotnet publish src/NativeBeeper.Server.Linux/NativeBeeper.Server.Linux.csproj -c Release --no-restore \
      -r linux-x64 -p:SelfContained=true -o /out \
 && if [ ! -x /out/messages-server ]; then \
      echo "Message Link Server build: the publish has no messages-server executable (expected AssemblyName messages-server, linux-x64)." >&2; \
      exit 1; \
    fi \
 && for file in SMBLibrary.dll licenses/SMBLibrary/NOTICE.txt licenses/SMBLibrary/LGPL-3.0.txt licenses/SMBLibrary/GPL-3.0.txt \
      licenses/SMBLibrary/source-1.5.8.1.zip; do \
      if [ ! -s "/out/$file" ]; then echo "Message Link Server build: the publish has no $file." >&2; exit 1; fi; \
    done
# mlink (SWITCHBOARD.md stage 13), Message Link in a terminal, for "docker exec -it messages-server mlink". Restored and published as the server is, from
# its plain net10.0 build (its project also targets Windows' framework, for the PC installer, which is why it may restore Windows targeting here), and
# laid into /out beside the server: a file both publishes make must be byte-identical or the build stops naming it, and only mlink's own files are
# copied. One exception, as the PC installer has it: the server's ASP.NET Core runtime pack carries Microsoft's own build of
# Microsoft.Extensions.DependencyInjection.Abstractions.dll, which mlink takes from NuGet. Both deps.json files must give it the same assembly and file
# version, and the server's copy is kept.
RUN set -eu; \
    dotnet restore src/NativeBeeper.Terminal/NativeBeeper.Terminal.csproj --locked-mode -p:SelfContained=true; \
    dotnet publish src/NativeBeeper.Terminal/NativeBeeper.Terminal.csproj -c Release -f net10.0 --no-restore -r linux-x64 -p:SelfContained=true -o /out-mlink; \
    if [ ! -x /out-mlink/mlink ]; then echo "mlink build: the publish has no mlink executable (expected AssemblyName mlink, linux-x64)." >&2; exit 1; fi; \
    versions() { grep -A2 'Microsoft.Extensions.DependencyInjection.Abstractions.dll": {' "$1" | grep -o '"[a-zA-Z]*Version": "[^"]*"' | tr '\n' ' '; }; \
    server_di=$(versions /out/messages-server.deps.json); mlink_di=$(versions /out-mlink/mlink.deps.json); \
    cd /out-mlink; \
    find . -type f | sort | while read -r file; do \
      file=${file#./}; \
      if [ ! -e "/out/$file" ]; then \
        case "$file" in mlink|mlink.*) cp -p "$file" "/out/$file" ;; *) echo "mlink build: $file is in mlink's publish but not the server's." >&2; exit 1 ;; esac; \
      elif ! cmp -s "$file" "/out/$file"; then \
        if [ "$file" = Microsoft.Extensions.DependencyInjection.Abstractions.dll ] && [ -n "$server_di" ] && [ "$server_di" = "$mlink_di" ]; then continue; fi; \
        echo "mlink build: $file differs from the server's copy." >&2; exit 1; \
      fi; \
    done; \
    cd /; rm -rf /out-mlink; \
    [ -x /out/mlink ] || { echo "mlink build: mlink was not copied beside the server." >&2; exit 1; }

# ---- Runtime --------------------------------------------------------------------------------------------------------------------------------------
FROM debian:13.7-slim@sha256:a29215f6a35e51e22adffa17f89e9d2ef06214e64a2bad10d765c46aea49f11f AS runtime
LABEL org.opencontainers.image.title="Message Link Server (Google)" \
      org.opencontainers.image.description="Message Link Server with Google Messages: the Message Link backends on a home server, reached by paired devices over TLS."

# What .NET needs on Debian (ICU, OpenSSL, time zones, certificates), tini, and xvfb: the virtual X display Chrome draws on (SWITCHBOARD.md 5.4).
# xvfb brings xauth, xkb-data and x11-xkb-utils (xkbcomp, which gives the display its keyboard) and a few X libraries. libxcb-xtest0 is the XTEST
# binding of libxcb, through which the server types and clicks on that display (real X input, so nothing is attached to Chrome); libxcb itself comes with Chrome.
# With --no-install-recommends, and no window manager. The upgrade brings Debian's security fixes as of each build: the digest above
# freezes the base, and the scheduled build (no-cache-filters: runtime) re-runs this stage, so OpenSSL and libc fixes arrive without a pin bump.
RUN apt-get update \
 && apt-get upgrade -y --no-install-recommends \
 && apt-get install -y --no-install-recommends ca-certificates tzdata libicu76 libssl3t64 tini xvfb libxcb-xtest0 \
 && rm -rf /var/lib/apt/lists/*

# Official Google Chrome (stable), from Google's own apt repository, in place of Debian's chromium: the server runs it persistently, signed in by hand, and lets it renew
# Google's sign-in itself (SWITCHBOARD.md 5.4, chosen 2026-10-10). The repository's key is fetched over HTTPS and only the keys whose fingerprints are listed in
# CHROME_SIGNER_FPRS are kept (the build fails if none of them is in the file); the repository line is pinned to that keyring and to amd64.
# CHROME_REFRESH is a build argument install-unraid.sh sets to the build time: it is named in this RUN, so a new value is a new layer and the newest stable Chrome is fetched on
# every build, never reused from Docker's cache. It is also written into the image as MESSAGES_CHROME_BUILT, which the server counts Chrome's age from (it says so when
# it is more than 21 days, and the client tells the person to reinstall). curl and gnupg are removed again after the key is made.
ARG CHROME_REFRESH=unset
ARG CHROME_SIGNER_FPRS="EB4C1BFD4F042F6DDDCCEC917721F63BD38B4796"
RUN set -eu; \
    echo "Chrome refresh: $CHROME_REFRESH"; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl gnupg; \
    curl -fsSL --proto '=https' --tlsv1.2 https://dl.google.com/linux/linux_signing_key.pub -o /tmp/google-signing.pub; \
    gpg --batch --no-default-keyring --keyring /tmp/google-all.gpg --import /tmp/google-signing.pub; \
    : > /tmp/google-chrome.gpg; \
    for fpr in $CHROME_SIGNER_FPRS; do gpg --batch --no-default-keyring --keyring /tmp/google-all.gpg --export "$fpr" >> /tmp/google-chrome.gpg 2>/dev/null || true; done; \
    [ -s /tmp/google-chrome.gpg ] || { echo "Google's signing key file holds none of the pinned keys ($CHROME_SIGNER_FPRS)." >&2; exit 1; }; \
    install -m 0644 /tmp/google-chrome.gpg /usr/share/keyrings/google-chrome.gpg; \
    echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] https://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list; \
    mkdir -p /etc/default; echo 'repo_add_once="false"' > /etc/default/google-chrome; \
    apt-get update; \
    apt-get install -y --no-install-recommends google-chrome-stable; \
    apt-get purge -y --auto-remove curl gnupg; \
    rm -rf /var/lib/apt/lists/* /tmp/google-* ; \
    google-chrome-stable --version
# What the browser may load and keep, set where the browser reads it and the server cannot be talked out of (docker/messages-server/chrome-policy.json): every address blocked but a short list
# (the account sign-in, Messages for web's configuration page by path, and the few static hosts they need; Facebook's and Instagram's own sites for their sign-ins, with Instagram's inbox
# blocked by path), no saved passwords or autofill, no Chrome account or sync, no developer tools or remote
# debugging, no downloads, file dialogs or extensions. URLBlocklist/URLAllowlist govern NAVIGATIONS (pages and frames) by the most specific match; what those pages then load follows what they ask for,
# which is why the list names hosts and not google.com. The server reads the path from nowhere else and will not start Chrome without the file.
COPY docker/messages-server/chrome-policy.json /etc/opt/chrome/policies/managed/messages-server.json
RUN chmod 0644 /etc/opt/chrome/policies/managed/messages-server.json

ARG PUID=2787
ARG PGID=2787
RUN set -e; \
    getent group "$PGID" >/dev/null || groupadd --system --gid "$PGID" messages-server; \
    getent passwd "$PUID" >/dev/null || useradd --system --no-create-home --home-dir /data --shell /usr/sbin/nologin \
      --uid "$PUID" --gid "$PGID" messages-server; \
    mkdir -p /data /keys && chown "$PUID:$PGID" /data /keys && chmod 700 /data /keys

WORKDIR /app
COPY --from=server /out/ /app/
# mlink on every shell's PATH in the container, for the container's own user: docker exec -it messages-server mlink.
RUN ln -s /app/mlink /usr/local/bin/mlink
# The registry installer copies these from a stopped container, without a source archive or starting the server.
COPY assets/app-icon.png /app/install/assets/app-icon.png
COPY docker/messages-server/chrome-seccomp.json /app/install/docker/messages-server/chrome-seccomp.json
COPY --from=messenger-helper /out/messenger-helper /app/messenger-helper
COPY --from=instagram-helper /out/instagram-helper /app/instagram-helper
COPY --from=gmessages-helper /out/gmessages-helper /app/gmessages-helper
COPY --from=gvoice-helper /out/gvoice-helper /app/gvoice-helper
# The tailnet door (MESSAGES_TAILNET=1) and the licences of what it links.
COPY --from=tailnet-door /out/messages-tailnet /app/messages-tailnet
COPY --from=tailnet-door /out/licenses/ /app/licenses/tailnet/
COPY THIRD_PARTY_NOTICES.md /app/licenses/THIRD_PARTY_NOTICES.md
COPY experiments/Messenger/helper/UPSTREAM_LICENSE.txt /app/licenses/MESSENGER_LICENSE.txt
COPY third_party/mautrix-meta/LICENSE /app/licenses/MAUTRIX_META_LICENSE.txt
COPY third_party/mautrix-meta/LICENSE.exceptions /app/licenses/MAUTRIX_META_LICENSE_EXCEPTIONS.txt
COPY experiments/GoogleMessages/helper/UPSTREAM_LICENSE.txt /app/licenses/GOOGLE_MESSAGES_LICENSE.txt
COPY third_party/mautrix-gmessages/LICENSE /app/licenses/MAUTRIX_GMESSAGES_LICENSE.txt
COPY third_party/mautrix-gmessages/LICENSE.exceptions /app/licenses/MAUTRIX_GMESSAGES_LICENSE_EXCEPTIONS.txt
COPY experiments/GoogleVoice/helper/UPSTREAM_LICENSE.txt /app/licenses/GOOGLE_VOICE_LICENSE.txt
COPY experiments/GoogleVoice/helper/UPSTREAM_LICENSE_EXCEPTIONS.txt /app/licenses/GOOGLE_VOICE_LICENSE_EXCEPTIONS.txt
# libSkiaSharp.so (Google Voice's photo re-encoder) links Skia, libjpeg-turbo, libpng, libwebp, FreeType and others in; their notices come with the
# package, from the restore above.
COPY --from=server /root/.nuget/packages/skiasharp.nativeassets.linux.nodependencies/*/THIRD-PARTY-NOTICES.txt /app/licenses/SKIASHARP_NATIVE_NOTICES.txt

# The server's settings, and the browser's: MESSAGES_CHROME (Google Chrome) and MESSAGES_CHROME_BUILT (this image's build time, for its age).
# MESSAGES_GOOGLE=1 and MESSAGES_GOOGLE_CHROME=1 are given by whatever starts the container, not baked in (Google Messages stays off in this image until the container says so).
# Not set here, on purpose: MESSAGES_CHROMIUM_NO_SANDBOX. Chromium's own sandbox needs user namespaces (no-new-privileges rules out its setuid helper),
# which Docker's default seccomp profile refuses a container with every capability dropped. Such a container is given chrome-seccomp.json
# (install-unraid.sh --chrome-sandbox) or seccomp=unconfined (the Community Applications template), or the run passes
# MESSAGES_CHROMIUM_NO_SANDBOX=1 itself (the log then says sandbox=off). Without its sandbox, a page's code that broke out of the renderer could read
# what the server's account can, the keys volume included; what limits that is described in docs/research/google-linux-renewal.md. Choosing it
# is up to whoever runs the container, not baked into the image.
ENV APP_UID=$PUID \
    MESSAGES_DATA=/data \
    MESSAGES_KEYS=/keys \
    HOME=/data \
    ASPNETCORE_HTTP_PORTS="" \
    DOTNET_EnableDiagnostics=0 \
    MESSAGES_CHROME=/usr/bin/google-chrome-stable \
    MESSAGES_CHROME_BUILT=$CHROME_REFRESH
# Where this image came from, for host.status: the channel (ghcr:google or local), the source commit and the build time.
ARG MESSAGES_CHANNEL=local
ARG MESSAGES_REVISION=
ENV MESSAGES_CHANNEL=$MESSAGES_CHANNEL \
    MESSAGES_REVISION=$MESSAGES_REVISION \
    MESSAGES_IMAGE_BUILT=$CHROME_REFRESH
VOLUME ["/data", "/keys"]
EXPOSE 8787/tcp
USER $PUID:$PGID

HEALTHCHECK --interval=60s --timeout=5s --start-period=120s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8787"]

STOPSIGNAL SIGTERM
# tini as the first process, so a container started without --init (or compose's init: true) still forwards SIGTERM to the server and reaps
# whatever a browser leaves behind. -s makes it a subreaper too, so it also does that when docker's own init is PID 1 and tini is not.
ENTRYPOINT ["/usr/bin/tini", "-s", "--", "/app/messages-server"]
