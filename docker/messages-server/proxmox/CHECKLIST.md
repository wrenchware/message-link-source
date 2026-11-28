# Checking install-proxmox.sh on a Proxmox host

A run from nothing to a phone joined and the Facebook test account signed in, on a fresh container. Use the Facebook
test account only: no real account is signed in on this test server, and nothing is sent anywhere but the test thread.

The setup code, the first-run steps and the QR code (part 3) come with the server's first run (10.4) and the Tailscale
screen (10.8). With a package from before them, set the page password at the script's question instead, sign Tailscale
in with the link the script prints, and add the phone with the pairing code.

The script and the package come from the newest stable release (a promotion publishes both, `docs/release.md`, "Server
image channels"). To try a build that isn't released yet, use `--tarball` with the package
`messages-server-<version>-linux-x64.tar.gz` and its `.sha256` from `tools\Build-ServerTarball.ps1` (in
`artifacts\server-tarball`), copied to the host with the script; the nightly update then leaves that package alone.

Have ready: the phone with the regular Message Link app
(`com.wrenchware.messages`, sideloaded with `android\Build.ps1 -Release -Install -Device <serial>`, never the `.dev`
build), a Google account that has never used Tailscale or Firebase, a PC browser signed in as that account only (a
fresh browser profile, so no other account's sign-in is offered), and a folder for screenshots.

**Screenshots** (for the website's guides): on every Tailscale and Firebase page, and every server-page step, press
Win+Shift+S, take the window, and save it into the folder numbered in order (`01-...png`). Don't hide the email or
project id while taking them; they are blurred afterwards, before anything is published.

## 1. Make the container

1. In the Proxmox web interface: the node, **Shell**. Run:
   `bash -c "$(curl -fsSL https://github.com/wrenchware/message-link-source/releases/latest/download/install-proxmox.sh)"`
   (an unreleased build: `bash /root/install-proxmox.sh --tarball /root/messages-server-<version>-linux-x64.tar.gz`).
2. Wanted: a blue dialog box titled "Message Link Server <version>" offering **Default** and **Advanced**. Choose
   **Advanced** and take every value (Enter), except: the backups folder, a test folder such as
   `/mnt/messages-backups-test`; and leave the Tailscale auth key and the page password empty (Enter). Wanted: the
   storages and bridges are lists to pick from, and both secrets show as stars.
3. Wanted: one last box listing what it will make, with **Make it** and **Cancel**. Choose **Make it**.
4. Wanted: a line per step, each ending with a check mark, and no apt or download noise.
5. Note how long it took, whether any box was cut off or hard to read, and whether any question was unclear.

Report: the last 20 lines it printed (the setup code and pairing code there are for a test server and expire, but leave
out the Tailscale link), anything it stopped on, and whether **Default** with **Cancel** on the next box ends with
"Nothing was changed." (try it once before the real run).

## 2. The container

In the host's shell (`<id>` is the container's number it printed):

```sh
pct config <id> | grep -E 'features|unprivileged|mp0|tags'
pct exec <id> -- systemctl is-active messages-server messages-server-update.timer
pct exec <id> -- journalctl -u messages-server --no-pager -o cat | grep -E 'server google|google chrome|sandbox|tailnet state|server started|refused'
pct exec <id> -- stat -c '%U %a %n' /var/lib/messages-server/backups
ls -ln /mnt/messages-backups-test -d
pct exec <id> -- cat /etc/messages-server/update
```

Also open the container's **Summary** in the web interface: wanted, notes headed "Message Link Server" with the page's
address as a link, the guide's link and the update command, shown as formatted text.

Report all of it. The two things this run is for:

- **Chrome's sandbox in an unprivileged container.** Wanted: `server google enabled browser=chrome sandbox=on`, then,
  about a minute after Chrome starts, `google chrome sandbox=active`. If it says `sandbox_unavailable detail=...`,
  report the detail word and the output of
  `cat /proc/sys/user/max_user_namespaces /proc/sys/kernel/apparmor_restrict_unprivileged_userns` on the host. Do not
  turn the sandbox off to make it start.
- **The backups folder**: owned by `messages-server` with mode 700 inside, and by `102787` on the host.

## 3. The page and the setup

1. On the PC, open the page address it printed (`https://<address>:8787/`). Accept the certificate warning once.
2. Enter the setup code and choose the page's password.
3. Follow the page's first-run steps:
   - **Tailscale**: sign in with your own Tailscale account. Wanted: the step turns green by itself, and the machine
     `messages-server` appears in Tailscale's admin console. Report anything the checklist on that step says is left.
   - **Accounts**: connect **Facebook (Messenger) with the test account only**. Report whether the sign-in window
     showed and finished, and any "Was this you?" prompt.
   - Skip Google this time unless the sandbox line above said `active`; if it did, report whether the Google sign-in
     screen shows on the page (do not sign a real Google account in).
4. **Add a phone**: on the page, show the QR code. On the phone, in Message Link, scan it. Wanted: the phone signs in to
   Tailscale, joins without typing a code, and shows the test account's conversations.
5. On the phone, open the owner-designated Messenger test thread and send one message there. Nowhere else.

Report: each step's result, the time from scanning to conversations shown, and the phone's Settings, Tailscale section
(direct or relayed).

## 4. Running it again (update)

Run the same command again. Wanted: a box "Update Message Link Server" naming the container, then a check-mark line
per step, and the server comes back with the test account still signed in and the phone still joined (no new
pairing). Report what it printed.

## 5. The nightly update and backups

```sh
pct exec <id> -- update
ls -l /mnt/messages-backups-test
```

Wanted from `update`: "the installed release is the newest." (installed from the release), or "no release address"
(installed with `--tarball`), and with Google on a line about Chrome. Report the lines, and whether a backup file
appears in the test folder after the page's backups step has been run.

## 5b. Notifications (Firebase)

After part 3 (a phone joined). Follow only what the page says, and its "See these steps with pictures" link, as a new
user would. Take a screenshot of each page on the way. Start a stopwatch.

1. On the page, open the notifications step (the checklist, or Manage → Notifications).
2. **Open Firebase**, signed in as the second account. Add a project named as the page says. Wanted: Firebase shows its
   terms once (first time on this account); Google Analytics can be turned off.
3. Add an Android app with the one package name the page shows (`com.wrenchware.messages`, its Copy button). Skip the
   steps after it's registered.
4. **Project settings, General**: download `google-services.json`. **Service accounts**: **Generate new private key**.
5. Drop both files on the page, in either order, and turn notifications on.
6. Wanted: the test push says "Arrived on <phone>", and the phone shows a notification. Nothing on the page or the
   phone mentions a second app or `.dev`.
7. Close Message Link on the phone (swipe it away), then have something arrive in the test thread (the test account,
   from another device). Wanted: a notification with the app closed.

Report: the time from Open Firebase to "Arrived", every moment of doubt (a button not where the page said, a word the
page didn't explain, a Firebase screen the page didn't mention), and what the page and the phone showed.

## 6. Messenger and Instagram signed in from the Windows app (10.5)

The server's own Chrome signs Messenger and Instagram in, shown in the Windows app's window, on a throwaway profile
that is deleted afterwards. **Facebook test account only.** Instagram is never signed in here: the owner's only
Instagram is the real account, so its part stops at the login page.

Needs: a package and a Windows build from the same commit (protocol 27), Google on in the container (the sandbox line
in part 2 says `server google enabled browser=chrome`), and the Facebook test account signed out of the server (on the
page, or skip part 3's Facebook step).

1. On the PC, start the isolated development copy (`powershell -NoProfile -File tools\Run-DevBuild.ps1`), never the
   installed app. In **Settings → Connections → Message Link Server → Manage…**, join this test server (**Ask to join**,
   approve on the page). The order devices joined in makes no difference.
2. In a second shell on the Proxmox host, follow the log:
   `pct exec <id> -- journalctl -u messages-server -f -o cat | grep -E 'remote sign-in|google chrome|server sign-in'`
3. **Cancel.** In the dev copy's **Settings → Connections**, Messenger, **Connect**. Wanted: a window titled
   "Sign in to Facebook" saying "This is Message Link Server's own browser. Sign in to Facebook here, then choose Done.",
   showing facebook.com's login page with an address bar, and buttons **Done** and **Cancel**. Choose **Cancel** (and
   once more, closing the window with its X instead). Wanted: "Facebook sign-in was canceled.",
   `remote sign-in service=messenger outcome=cancelled reason=client`, `keys/SignIn` empty (below), and the server's
   Google sign-in (if any) still working.
4. **Done too early.** Connect again and choose **Done** on the login page without signing in. Wanted: the window
   closes, Settings says "The Facebook sign-in didn't finish. Choose Connect to try again.", the log has
   `remote sign-in service=messenger outcome=refused reason=missing_cookies`, and
   `pct exec <id> -- ls -A /var/lib/messages-server/keys/SignIn` prints nothing.
5. **Messenger.** Connect once more.
   - Sign in with the Facebook test account. Answer anything Facebook asks (a code, "Was this you?" on the test
     account's phone, "Trust this browser"). Note each prompt Facebook showed, in order.
   - Choose **Done** only once Facebook shows its home page. The header changes to "Finishing the sign-in on Message
     Link Server…" and the window closes by itself.
   - Wanted in Settings: "Loading the Messenger inbox…", "Connected. Turning on encrypted conversations…", then
     "Connected. N conversations joined your chat list. Encrypted conversations are on too: …".
   - Wanted in the log, in this order and nothing with a name, an id or a cookie:
     ```
     remote sign-in service=messenger outcome=started reason=none
     remote sign-in service=messenger outcome=signed_in reason=none
     remote sign-in service=messenger outcome=connected reason=none
     remote sign-in service=messenger outcome=encryption reason=on
     ```
     with `google chrome started` lines between them (Chrome on the throwaway profile, then back on the Google
     profile if one is kept). `reason=e2ee_...` or `reason=TimeoutException` on the last line means encryption did not
     turn on: report it, do not press its Settings button more than once.
   - `pct exec <id> -- ls -A /var/lib/messages-server/keys/SignIn` prints nothing (the profile is gone).
   - In Facebook's Settings → Security and login → Where you're logged in, on the test account's phone: report what
     the new session is called and where Facebook places it (it should be the server's address, not the PC's).
6. **Instagram, without signing in.** Instagram, **Connect**. Wanted: "Sign in to Instagram" at Instagram's login
   page. Do not type the real account. Click the address bar, type `instagram.com/direct/inbox/` and press Enter.
   Wanted: Chrome's "blocked" page (the inbox never loads in the server's browser). Then **Cancel**:
   `remote sign-in service=instagram outcome=cancelled reason=client`.

If Connect opens this PC's own Facebook or Instagram window instead of the server's, the server is not offering the
sign-in: report `pct exec <id> -- journalctl -u messages-server --no-pager -o cat | grep -E 'remote_signin refused|server google'`.

Report: each step's result, Facebook's prompts and how long from Done to "Connected", the log lines, and anything in
the window that looked wrong (a frozen picture, keys that did not type, the Done button doing nothing).

## 7. Remove it

1. On the page, remove the Facebook test account (so Facebook forgets the server's device).
2. Remove the `messages-server` machine in Tailscale's admin console.
3. `pct stop <id> && pct destroy <id>`, then `rm -rf /mnt/messages-backups-test`.
4. The phone's node in the second account's Tailscale, the Firebase project (unless kept for the next run), and the
   sideloaded app if not wanted.

Report anything that was left behind.
