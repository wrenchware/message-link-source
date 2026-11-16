# Message Link reports for Hermes

`ml-report` lets a Hermes profile send a short report into its own conversation in Message Link: a background job that
finished, a scheduled script's result, or something that needs the owner. It is a Python 3 script (3.10 or later,
standard library only). `mlreport_client.py` holds the code it shares with the Hermes plugin. Tested with Hermes
v0.23.1.

## How it works

Each Hermes profile has its own connection code, kept in the profile's `.env` under two names with the same value:
`MESSAGE_LINK_REPORTS` for `ml-report`, and `MESSAGELINK_CODE` for the plugin. The plugin declares `MESSAGELINK_CODE`
as its password, so Hermes treats it as a platform secret and never passes it to commands; commands get
`MESSAGE_LINK_REPORTS` instead. The code holds the server's address, the fingerprint of its certificate and a token for that profile.
The setup command makes it inside the profile (below); setting up by hand, the server's page shows it once.

- `ml-report` connects over HTTPS and checks the server's certificate against the fingerprint before it sends anything.
- It posts the text to `/v1/reports` with the token as a bearer token. `GET /v1/reports/check` tests a code.
- It never follows a redirect, and it never retries a report.
- The server accepts reports only from the home network. A report is plain text, up to 8,000 characters.

Exit codes: 0 delivered (or already delivered), 1 refused or failed, 2 setup problem (the code is missing or malformed,
the certificate doesn't match, or the server can't be reached). The reason is printed on standard error. Neither the
code nor the token is ever printed.

## Set up in one command

On the Message Link server's page, open Hermes, select **Set up…** on the profile's Reports line and enter the server's
password. The page shows one command. Run it on the computer that runs Hermes: if Hermes runs in Docker, run it there,
not inside the container. On a PC that runs its own Message Link Server, the command is for PowerShell on that PC.

The command works once, within 10 minutes. It downloads the installer over a connection pinned to the server's key,
checks the installer's SHA-256 against the one in the command, and only then runs it. With Hermes in Docker, it finds
the Hermes container (and asks which one if there are several) and runs the rest inside it, as the owner of the
profile's folder and with Hermes's own Python. Then, inside the profile:

1. It finds an address where Hermes reaches the server: the one the profile's current code names, the server's
   container name on a Docker network both containers share, then the address the page was opened at.
2. It makes the profile's report token there and sends the server only its hash. The token never leaves Hermes, and
   the command can't be used again.
3. It puts `ml-report`, `mlreport_client.py`, `SKILL.md` and the `messagelink` plugin in the profile, sets
   the code in its `.env` under both names, enables the plugin, and adds the two settings below to `config.yaml`, keeping its
   comments. If `config.yaml` is laid out in a way it can't edit with certainty, it changes nothing and prints the
   lines to add by hand.
4. It checks the new code once.
5. It restarts Hermes, which every profile shares, and ends with "Done.", the profile's name and "can now message you
   first." In Docker the container keeps running. If a chat or job is running in Hermes, Hermes refuses the restart.
   The setup is still done, and the command says to restart Hermes when nothing is running.

Hermes needs that restart once. Until then, commands in a profile that ran one before setup can't see
`MESSAGE_LINK_REPORTS`. Run the command again, from **New code…**, to replace the code; nothing else changes. A
profile set up before both names existed gets `MESSAGE_LINK_REPORTS` that way, and `MESSAGELINK_CODE` comes out of
its `env_passthrough`, where Hermes refuses it.

If Hermes can't reach the server, the command says so. With Hermes and Message Link in Docker on the same computer,
put both containers on one user-defined Docker network (not the default bridge): a connection through a published
port arrives from Docker's own address, which the server refuses.

On Unraid, make the network in a terminal, such as `docker network create messagelink`, and choose it as the network
of both containers. Give it a name that starts with a letter: Unraid removes a network whose name starts with a number
when Docker restarts. If Unraid hasn't kept a network made this way before, go to Settings, Docker, stop Docker, set
**Preserve user defined networks** to **Yes**, and start Docker again.

## Set up by hand

The command's page has **Set up by hand instead**, which shows a connection code once.

1. Put `ml-report` and `mlreport_client.py` in the profile's `scripts` folder (`$HERMES_HOME/scripts/`), and make
   `ml-report` executable (`chmod +x`).
2. Add the connection code to the profile's `.env`:

   ```
   MESSAGE_LINK_REPORTS=mlrc1....
   ```

   `ml-report` also reads `MESSAGELINK_CODE`, the plugin's name for it, when `MESSAGE_LINK_REPORTS` isn't set. In a
   profile with the plugin, Hermes never passes that name to commands.

