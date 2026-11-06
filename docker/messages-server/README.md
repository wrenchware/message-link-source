# Message Link Server

## Install

Pick the one that matches what you have. Each ends at the server's own page, `https://<the server>:8787/` (the browser
warns about the certificate once: the server made it itself), which asks for the setup code from the server's log on
its first visit and then walks you through Tailscale, your accounts, your phone and PCs, notifications and backups.

- **Unraid.** `install-unraid.sh` with the image from GitHub's registry (below, "Updates from GitHub's registry"), or
  built on the server ("Build the image"). Backups go to the `backups` share, `/mnt/user/backups/messages`. A
  Community Applications template is ready in `unraid/ca/` for when the image is public.
- **Proxmox VE.** In the node's **Shell**:

  ```sh
  bash -c "$(curl -fsSL https://raw.githubusercontent.com/wrenchware/switchboard-messages/main/docker/messages-server/proxmox/install-proxmox.sh)"
  ```

  It asks a few questions (each with a default), makes an unprivileged Debian 13 container with the server, Google
  Chrome and the tailnet door, and prints the page's address and the setup code. Until a package is published, copy
  `messages-server-<version>-linux-x64.tar.gz` to the host and add `_ --tarball /root/messages-server-<version>-linux-x64.tar.gz`
  to that line. Running it again updates the server in the same container. The script's header says the rest.
- **Docker Compose**, on any Docker host: `compose.yaml` in this folder, its settings at the top (data and backups
  folders, port, tailnet, and a Tailscale auth key in `.env`). Make the data and backups folders owned by `2787:2787`,
  mode 700, first ("Run it").
