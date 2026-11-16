# Message Link reports for Hermes

`ml-report` lets a Hermes profile send a short report into its own conversation in Message Link: a background job that
finished, a scheduled script's result, or something that needs the owner. It is a Python 3 script (3.10 or later,
standard library only). `mlreport_client.py` holds the code it shares with the Hermes plugin. Tested with Hermes
v0.23.1.

## How it works

Each Hermes profile has its own connection code, `MESSAGELINK_CODE` in the profile's `.env`, which `ml-report` and the
plugin both use. The code holds the server's address, the fingerprint of its certificate and a token for that profile.
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
   `MESSAGELINK_CODE` in its `.env`, enables the plugin, and adds the two settings below to `config.yaml`, keeping its
   comments. If `config.yaml` is laid out in a way it can't edit with certainty, it changes nothing and prints the
   lines to add by hand.
4. It checks the new code once and ends with "Reports are on for" and the profile's name.

Hermes reads a profile's changed `.env` and `config.yaml` by itself within about 30 seconds. The `default` profile is
the exception: restart Hermes once. Run the command again, from **New code…**, to replace the code; nothing else
changes.

If Hermes can't reach the server, the command says so. With Hermes and Message Link in Docker on the same computer,
put both containers on one user-defined Docker network (not the default bridge): a connection through a published
port arrives from Docker's own address, which the server refuses.

## Set up by hand

The command's page has **Set up by hand instead**, which shows a connection code once.

1. Put `ml-report` and `mlreport_client.py` in the profile's `scripts` folder (`$HERMES_HOME/scripts/`), and make
   `ml-report` executable (`chmod +x`).
2. Add the connection code to the profile's `.env`:

   ```
   MESSAGELINK_CODE=mlrc1....
   ```

   `ml-report` still reads the older name, `MESSAGE_LINK_REPORTS`, when `MESSAGELINK_CODE` isn't set.

3. Let commands see it. Hermes removes secrets from the commands it runs, so do one of these:
   - list it under `terminal.env_passthrough` in the profile's `config.yaml`:

     ```yaml
     terminal:
       env_passthrough:
         - MESSAGELINK_CODE
     ```

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
from the process environment. (Commands see it only because `ml-report` needs it: `terminal.env_passthrough`.) The
plugin only sends: chat with Hermes in Message Link as before. Built against Hermes v0.23.1.

The setup command sets it up. By hand, for each profile:

1. Download the plugin from the server's page (`/hermes/plugin.zip`) and unzip it in the profile's plugins folder
   (`$HERMES_HOME/plugins/`). Its files go in `plugins/messagelink/`.
2. Enable it for that profile: `hermes -p <profile> plugins enable messagelink`.
3. Add the connection code to the profile's `.env`. The home channel is `home` unless
   `MESSAGELINK_HOME_CHANNEL` says otherwise.

   ```
   MESSAGELINK_CODE=mlrc1....
   ```

4. Keep Hermes's restart notices out of Message Link: add this to the profile's `config.yaml`.

   ```yaml
   messagelink:
     gateway_restart_notification: false
   ```

Hermes reads a profile's changed `.env` and `config.yaml` by itself within about 30 seconds, so there's nothing to
restart. The `default` profile is the exception: it reads its `.env` only when Hermes starts, so restart Hermes once.

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