3. Let commands see it. Hermes removes secrets from the commands it runs, so do one of these:
   - list it under `terminal.env_passthrough` in the profile's `config.yaml`:

     ```yaml
     terminal:
       env_passthrough:
         - MESSAGE_LINK_REPORTS
     ```

     Then restart Hermes once (`hermes gateway restart --all`, or restart its container). Hermes keeps the list it
     read first until it restarts.

   - or put `SKILL.md` in the profile's skills folder (`$HERMES_HOME/skills/message-link-reports/SKILL.md`). It
     declares the variable, which Hermes passes to commands while the skill is loaded.

4. From the profile's terminal, run `ml-report check`.

Then:

```
ml-report "The backup finished."
long-job | tail -n 20 | ml-report
ml-report --key nightly-2026-11-15 "Nightly build passed."
```

A no-agent scheduled job runs a script file and takes no arguments for it, so put the `ml-report` call in a small
script of its own and give the job that script.

If the profile's terminal runs over SSH, use the Message Link plugin for Hermes instead, which runs inside Hermes.

## The Hermes plugin

The `messagelink` plugin lets Hermes itself deliver into Message Link: `hermes send --to messagelink "..."` and
scheduled jobs with `--deliver messagelink`. The plugin reads the code through Hermes's settings for the profile, not
from the process environment, and Hermes keeps it from commands. (Commands get the same code as
`MESSAGE_LINK_REPORTS`, for `ml-report`.) The
plugin only sends: chat with Hermes in Message Link as before. Built against Hermes v0.23.1.

The setup command sets it up. By hand, for each profile:

1. Download the plugin from the server's page (`/hermes/plugin.zip`) and unzip it in the profile's plugins folder
   (`$HERMES_HOME/plugins/`). Its files go in `plugins/messagelink/`.
2. Enable it for that profile: `hermes -p <profile> plugins enable messagelink`.
3. Add the connection code to the profile's `.env` (with `ml-report` too, the same code under both names). The home
   channel is `home` unless `MESSAGELINK_HOME_CHANNEL` says otherwise.

   ```
   MESSAGELINK_CODE=mlrc1....
   ```

4. Keep Hermes's restart notices out of Message Link: add this to the profile's `config.yaml`.

   ```yaml
   messagelink:
     gateway_restart_notification: false
   ```

5. Restart Hermes once, so it loads the plugin with these settings: `hermes gateway restart --all`, or restart its
   container.

Then:

```
hermes send --to messagelink "The backup finished."
hermes cron create "every 1d at 09:00" "Check the backups" --deliver messagelink
```

What to know:

- A profile whose terminal runs over SSH should use the plugin. `ml-report` runs where the terminal runs; the plugin
  runs inside Hermes.
- A scheduled job made from a chat in Message Link has no chat of its own to deliver to, so Hermes delivers it to the
  first platform with a home channel. With no other messaging platform set up, that is Message Link.
- Hermes adds a header and footer to a scheduled job's result. To deliver the result alone, set
  `cron.wrap_response: false` in the profile's `config.yaml`. It applies to every platform.
- A message longer than one report (8,000 characters) is split at a line break or a space, into at most five reports.
  A message that needs more is refused whole, and nothing of it is sent. Hermes hands the plugin each message whole,
  and a scheduled job's long result isn't cut short.
- Nothing that may have arrived is sent again. Hermes gives a platform no id for each delivery, so the server can't
  recognize a repeat, and the plugin never sends a report twice instead. When no answer comes back, or a later part
  of a long message fails, the plugin tells Hermes the message was partly delivered, so a scheduled job doesn't try
  its other way of sending. The job records it as an error, and the report may or may not be in Message Link.
- Hermes's restart notices ("Gateway online", restarting, shutting down) don't go to Message Link once
  `gateway_restart_notification: false` is in `config.yaml`.
  The plugin also turns them off when it starts, but Hermes rereads a profile's settings when its `.env` or
  `config.yaml` changes, and that fresh copy has them on again unless `config.yaml` says otherwise. Hermes sends a
  notice again until one succeeds, so the plugin also reports a notice, or a kanban notification sent to this home
  channel, as sent when no answer came back. Hermes's log says it may not have been delivered.
- A report refused with nothing stored, such as one over the limit of 6 a minute, may be tried once more by a
  scheduled job's other way of sending, which can't make a duplicate. The plugin itself never retries.
- Messages are text only. Files attached in Hermes are not sent.

## Without Python (unsupported)

curl can send a report if it pins the server's public key. The connection code doesn't carry that key's hash, so it has
to be worked out from the server's certificate by hand. This is not supported:

```
curl --pinnedpubkey "sha256//<SPKI hash>" --insecure -H "Authorization: Bearer $TOKEN" -H "Content-Type: text/plain; charset=utf-8" --data-binary @- https://<server>:8787/v1/reports
```

## License

MIT. See `LICENSE`.
