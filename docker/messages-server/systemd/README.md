# Message Link Server on a Linux host without Docker

This package runs Message Link Server as a systemd service on an x86-64 Linux host: Ubuntu, Debian, or a Proxmox LXC (the
Proxmox script, `proxmox/install-proxmox.sh` in the source, makes the container and runs this for you). It is the same
server as the container, with Messenger and Instagram, its own door on your Tailscale network, and, with `--google`,
Google Messages and Google Voice beside the server's own Google Chrome.

Nothing needs installing first: .NET is inside the package. It does use the host's ICU, OpenSSL 3, CA certificates and
time zone data, and `install.sh` adds whichever of them is missing with `apt-get` or `dnf`.

## Install or update

```sh
tar -xzf messages-server-<version>-linux-x64.tar.gz
sudo ./messages-server-<version>-linux-x64/install.sh            # keeps existing accounts; no new sign-in without Chrome
sudo ./messages-server-<version>-linux-x64/install.sh --google   # with Google Chrome (Debian, Ubuntu), which the page connects accounts in
```

It makes the account `messages-server`, puts the program in `/opt/messages-server` and starts `messages-server.service`.
Running a newer package's `install.sh` updates in place: the server is stopped (pending sends finish first), replaced and
started again with the same sign-ins, paired devices and Google setting. `--no-google` turns Google off again.

## Google

`--google` installs official Google Chrome from Google's own apt repository (its signing key checked against a pinned
fingerprint), Xvfb for the virtual display Chrome draws on, and Chrome's policy for Google, Facebook and Instagram
sign-in. Chrome is needed to connect any new account on the page. On a plain Linux host the installer asks whether
you trust the other local users and programs before allowing sign-in on its shared network. For an unattended install
on a trusted host, use `--google --allow-host-sign-in`. The choice is kept across updates as
`MESSAGES_REMOTE_SIGNIN_HOST_NETWORK=1` in `/etc/messages-server/environment`; remove that line to withdraw it.
An update with no flags keeps the existing sign-in permission, including when it is off. To enable sign-in on an
older plain-Linux install, run the installer with `--google` and answer its question.
The Proxmox installer gives the server its own network and needs no such override.
The server keeps Chrome running and signed in, and you sign in on the server's page. Chrome runs with its own
sandbox, which is made of user namespaces: the kernel must let an ordinary account make them
(`/proc/sys/user/max_user_namespaces` above 0, and on Ubuntu 24.04 and later
`/proc/sys/kernel/apparmor_restrict_unprivileged_userns` at 0). The server never starts Chrome without its sandbox; it
says `server google refused reason=sandbox_unavailable` instead, and `google chrome sandbox=active` once it is running.

## Settings

`/etc/messages-server/environment` (root only), then `sudo systemctl restart messages-server`:

- `MESSAGES_HOSTNAMES`: the names and addresses devices reach the server by (for example `server.lan,192.168.1.20`).
  They go into the certificate made on the first start, so set them before then.
- `MESSAGES_PORT`: the HTTPS port, 8787 by default. Allow it in the firewall for the home network only.
- `MESSAGES_ADMIN_PASSWORD`: the server's own page at `https://<this server>:8787/` (12 characters or more, used
  nowhere else). Without it, the page asks for the setup code from the server's log on its first visit.
- `MESSAGES_TAILNET=1`: the server joins your Tailscale network as a machine of its own (`MESSAGES_TAILNET_NAME`,
  `messages-server` by default), so Message Link on a phone reaches it from anywhere. The page walks you through signing it
  in; the link is also in the log.
- `MESSAGES_TAILNET_AUTHKEY`: for a machine with no browser at hand, a Tailscale auth key (`tskey-auth-...`, made in
  Tailscale's admin console under Settings, Keys) signs the server in by itself the first time. Only the tailnet door
  is given it. A **tagged** key makes a tagged machine, and the server then cannot tell who owns it: instead of letting
  in only devices signed in as you, it lets in every device your access rules let through. Use an untagged key unless
  your access rules already limit who reaches it.

## Setup code, pairing code and fingerprint

All three are printed when the server starts (the pairing code only while no device is paired):

```sh
sudo journalctl -u messages-server --no-pager | grep -Ei "setup code|pairing code=|tls fingerprint|tailnet login"
```

In Message Link: Settings → Message Link Server, with the server's **Address** (and port, if not 8787), the **Fingerprint**
and the **Pairing code**. The code works once, for 10 minutes; a new one is printed when it runs out.

## Where things live

- `/var/lib/messages-server/keys`: the sealing keys, the TLS certificate's private key, everything the Messenger
  helper keeps (including this server's Messenger history), Google's Chrome profile, and the server's Tailscale
  identity (`Tailnet/`).
- `/var/lib/messages-server/data`: sign-ins, paired devices, the server's stores and its log, `server.log`. Everything
  secret in it is sealed with the keys, so it is useless without them.
- `/var/lib/messages-server/backups`: the server's own backups (`MESSAGES_BACKUPS`). On Proxmox the script can mount a
  folder of the host here, so the backups live outside the container.

All three are private to the account. To copy the data and keys by hand, stop the server first:

```sh
sudo systemctl stop messages-server
sudo tar -czf messages-server-backup.tar.gz -C /var/lib messages-server/data messages-server/keys
sudo systemctl start messages-server
```

Never run a copy of the keys beside the server they came from: the Tailscale identity and Messenger's device would be in
two places at once.

## Updates

`messages-server-update.timer` runs each night at about 03:00. It updates the server only when
`/etc/messages-server/update` names a release address (`MESSAGES_RELEASE_URL=`, empty until a public release exists),
checking the download against its `.sha256` before anything is stopped. With Google on it also takes the newest stable
Google Chrome, stopping the server while Chrome is replaced. Run it now with `sudo systemctl start messages-server-update`
and read what it did with `journalctl -u messages-server-update`.

## Limits

The service has the container's limits: 2 GB of memory with no swap beyond it, and 1024 threads (4 GB and 2048 with
Google). To change them, `sudo systemctl edit messages-server` and set `MemoryMax=` or `TasksMax=` under `[Service]`. It
runs with no privileges or capabilities and can write only its own folder in `/var/lib`.

## Remove it

```sh
sudo /opt/messages-server/uninstall.sh          # the program, service and nightly update; the data, keys, backups and settings stay
sudo /opt/messages-server/uninstall.sh --all    # also the data, keys, settings and account (the backups stay; add --backups)
```

Remove the accounts on the server's page first if the server should sign out of them.