- **Linux with systemd and no Docker** (Debian, Ubuntu, an LXC you made yourself):
  `tar -xzf messages-server-<version>-linux-x64.tar.gz && sudo ./messages-server-<version>-linux-x64/install.sh --google`
  (`systemd/README.md`; without `--google` there is no Chrome, so the page can't connect any account).
- **Windows.** Message Link for Windows connects your accounts on the PC itself, with no server; it can also use any server
  above (Settings, Message Link Server).

## Build the image (on the Unraid server)

Unraid has no git, so copy the committed source over. On the PC, in the repository:

```powershell
git archive -o messages-source.tar.gz main
```

Copy `messages-source.tar.gz` to a share (for example `\\tower\system`), then in the Unraid terminal:

```sh
mkdir -p /tmp/messages-source && tar -xzf /mnt/user/system/messages-source.tar.gz -C /tmp/messages-source
cd /tmp/messages-source
docker build -f docker/messages-server/Dockerfile -t messages-server:local .
```

The build downloads the Go and .NET build images and packages and takes several minutes the first time. Run it again
with a newer archive to update; the container picks up the new image when it is recreated.

The simpler way is `tools\Publish-ServerBundle.ps1 -Share <folder> -HostNames <names> -BindAddress <the server's LAN
address>` on the PC, then `bash <that folder>/install-unraid.sh` on the server: it builds the image, prepares both
folders below and (re)starts the container.

## Updates from GitHub's registry

Local builds remain the default. To publish private images from a copy of this repository:

1. Enable GitHub Actions for the repository. Add the repository Actions
   secret **`MESSAGES_GHCR_TOKEN`**: a personal access token **(classic)** with `write:packages` (which includes read).
   Avoid the broad `repo` and `delete:packages` scopes. The token's account must be allowed to create and write packages
   under `wrenchware`; authorize organization SSO if required. If the token belongs to a different username, set the
   repository Actions variable **`MESSAGES_GHCR_USERNAME`** to that username.
2. Run **Message Link Server image** from Actions on `main`, or wait for the next main push. It builds the Linux x64
   image on every main push and each Sunday at 06:23 UTC. A missing secret skips the build and publishes
   nothing; manual runs from other branches cannot publish. Removing the secret disables future publishing.
3. Check that the new `messages-server` package is **Private**, and give the Unraid token's account read access.
   GHCR creates new packages private; the workflow refuses an existing package with any other visibility. It never
   changes visibility. Keep it private unless you mean to share it.

The package is `ghcr.io/wrenchware/messages-server`. Tags are `latest`, the app version (such as `0.1.433`) and exact builds
(`sha-<commit>-run-<run-id>-<attempt>`), each also under a `-google` name for servers installed when there were two images; they are one image. Version tags receive fresh runtime layers too;
use an exact-build tag or digest to pin a particular build, though only the newest three images are kept (the workflow deletes older ones, which needs `delete:packages` on `MESSAGES_GHCR_TOKEN`). The helpers and locked .NET dependencies remain cached;
runtime package installation is repeated and `CHROME_REFRESH` changes on every build. Pinned base-image digests and
.NET/Go versions still require source updates (Dependabot opens a pull request when one moves); the schedule refreshes
Chrome and the base's own packages (`apt-get upgrade` in both runtime stages), not those pins.

On Unraid, create a **separate classic token with only `read:packages`**, from an account with read access to the
package. Log Docker in as root using a hidden prompt (put your GitHub username in the command):

```sh
read -rsp 'GitHub read:packages token: ' GHCR_PULL_TOKEN
printf '\n'
printf '%s' "$GHCR_PULL_TOKEN" | docker login ghcr.io --username YOUR_GITHUB_USERNAME --password-stdin
unset GHCR_PULL_TOKEN
chmod 700 /root/.docker
chmod 600 /root/.docker/config.json
```

Keep this login in root's private Docker configuration, never in `install.env`, a template, a share, or the server's
volumes. Unraid's Docker manager reads `/root/.docker/config.json` for private update checks and pulls. Root's home is
normally in RAM, so log in again after a reboot unless you already restore Docker credentials securely; do not put
them on an exported flash share. The install script uses the saved login and never accepts or writes a token.

Copy the new `install-unraid.sh` beside the existing `install.env` (the publisher also copies it). No source archive
is needed for this mode. With Chrome's sandbox:

```sh
bash /mnt/user/tmp/messages-server/install-unraid.sh --ghcr --chrome-sandbox
```

Use `--ghcr` alone for Chrome without its sandbox. The script pulls and
checks install assets before replacing the running container, retains its data and keys, and saves a matching Unraid
user template. That template keeps the LAN bind, Google options, limits and shutdown timeout when Unraid recreates
the container. The sandbox profile stays at `/boot/config/plugins/dockerMan/messages-server/chrome-seccomp.json`.

In **Auto Update Applications**, enable `messages-server` in the existing nightly 03:00 schedule and set **Docker
stop timeout/delay to at least 60 seconds**: the plugin otherwise supplies its own shorter timeout, even when the
container has a 60-second stop timeout. An update restarts the server and restores the same saved sign-ins.

For another namespace, set `MESSAGES_GHCR_IMAGE=ghcr.io/<owner>/messages-server`; for a version or exact build set
`MESSAGES_GHCR_TAG=<tag>` in `install.env` or the environment.
Keep `latest` for automatic updates. To return to local builds, disable this container's automatic updates, provide
the source archive and run the script without `--ghcr`; remove or update the saved GHCR template before using
DockerMan to recreate it.

Authentication/scopes and default visibility: [GitHub's Container registry guide](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
Unraid's credential lookup: [DockerClient.php](https://github.com/unraid/webgui/blob/26eb1b5c1d547ddbf44a38e63956ba24f659098b/emhttp/plugins/dynamix.docker.manager/include/DockerClient.php);
the plugin's stop timeout: [updateDocker.php](https://github.com/Squidly271/ca.update.applications/blob/9564dc00683b6b662823f3573b654b8e813e5082/source/ca.update.applications/usr/local/emhttp/plugins/ca.update.applications/scripts/updateDocker.php).

## Where things live

- **Keys** (`/keys` in the container): the Docker named volume `messages-server-keys`. It holds the sealing keys, the
  TLS certificate's private key and everything the Messenger helper keeps: the encrypted device and **the Messenger
  history this server has kept** (its own records say who talks to whom, so they stay off the shares), and, with the
  tailnet door on, the server's Tailscale identity (`Tailnet/`, see Your tailnet). A named volume
  lives inside Docker's own storage (`docker.img` on Unraid), outside every user share, so no share setting, export or
  permission tool can reach it.
- **Data** (`/data`, `/mnt/user/appdata/messages-server`): the signed-in sessions, paired devices, the server's own
  stores and its log. Everything secret in it is sealed with the keys, so a copy of this folder alone is useless.
- **Backups** (`/backups`, `/mnt/user/backups/messages`): where the server writes its own backups (see Backups). Owned
  by 2787:2787, mode 700, like the data folder.

If the keys volume is lost (for example when `docker.img` is recreated), the server refuses to start with the old data
(`key_missing`): nothing leaks, but this server's Messenger history is gone with it, and you sign in and pair again
after clearing the data folder. **Back up the keys volume** (see Backups) to keep the history. A keys volume from
another server is refused too (`key_mismatch`).

An install from before this layout has its keys, certificate and Messenger files moved into the keys volume at the
first start (`server key moved`, `server tls moved`, `server helper store moved` in the log).

The owner's Message Link keeps copies of the server's logs (`server.log` and the run before it, Google Messages and its
sign-in history, Google Voice, Instagram, and the Messenger helper's log from the keys volume), refreshed about once a
minute while it is connected, in `ServerLogs` under its data folder (`%LOCALAPPDATA%\Messages for Windows\ServerLogs`)
and in its log viewer as "Server · …". Any paired device can read them, and only by those names; nothing
else on either volume is ever served. No shell or copy loop on the server is needed to read them.

## Run it

The server runs as its own account, 2787:2787, which nothing else on Unraid uses (not `nobody:users`, 99:100, which is
the Samba guest account and most other containers' user). Create the data folder for it, and the keys volume:

```sh
mkdir -p /mnt/user/appdata/messages-server /mnt/user/backups/messages
chown -R 2787:2787 /mnt/user/appdata/messages-server && chmod 700 /mnt/user/appdata/messages-server
chown 2787:2787 /mnt/user/backups/messages && chmod 700 /mnt/user/backups/messages
docker volume create messages-server-keys
docker run --rm --user 0:0 --network none --entrypoint /bin/sh -v messages-server-keys:/keys messages-server:local \
  -c "chown -R 2787:2787 /keys && chmod 700 /keys"
```

Then either:

- **Unraid template:** `cp docker/messages-server/unraid/messages-server.xml /boot/config/plugins/dockerMan/templates-user/my-messages-server.xml`,
  then Docker tab, Add Container, Template: messages-server, Apply.
- **Compose** (any Docker host; on Unraid it needs a compose plugin): `cd docker/messages-server && docker compose up -d`
  (settings at the top of `compose.yaml`).

Publish the port on the server's LAN address only (`MESSAGES_BIND_IP` for compose, `-BindAddress` for the bundle).
Without one it is published on every interface, a VPN's (Tailscale, WireGuard) included. To reach the server from your
tailnet, turn on its own door (below) rather than publishing the port there.

## Your tailnet

Message Link on a phone reaches the server through Tailscale built into the app. With `MESSAGES_TAILNET=1` the server joins
your tailnet as a machine of its own, `messages-server` (`MESSAGES_TAILNET_NAME` to change it), from inside the container
(Tailscale's `tsnet`, which needs no capability, tun device or host network). Your devices reach it at
`messages-server:8787`. The published port stays on the LAN address, and nothing on Unraid changes, its own Tailscale
included. Why not Unraid's Tailscale with the port published on it: after Unraid's Tailscale restarts (a plugin update
does that), it masquerades the connections it forwards to a container as Docker's own address, so the server would see
every tailnet device as one; and a port published on the tailnet address keeps the container from starting at a boot
where Tailscale has no address yet (no internet).

To turn it on:

1. On Unraid's Docker tab, edit messages-server. Extra Parameters keep the port on the LAN address (for example
   `-p 10.13.37.103:8788:8787`). Set **Reach from your tailnet** to `1` (a template without the field: **Add another
   Path, Port, Variable, Label or Device**, Variable, key `MESSAGES_TAILNET`, value `1`). Apply. With compose, set
   `MESSAGES_TAILNET=1` in `.env` and `docker compose up -d`; on Linux or Proxmox, `MESSAGES_TAILNET=1` in
   `/etc/messages-server/environment` and `systemctl restart messages-server` (the Proxmox script turns it on when
   asked).
2. Open the server's page (its address in a browser at home). Its **Tailscale** card, and the first run's Tailscale
   screen, walk you through the rest, each step ticked by itself once the server sees it done, or with a link to the
   exact page of Tailscale's console:
   - **Sign in to Tailscale** with your own account: a button, and the same link as a QR code to sign in on your phone.
     The card moves on by itself. If Tailscale signs the server out later (its key expired, or it was logged out in the
     console), the card says so and offers **Sign in again**, with no restart.
   - **Approve the server**, when your Tailscale network asks for new machines to be approved.
   - **Keep the server signed in** (key expiry off), otherwise Tailscale signs it out after 180 days; and the same for
     each Message Link phone, listed by name.
   - **Only your devices reach it**: the server reads its own access rules. Tailscale's default rule (everyone reaches
     everything) counts as done while your network holds only your own devices; otherwise the card gives the rule to
     add (below), filled in with your login and its address.
   - **Sign it for Tailnet Lock**, only when your network uses Tailnet Lock: the signing command is behind **Show the
     command**, to run on one of your signing devices (Unraid's Tailscale plugin signs nodes on its Lock page).
   - **Fix it for me** does all of that but Tailnet Lock: make an OAuth client in Tailscale (**Settings**, **OAuth
     clients**, scopes **Devices: Core** and **Policy File**, both write) and paste its ID and secret once. The server
     keeps them sealed with its keys (never in a backup; **Remove the OAuth client** forgets them), then turns off key
     expiry for itself and your phones, approves them, and adds the rule to your access rules (only when missing, never
     removing anything), and says what it changed.
   - **Use another Tailscale account** signs the server out of Tailscale and forgets its machine, so it starts over with
     a new sign-in. Remove the old machine in **Machines**.

   Without the page (before its first run is done), the sign-in link and the Tailnet Lock key are in the container's log
   (**Logs**, or `docker logs messages-server 2>&1 | grep "tailnet login"`), never in `server.log`, again every 10
   minutes while needed. The link is only ever on the page or in the container's own output: whoever opens it chooses
   the tailnet the server joins.

The log then says `tailnet state=Running` and `tailnet owner_check=on`.

**Without a browser at hand** (a headless install, or a server you set up from a script): give it a Tailscale auth key
instead of the link. Make one in Tailscale's admin console (**Settings**, **Keys**, **Generate auth key**) and set
`MESSAGES_TAILNET_AUTHKEY` to it: Unraid's **Tailscale auth key** field (advanced, masked), `MESSAGES_TAILNET_AUTHKEY=` in
compose's `.env`, `/etc/messages-server/environment` on Linux, or the Proxmox script's question. The server takes it out
of its environment at start and hands it to the tailnet door alone, on the door's input, never in an argument or a
variable. It is used only while the server has no Tailscale identity yet; once signed in, clear it. A key Tailscale
refuses (expired, revoked, used up) is said in the log (`tailnet authkey refused`), and the sign-in link comes instead.

A **tagged** key (one made with tags, such as `tag:server`) makes a tagged machine. Tailscale then does not say which
person owns it, so the door's same-user check is off (`tailnet owner_check=off reason=tagged`): instead of letting in
only devices signed in as you, it lets in every device your access rules let through, and each of them counts as home.
Use an untagged key unless your access rules already limit who reaches the server's port.

Who gets in: only devices signed in to Tailscale as the user who signed the server in (yours). Another person's device or
a tagged one is turned away before the server sees it (`tailnet connection refused reason=other_user|tagged`, the first and
then one in a hundred). Then the server asks every connection for its device token, as on the home network; a new device
pairs by asking to join, since tailnet addresses count as home. If the server's machine is ever tagged, it cannot know
its owner (`tailnet owner_check=off reason=tagged`) and your access rules decide alone: every device they let through
counts as home, so it reaches the page's sign-in and can ask to join.

Access rules (admin console, **Access controls**): Tailscale's default policy lets every device reach every port of every
other. Limit the server to its one port, from your devices only: name its address (from **Machines**) and give your own
account one rule to it, with no other rule (such as the default's `"*"`) reaching it.

```json
"hosts": { "messages-server": "100.x.y.z" },
"grants": [
  { "src": ["you@example.com"], "dst": ["messages-server"], "ip": ["tcp:8787"] }
]
```

The server keeps the machine's identity in the keys volume (`Tailnet/`), so it survives updates and reboots. Turning
`MESSAGES_TAILNET` off leaves the machine offline in your list; to drop it, choose **Use another Tailscale account** on
the page first, then turn the switch off and remove the machine in **Machines**. A restored keys volume
carries the identity too: never run it beside the original.

What the logs say: `server network listening port=8787 relay=on` and `server tailnet started`; a device that came through
the tailnet is `protocol connection accepted ... via=tailnet`; `tailnet paths direct=N relayed=N` says how the active
devices reach the server (a phone at home should be direct; relayed means it goes through Tailscale's relays, a few tens
of milliseconds more). Tailscale's own log is turned off: nothing about your tailnet is uploaded or kept.

At start the server resumes the accounts signed in on it, at most 4 times an hour, and after a crash it waits first
(1 minute, doubling with each crash in a row, up to 30): every resume logs Messenger and Instagram in again, and a fast
loop of them is what gets an account checkpointed. The log says `server restore held reason=paced wait_s=...` while it waits; the
server answers devices meanwhile. A start with no Messenger or Instagram account to log in is not counted
(`server restore counted=no`), and the first login of accounts moved here counts once for both.

## Notifications on phones

Message Link on Android shows new messages as notifications through a Firebase project of your own (no relay; each push is
sealed for the phone it goes to, so Google carries it without being able to read it). Set it up from the server's page:
its **Notifications on phones** card lists the four steps in Firebase's console, then takes the project's
`google-services.json` and a private key from Service accounts, with the server's password. The server checks the two
files are one project's with an app for Message Link (`com.wrenchware.messages.dev`, or `com.wrenchware.messages`), asks
Google whether it takes the key, keeps both in the keys volume (`push/`) and turns notifications on at once. A phone
signs up the next time Message Link on it connects; the card then names it. Replacing the files works the same way.

In the log: `push setup outcome=on apps=` (or `on_unchecked` when Google could not be reached to check the key, or
`refused reason=` with nothing changed), `push on apps=` at each start, `push registered` when a phone signs up, and
`push sent kind=message outcome=sent` for a message while the phone is in the background. Files put by hand in the
data folder's `push/` still work when the keys volume has none; `MESSAGES_PUSH` names one folder for both.

## Limits

The container is started with limits, so a flood of requests, a runaway browser or a leak cannot take the whole server
down with it: `--memory` (with `--memory-swap` equal, so no swap beyond it) and `--pids-limit` (threads count). The
install script uses 4 GB and 2048, where Chrome alone takes
0.6 to 1.2 GB and a few hundred threads. Change them with `MESSAGES_MEMORY` (such as `3g`), `MESSAGES_PIDS_LIMIT` and,
if you want a cap on processor use, `MESSAGES_CPUS` (such as `2`; not set by default because the script cannot know how
many cores the server has) in `install.env` or the environment. The compose file and the Unraid templates carry the same
values. The server itself also limits each paired device: at most 4 connections open at once (a new one closes the
device's oldest), 256 requests in flight on a connection (the next is refused as unavailable and can be sent again), and
no more than 64 open connections in all.

## Chrome's sandbox

By default the image runs Chrome with `--no-sandbox`, because a container with every capability dropped cannot give
Chrome the user namespaces its sandbox is made of. Chrome runs as the same account that can read the keys volume, so a page's
code that broke out of a renderer would have every provider's secrets. `--chrome-sandbox` turns the sandbox on:

```sh
bash /mnt/user/tmp/messages-server/install-unraid.sh --chrome-sandbox
```

What it changes, and nothing more: the container is started with `docker/messages-server/chrome-seccomp.json` (Docker's own
default seccomp profile (from github.com/moby/profiles), plus one rule that allows `chroot`, `clone`,
`setns` and `unshare` without a capability) and without `MESSAGES_CHROMIUM_NO_SANDBOX=1`. It is not `--privileged` and gets no
`SYS_ADMIN`; every capability is still dropped and `no-new-privileges` still holds. The cost is real: the container can now
create user namespaces, a kernel feature with its own bug history, in exchange for the browser's renderers being jailed.

What the server does about it: before it starts Chrome with the sandbox on it asks the container to make a user namespace, and
refuses to start Chrome if it cannot (`server google refused reason=sandbox_unavailable detail=userns_disabled|userns_denied|userns_restricted`),
rather than starting it to fail. Once Chrome has shown a page it reads its processes from `/proc` and logs one line,
`google chrome sandbox=active renderers=N filtered=N`. `active` means every renderer carries a seccomp filter, which `--no-sandbox`
never gives it; `inactive` or `unknown` means the sandbox is not proven. `server google enabled ... sandbox=on` only says what was asked.

**What you must do on Unraid:** nothing, if the kernel allows unprivileged user namespaces, which the script checks first
(`cat /proc/sys/user/max_user_namespaces` must be above 0, as it is on Unraid's stock kernel). If it
says they are off, leave the sandbox off.

**To check it**: install with `--chrome-sandbox`; the install output shows the
`server google` lines. Wait about a minute after Chrome starts and look for `google chrome sandbox=active` in
`docker logs messages-server`. Then sign in as usual and check that the Google pairing renews. If Chrome does not start, the log says
why (`sandbox_unavailable`, or `google chrome start=failed`; Chrome's own message is in `last-chrome-error.log` on the keys volume).
To go back, run the script again without `--chrome-sandbox`: it is the same data and the same sign-in.

## Pairing code and fingerprint

Both are printed in the container log when the server starts (the code only while no device is paired): the Docker
tab, the container's icon, **Logs**, or

```sh
docker logs messages-server 2>&1 | grep -E "pairing code=|tls fingerprint"
```

The code works once, for 10 minutes; with no device paired, a new one is printed when it runs out. A wrong code does
not use it up, but tries are slowed down (one a second, and longer for a device that keeps getting it wrong). The code
is never written to the log file in the data folder, only to the container's own log. Check that the fingerprint the
app shows matches the one in the log before you accept it.

## Devices

Devices are added and removed on the server's page, with its password. No device outranks another: each reads and sends, and can remove itself.

To start over with no device: stop the container, delete `devices.json` in the data folder and start it again. That
unpairs every device, and the log shows a new pairing code. With the server's page on (below), a lost owner PC is
removed there instead, and the next oldest device takes over.

## The server's page

The server has a page of its own on the same port: open `https://<this server>:8787/` in a browser on the home network
(or your Tailscale network). It shows devices waiting to join, the server's version and uptime, the accounts it holds, and its
paired devices; it approves a device that asks to join, removes a device, shows a QR code a phone scans to join (**Add a
phone**) and a line a PC pastes (**Add a PC**), each asking for the password again; and it shows the end of the server's
log, and what it keeps on disk by kind (Storage, measured in the background and refreshed every ten minutes). Accounts are connected and removed there too, and nowhere else.

**First run.** On its first start without a password, the server prints a one-time setup code to the container's output
(`server setup code=XXXXX-XXXXX`, good for a day; **Logs** on Unraid, `docker logs messages-server 2>&1 | grep -i "setup
code"`), never to `server.log`. The page's first visit asks for it, then for the page's password (12 characters or more),
which the server keeps as a salted PBKDF2 hash beside its keys (`server-page.password` in the keys volume), never the
password itself. The page then walks you through Tailscale, your accounts, a phone, notifications, a PC and backups, one
screen each and every one skippable; a checklist on the front page keeps what is left until it is done or hidden. A
restart prints a new code until the password is set. Forgot the password? Set `MESSAGES_ADMIN_PASSWORD` (below) and start
the server again: it wins over the kept one.

To set the password where the server is installed instead, set `MESSAGES_ADMIN_PASSWORD`: in Unraid, edit the container, choose **Add another Path, Port, Variable,
Label or Device**, pick **Variable**, give it the key `MESSAGES_ADMIN_PASSWORD`, set **Password Mask** to Yes, enter the
password and apply (a template from the install script already has the field). The page is off, and the log says
`server page off reason=` why, for a password that is under 12 characters, over 128 bytes, or one Unraid would
change between its editor and the container: two spaces in a row, a space at either end, a tab or line break, or an
`&`-code such as `&amp;` (Unraid collapses the first and decodes the last when it recreates the container, so the
password would stop working at the next update).

Keeping the password in the template has a cost: **Mask** only hides the field. Unraid
puts every variable on the `docker run` command line when you apply or update the container, shows that command in the
window it prints, and other programs on the Unraid box can see it while it runs; the template sits on the flash drive,
so keep the flash share private. Use a password you use nowhere else. A reinstall with the install script keeps the
running container's password (or, when no container is left, the saved template's), and keeps it empty when it was
cleared; it passes the value to Docker by name, so its own command line never holds it. Do not put it in
`install.env`: that file sits on a share and is rewritten by every publish.

- **Check the certificate before you type the password.** The browser warns the first time, because the certificate is
  the server's own. Open the browser's certificate viewer (the warning page, or the padlock) and compare its SHA-256
  fingerprint with the `tls fingerprint` line in the container log (the viewer shows it in capitals with colons). The
  page cannot vouch for itself: what it shows after you sign in is only as true as that check.
- A sign-in lasts 7 days and survives updates. **Sign out** ends every sign-in, in every browser; so does changing the
  password or turning the page off, even if an earlier password comes back later.
- A pairing code and removing a device need the password itself, not only the sign-in: a browser's cookie is sent to
  any other HTTPS service on the same address too, so a copy of it is never enough to read everything.
- Wrong passwords are slowed down, as wrong pairing codes are; tries sent at once count as one. The password and the
  sign-in are never logged, and the server takes the password out of its environment at start, so no helper or browser
  it runs inherits it.
- The page is served only to home and private-network addresses (the private ranges, a VPN's 100.64.0.0/10,
  loopback, IPv6 unique-local and link-local): should port 8787 ever be reachable from outside, the page is not there.
  A form posted from any other page is refused, and nothing on the page can reach the app's connection.
- The install script publishes the port on IPv4 only. With an IPv6 `MESSAGES_BIND_IP`, Docker carries each connection
  through its own proxy, so the server sees the proxy's private address for every device: the page then cannot tell
  where a browser is, and every device shares one pace of password tries.

## Moving a PC's choices here (a fresh start)

A PC moves its pins, names, drafts, labels and saved messages onto this server with Settings → Message Link Server → **Move
to this server…**. The
server's shared space must be empty first (or already belong to that PC), so a server that has been used starts again with
one command. It removes only the server's shared choices (`Server/shared.db` and `Server/space.protected`); sign-ins, keys and
paired devices stay. Stop the container, run it once by hand with a terminal, type `RESET` when asked, then start it again. The
reset runs from the image the container itself runs (not a guessed name, which may be an older build), with no network:

```sh
docker stop -t 60 messages-server
docker run --rm -it --network none --user 2787:2787 -v /mnt/user/appdata/messages-server:/data -e MESSAGES_DATA=/data \
  "$(docker inspect -f '{{.Config.Image}}' messages-server)" --reset-space
docker start messages-server
```

It refuses while the server is running, without a terminal (`-it`), while the shared state still holds a scheduled message or a send
that may not have gone through (it names how many; send or cancel them from Message Link first), or if anything but `RESET` is typed.
The server rejects an argument it does not know, so a typo cannot start a full server.

Keep every other device closed (Message Link on a second PC, a phone) from the reset until the move has finished: a device that is
connected writes its choices into the new, empty space, and the move then refuses it as already in use.

## Moving Messenger and Instagram here, and back

Moving to the server also moves a PC's Messenger and Instagram accounts: Messenger as that PC's own device (its
sign-in, its encrypted-device keys and its saved history), Instagram as that PC's sign-in. Nothing signs in again and no new
Messenger device is added. Google Messages and Google Voice are not carried; sign them in on the server itself. The move
refuses while the server has a Messenger or Instagram sign-in of its own (sign it out of the server first). Signing Messenger out
keeps its encrypted-device keys on the server; if the same account then moves in, the move sets those files aside under
`Messenger/set-aside` in the keys volume (kept, never deleted; the log says `account move set aside`). The PC's copies are
deleted once the server has signed the account in.

A move back (Settings → Message Link Server → **Move back to this PC…**) holds the accounts on the server and takes them home;
the server then keeps a paused copy with a note that they moved away, which **Remove** on the server's page forgets. While that note is there the server will not restore, connect, turn on encryption
for or sign in Messenger or Instagram; a new move from the PC takes it away. If the PC is lost and the server should run them
again, clear the note with a command like the reset's (stop the container, mount both volumes, type `CLEAR`):

```sh
docker stop -t 60 messages-server
docker run --rm -it --network none --user 2787:2787 -v /mnt/user/appdata/messages-server:/data -v messages-server-keys:/keys \
  -e MESSAGES_DATA=/data -e MESSAGES_KEYS=/keys "$(docker inspect -f '{{.Config.Image}}' messages-server)" --clear-held
docker start messages-server
```

It removes only the note and the marker files it left in the accounts' folders; no sign-in, key or history is touched.

## Stop

Stop it from the Docker tab, or `docker stop -t 60 messages-server`. Give it the full 60 seconds so pending sends finish.

## Backups

The server writes its own backups to `/backups` whenever that folder is mapped: the template's **Backups** path
(`/mnt/user/backups/messages`, a `backups` share), compose's `MESSAGES_BACKUPS_DIR`, `/var/lib/messages-server/backups`
on Linux (`MESSAGES_BACKUPS`), or a folder of the Proxmox host. A server backup holds the history and the server's
identity (its certificate, devices and push setup) but never a service sign-in, Messenger's encrypted device or the
Tailscale identity: after restoring one, sign the accounts in again on the page. Copying that folder elsewhere is up to
your own backup tool.

For a full copy by hand, keys and sign-ins included, back up only while the container is stopped, and keep every copy
private: never in an exported share. `umask 077` makes the archives readable by root alone. The destinations below are
examples; use a folder that is not shared.

```sh
docker stop -t 60 messages-server
umask 077
mkdir -p /root/messages-backup
# The data (sealed; useless without the keys).
tar -czpf /root/messages-backup/messages-data-$(date +%F).tar.gz -C /mnt/user/appdata messages-server
# The keys, separately: they also hold the Messenger history, so back them up whenever you back up the data.
docker run --rm --network none --user 0:0 --entrypoint /bin/tar -v messages-server-keys:/keys:ro messages-server:local \
  -czpf - -C /keys . > /root/messages-backup/messages-keys-$(date +%F).tar.gz
chmod 600 /root/messages-backup/*.tar.gz
docker start messages-server
```

The keys archive opens everything in the data archive: keep it apart from it (for example on a USB stick), and never
on a share. It also holds the Messenger device and history, so restore it only to this server. Never run two servers
from the same data at once (a restored copy beside the original): they would be the same Messenger device in two
places.

After restoring a backup, revoke again any device you removed since that backup was made. The server refuses a device
list older than the one it last wrote while the keys volume still remembers that, but a restored keys volume forgets it
too.
