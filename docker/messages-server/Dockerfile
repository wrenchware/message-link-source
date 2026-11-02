# Messages Server for Linux: builds the server and the Messenger and Instagram helpers from this repository's source, so the machine
# that builds it needs Docker and nothing else. Build from the repository root (the context must be the root, where
# .dockerignore keeps everything but the source out of it):
#
#   docker build -f docker/messages-server/Dockerfile -t messages-server:local .
#
# Written for the classic builder as well as BuildKit (Unraid's docker may have no buildx): no heredocs, no RUN
# --mount, no COPY --chmod or --parents. Base images are pinned by version and digest; to move to a newer patch, change
# the tag and the digest together.

# ---- Messenger helper (Go). The version is experiments/Messenger/helper/go.mod's "go" line. ---------------------------
FROM golang:1.27.3-bookworm@sha256:d9b6a1beb9474adc2a1ce842573639b44b2a0dd49b6669f51d3821baf9ec37c9 AS helper
# Static binary (modernc sqlite needs no cgo), built for the image's platform. GOTOOLCHAIN=local stops go.mod from
# fetching a different toolchain than the pinned image; -mod=readonly refuses a go.sum that does not match.
ENV CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
# go.mod replaces go.mau.fi/mautrix-meta with ../../../third_party/mautrix-meta, so the tree keeps the repo's layout.
COPY third_party/mautrix-meta/ /source/third_party/mautrix-meta/
WORKDIR /source/experiments/Messenger/helper
COPY experiments/Messenger/helper/go.mod experiments/Messenger/helper/go.sum ./
RUN go mod download && go mod verify
COPY experiments/Messenger/helper/ ./
# The same tests experiments/Messenger/Build.ps1 runs before it builds.
RUN go test ./... go.mau.fi/mautrix-meta/pkg/messagix \
 && go build -trimpath -o /out/messenger-helper .

# ---- Instagram helper (Go). The same image and rules as the Messenger helper's; its go.mod's "go" line is the same. ---
# A stage of its own, so each helper's module download is cached apart and the two build side by side under BuildKit.
FROM golang:1.27.3-bookworm@sha256:d9b6a1beb9474adc2a1ce842573639b44b2a0dd49b6669f51d3821baf9ec37c9 AS instagram-helper
ENV CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
# go.mod replaces go.mau.fi/mautrix-meta with the same ../../../third_party/mautrix-meta (pkg/instameow).
COPY third_party/mautrix-meta/ /source/third_party/mautrix-meta/
WORKDIR /source/experiments/Instagram/helper
COPY experiments/Instagram/helper/go.mod experiments/Instagram/helper/go.sum ./
RUN go mod download && go mod verify
COPY experiments/Instagram/helper/ ./
# The same tests experiments/Instagram/Build.ps1 runs before it builds, and on Linux also the parent-death watch's.
RUN go test ./... \
 && go build -trimpath -o /out/instagram-helper .

# ---- Tailnet door (Go): the server's own node on the owner's tailnet (SWITCHBOARD.md 4.7, TailnetDoor.cs), run only with MESSAGES_TAILNET=1. -------
# Tailscale's tsnet: userspace networking, so the container needs no capability or tun device for it. The licence files of every module it links (and
# Go's own) are copied beside it, as the app's THIRD_PARTY_NOTICES.md lists only what the PC installer ships; a module without one stops the build.
FROM golang:1.27.3-bookworm@sha256:d9b6a1beb9474adc2a1ce842573639b44b2a0dd49b6669f51d3821baf9ec37c9 AS tailnet-door
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

# ---- Server (.NET). The SDK is global.json's; the runtime is the runtime image's below (10.0.14), not the SDK's. ---------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS server
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets ./
# Backends compiles source files linked from experiments/*/*.Spike, so both trees are needed.
COPY src/ src/
COPY experiments/ experiments/
RUN for project in src/NativeBeeper.Server.Linux/NativeBeeper.Server.Linux.csproj \
                   src/NativeBeeper.Host.Network/NativeBeeper.Host.Network.csproj; do \
      if [ ! -f "$project" ]; then \
        echo "Messages Server build: $project is missing from this checkout. Build from a commit that has the Linux server host and the network transport." >&2; \
        exit 1; \
      fi; \
    done; \
    if [ ! -f src/NativeBeeper.Server.Linux/packages.lock.json ]; then \
      echo "Messages Server build: src/NativeBeeper.Server.Linux/packages.lock.json is missing. Restore is locked; commit the lock file." >&2; \
      exit 1; \
    fi
# Locked restore: a dependency graph that differs from the committed lock files fails here rather than being rewritten.
# NuGet audit stays on (Directory.Build.props), so a known vulnerable package fails the build too.
# Framework-dependent: the runtime image already carries exactly the ASP.NET Core shared framework the server needs,
# in layers Microsoft patches; self-contained would copy the same ~70 MB of runtime into the server's own layer.
# The project lists its Linux runtimes (RuntimeIdentifiers) for the lock file and the publish picks one. Restore takes
# no -r: it would flow into every referenced project, whose lock files name no runtime, and locked mode refuses that.
RUN dotnet restore src/NativeBeeper.Server.Linux/NativeBeeper.Server.Linux.csproj --locked-mode -p:SelfContained=false
RUN dotnet publish src/NativeBeeper.Server.Linux/NativeBeeper.Server.Linux.csproj -c Release --no-restore \
      -r linux-x64 -p:SelfContained=false -o /out \
 && if [ ! -x /out/messages-server ]; then \
      echo "Messages Server build: the publish has no messages-server executable (expected AssemblyName messages-server, linux-x64)." >&2; \
      exit 1; \
    fi

