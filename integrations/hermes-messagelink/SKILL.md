---
name: message-link-reports
description: Send the owner a report in Message Link when a long task finishes or something needs them.
version: 1.0.0
metadata:
  hermes:
    tags: [message-link, notifications]
    category: productivity
    requires_toolsets: [terminal]
required_environment_variables:
  - name: MESSAGE_LINK_REPORTS
    prompt: Message Link connection code
    help: Turn on reports for this profile on the Message Link server's page, then copy the connection code it shows.
    required_for: sending reports
---

# Message Link reports

`$HERMES_HOME/scripts/ml-report` sends a short text report into this profile's conversation in Message Link.

- `ml-report "text"` sends the text. With no text argument, it reads the text from standard input.
- `ml-report --key <id> "text"` sends at most once for that key, so a retried job doesn't report twice.
- `ml-report check` tests the setup.
- Exit code 0 means the report arrived. 1 means the server refused it or there was no answer, and 2 means the setup
  needs fixing. The reason is on standard error.

When a task will take more than a few minutes, start it in the background, reply that it has started, and make the
job's last step `ml-report` with the result. Use `ml-report` when something on the VM needs the owner. Don't put links
in a report unless the owner asked for one.

A report is at most 8,000 characters of plain text. Never put the connection code in a command line, a file or a report.
