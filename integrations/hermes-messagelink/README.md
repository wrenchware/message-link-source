# Message Link reports for Hermes

`ml-report` lets a Hermes profile send a short report into its own conversation in Message Link: a background job that
finished, a scheduled script's result, or something that needs the owner. It is a Python 3 script (3.10 or later,
standard library only). `mlreport_client.py` holds the code it shares with the Hermes plugin. Tested with Hermes
v0.23.1.

## How it works

The Message Link server's page turns reports on for one Hermes profile and shows a connection code once. The code holds
the server's address, the fingerprint of its certificate and a token for that profile.

- `ml-report` connects over HTTPS and checks the server's certificate against the fingerprint before it sends anything.
- It posts the text to `/v1/reports` with the token as a bearer token. `GET /v1/reports/check` tests a code.
- It never follows a redirect, and it never retries a report.
- The server accepts reports only from the home network. A report is plain text, up to 8,000 characters.

Exit codes: 0 delivered (or already delivered), 1 refused or failed, 2 setup problem (the code is missing or malformed,
the certificate doesn't match, or the server can't be reached). The reason is printed on standard error. Neither the
code nor the token is ever printed.

## Set up

1. Put `ml-report` and `mlreport_client.py` in the profile's `scripts` folder (`$HERMES_HOME/scripts/`), and make
   `ml-report` executable (`chmod +x`).
2. Add the connection code to the profile's `.env`:

   ```
   MESSAGE_LINK_REPORTS=mlrc1....
   ```

3. Let commands see it. Hermes removes secrets from the commands it runs, so do one of these:
   - list it under `terminal.env_passthrough` in the profile's `config.yaml`:

     ```yaml
     terminal:
       env_passthrough:
         - MESSAGE_LINK_REPORTS
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

If the profile's terminal runs over SSH, use the Message Link plugin for Hermes instead, which keeps the code inside
Hermes.

## The Hermes plugin

The `messagelink` plugin lets Hermes itself deliver into Message Link: `hermes send --to messagelink "..."` and
scheduled jobs with `--deliver messagelink`. The code stays in the profile's settings, where commands can't see it. The
plugin only sends: chat with Hermes in Message Link as before. Built against Hermes v0.23.1.

Set it up for each profile:

1. Download the plugin from the server's page (`/hermes/plugin.zip`) and unzip it in the profile's plugins folder
   (`$HERMES_HOME/plugins/`). Its files go in `plugins/messagelink/`.
2. Enable it for that profile: `hermes -p <profile> plugins enable messagelink`.
3. Add the connection code and the home channel to the profile's `.env`:

   ```
   MESSAGELINK_CODE=mlrc1....
   MESSAGELINK_HOME_CHANNEL=home
   ```

4. Restart Hermes.

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
- Hermes's restart notices ("Gateway online", restarting, shutting down) don't go to Message Link: the plugin turns
  `gateway_restart_notification` off for this platform. Hermes sends a notice again until one succeeds, so the
  plugin also reports a notice, or a kanban notification sent to this home channel, as sent when no answer came back.
  Hermes's log says it may not have been delivered.
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