# ---- Runtime ----------------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
LABEL org.opencontainers.image.title="Messages Server" \
      org.opencontainers.image.description="Messages Server: the Messages backends on a home server, reached by paired devices over TLS."

# Ubuntu's security fixes as of each build: the digest above freezes the base (OpenSSL, which .NET's TLS uses on Linux,
# among it), and the scheduled build (no-cache-filters: runtime) re-runs this stage, so fixes arrive without a pin bump.
RUN apt-get update \
 && apt-get upgrade -y --no-install-recommends \
 && rm -rf /var/lib/apt/lists/*

# The account the server runs as: 2787:2787, its own. Not Unraid's nobody:users (99:100), which is the Samba guest
# account and most other containers' PUID, so anything else running as it could read the server's files. The names are
# not APP_UID/APP_GID because the base image sets ENV APP_UID, and an ENV always wins over an ARG of the same name.
ARG PUID=2787
ARG PGID=2787
RUN set -e; \
    getent group "$PGID" >/dev/null || groupadd --system --gid "$PGID" messages-server; \
    getent passwd "$PUID" >/dev/null || useradd --system --no-create-home --home-dir /data --shell /usr/sbin/nologin \
      --uid "$PUID" --gid "$PGID" messages-server; \
    mkdir -p /data /keys && chown "$PUID:$PGID" /data /keys && chmod 700 /data /keys

# The server and helpers stay owned by root, so the account they run as cannot change them.
WORKDIR /app
COPY --from=server /out/ /app/
COPY --from=helper /out/messenger-helper /app/messenger-helper
COPY --from=instagram-helper /out/instagram-helper /app/instagram-helper
# The tailnet door (MESSAGES_TAILNET=1) and the licences of what it links.
COPY --from=tailnet-door /out/messages-tailnet /app/messages-tailnet
COPY --from=tailnet-door /out/licenses/ /app/licenses/tailnet/
# Kept in the image so a private-registry install needs no source archive and executes no server to copy its icon.
COPY assets/app-icon.png /app/install/assets/app-icon.png
COPY docker/messages-server/chrome-seccomp.json /app/install/docker/messages-server/chrome-seccomp.json
COPY THIRD_PARTY_NOTICES.md /app/licenses/THIRD_PARTY_NOTICES.md
COPY experiments/Messenger/helper/UPSTREAM_LICENSE.txt /app/licenses/MESSENGER_LICENSE.txt
COPY third_party/mautrix-meta/LICENSE /app/licenses/MAUTRIX_META_LICENSE.txt
COPY third_party/mautrix-meta/LICENSE.exceptions /app/licenses/MAUTRIX_META_LICENSE_EXCEPTIONS.txt
# libSkiaSharp.so (Google Voice's photo re-encoder) has Skia, libjpeg-turbo, libpng, libwebp, FreeType and others linked in; their notices come
# with the package, from the restore above. THIRD_PARTY_NOTICES.md lists the package but carries licence texts only for what the app ships.
COPY --from=server /root/.nuget/packages/skiasharp.nativeassets.linux.nodependencies/*/THIRD-PARTY-NOTICES.txt /app/licenses/SKIASHARP_NATIVE_NOTICES.txt

# MESSAGES_DATA: the data root (sealed sessions, history, paired devices, logs). Useless without /keys.
# MESSAGES_KEYS: the keys and everything that is a secret in itself (the TLS key, all the Messenger helper keeps). Mount a
#   Docker named volume here, never a share: it then lives in Docker's own storage, outside every user share.
# HOME=/data: anything a library keeps under the home folder lands in the volume, so it is kept and backed up with the
#   rest instead of vanishing with the container.
# ASPNETCORE_HTTP_PORTS is blanked: the base image sets it to 8080, and nothing here may listen on plain HTTP.
# DOTNET_EnableDiagnostics=0: no diagnostics socket, so nothing can attach a debugger or dump the process's memory.
ENV APP_UID=$PUID \
    MESSAGES_DATA=/data \
    MESSAGES_KEYS=/keys \
    HOME=/data \
    ASPNETCORE_HTTP_PORTS="" \
    DOTNET_EnableDiagnostics=0
# Where this image came from, for the owner's Settings → Messages Server → Manage server (host.status): MESSAGES_CHANNEL is ghcr:<variant> (plain or google) for an image GitHub
# built (which tag the server follows is its own install's), local for one built on the server; MESSAGES_REVISION is the source commit and MESSAGES_IMAGE_BUILT the build time (yyyyMMddTHHmmssZ). The
# workflow passes all three; install-unraid.sh passes the build time. After everything else, so a new value rebuilds nothing but this layer.
ARG MESSAGES_CHANNEL=local
ARG MESSAGES_REVISION=
ARG CHROME_REFRESH=unset
ENV MESSAGES_CHANNEL=$MESSAGES_CHANNEL \
    MESSAGES_REVISION=$MESSAGES_REVISION \
    MESSAGES_IMAGE_BUILT=$CHROME_REFRESH
VOLUME ["/data", "/keys"]
EXPOSE 8787/tcp
USER $PUID:$PGID

# A TCP connect to the TLS port: it needs no token and opens no unauthenticated endpoint. A healthy server answers it;
# a hung or crashed listener does not. The start period covers first-run certificate generation and helper startup.
HEALTHCHECK --interval=60s --timeout=5s --start-period=120s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8787"]

# SIGTERM runs the server's shutdown (pending sends finish, helpers close). Give it time: compose sets
# stop_grace_period 60s and the Unraid template --stop-timeout=60; Docker's own default of 10 s is too short.
STOPSIGNAL SIGTERM
ENTRYPOINT ["/app/messages-server"]
