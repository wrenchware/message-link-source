# Pinned Google Messages library source

This is `pkg/libgm` of `go.mau.fi/mautrix-gmessages v0.2611.1-0.20261126200815-6d8ba56a9462` (upstream `main` at
`6d8ba56`, 2026-11-26, 22 commits after the `v0.2611.0` tag), with that module's `go.mod`, `go.sum`, `LICENSE` and
`LICENSE.exceptions`, exported from the upstream repository at that commit. It remains AGPL-3.0. The bridge's
`pkg/connector` is left out because the helper does not import it. The helper's `go.mod` requires that version and
replaces the module with this directory so fresh clones build the same patched source.

Synced 2026-11-26 (before that 2026-11-22, at `ed5dc19`). Before 2026-11-22 the copy took upstream's `pkg/libgm`
changes without moving the pin (from `186284e` up to `d49e4d7`, then `b580a1a` and `ec50396`), because upstream's
`go.mod` also moves `maunium.net/go/mautrix` and `go.mau.fi/util`. That reason no longer holds: `pkg/libgm` imports
nothing from `maunium.net/go/mautrix`, so the pin follows upstream and the helper takes `go.mau.fi/util` from it (now a
pseudo-version after 0.11.1 whose three commits touch only `l10n`, which nothing here imports). Upstream's `SetTyping` now takes whether typing started or stopped; the helper
passes true, as it always sent. `PairedPhoneRegistered` (upstream's check that the paired phone is still on the Google
account) is in the copy, but the helper does not call it. The helper pairs from a fresh `AuthData` every time, so the
re-pair filter (`DestPhoneID`, `HasOtherGaiaPhone`, `CopyForRepair`) never applies to it.

When moving the pin, `diff -r` this `pkg/libgm` against upstream's at the pinned commit (clone with
`core.autocrlf false`): only the local changes below should differ.

Local changes:

- The added `pkg/libgm/contacts_page.go`: `ListContactsPage`, which sends `LIST_CONTACTS` with the request's three
  numbers chosen by the caller. Upstream's `ListContacts` always sends 1, 350, 50, and my phone answered exactly
  50 contacts of about 410 (2026-10-05). Go refuses `-overlay` for files in the module cache, so an added file needs this
  copy.
- A request to the phone is posted again after a 5xx only when its action is on a list of actions that change nothing
  (the added `pkg/libgm/relay_retry.go`; `http.go` takes the number of attempts and `session_handler.go`'s
  `postMessage`, upstream's one post for both kinds of request since `cdbe4a4`, passes it by action). Upstream posts
  every request up to three times while Google answers 5xx, with the same body. Every action, read or write, goes through the same relay endpoint, and a 5xx does not say whether the relay had already passed the
  request to the phone, so a repeated send could arrive twice, and so could a reaction, a delete, a read marker or a
  group made by `GET_OR_CREATE_CONVERSATION`. The app never resends an uncertain mutation, and the library must not
  either. It is a list of what may repeat, not of what may not, so an action missed or added upstream later is posted
  once:
  - reads, which ask the phone for something and change nothing: `LIST_CONVERSATIONS`, `LIST_MESSAGES`,
    `LIST_CONTACTS`, `LIST_TOP_CONTACTS`, `GET_CONVERSATION`, `GET_CONVERSATION_TYPE`, `GET_CONTACTS_THUMBNAIL`,
    `GET_PARTICIPANTS_THUMBNAIL`, `IS_BUGLE_DEFAULT`, and `GET_FULL_SIZE_IMAGE` (a full-size copy of a picture the phone
    already holds; asking twice makes the same copy);
  - connection upkeep, repeated as the identical request and reaching no conversation: `GET_UPDATES` (makes this
    session the one receiving updates; its request id is the session id), `NOTIFY_DITTO_ACTIVITY` (the ping) and
    `ACK_BROWSER_PRESENCE`. Keeping their repeats keeps upstream's tolerance of a passing 5xx at connect and in the
    pinger.

  Everything else is posted once: sends, replies, reactions, deletes, read markers, typing, settings, conversation
  updates, `GET_OR_CREATE_CONVERSATION`, group changes and the Google-account pairing actions. Requests that are not
  phone actions (acknowledgements, token refresh, the QR pairing and sign-in endpoints, the long poll) keep upstream's
  behaviour: acknowledgements and the token refresh are idempotent, pairing is started by the user, and the long poll
  never repeated. `pkg/libgm/relay_retry_test.go` posts each kind against a relay answering 503 and counts the attempts.
  Upstream's group rename (`RENAME_GROUP`, 2026-11-09) travels as `UPDATE_CONVERSATION` and is posted once.
- The added `pkg/libgm/nested_ids_test.go`, a test of upstream `b580a1a`: a conversation's and a message's empty
  top-level IDs are filled from `someKindOfGroupID` and `anotherMessageID`, which some phones now send alone (a
  cousin's phone paired through a Google account: every list answer came back with no conversation and every pushed
  message with no chat ID, while Messages for web worked, 2026-11-22).

The helper build script runs the `libgm` package tests through the helper's dependency graph. When moving the upstream
pin, copy the new version's `pkg/libgm` and module files here and keep `contacts_page.go`, `relay_retry.go` and its
test, `nested_ids_test.go`, and the attempts argument in `http.go` and in `session_handler.go`'s `postMessage`, or remove the replacement if
upstream gains equivalents. Check any action upstream added against the list above before adding it. Then move the
helper's `go.mod` require line, its hello `library` string (`main.go`, `main_test.go`) and the mautrix-gmessages
paragraph in `THIRD_PARTY_NOTICES.md`.

Source: https://github.com/mautrix/gmessages/tree/6d8ba56a9462
