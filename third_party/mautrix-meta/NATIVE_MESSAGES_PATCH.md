# Pinned Messenger library source

This is the source of `go.mau.fi/mautrix-meta v0.2611.1-0.20261126163501-16724d697cdc` (upstream `main` at 16724d6,
2026-11-26), exported from the upstream repository. It remains AGPL-3.0; upstream `LICENSE` and `LICENSE.exceptions`
are retained, and `go.mod` and `go.sum` are upstream's own. The helpers' `go.mod` files replace that module with this
directory so fresh clones build the same patched source.

## Local changes

Limited to `pkg/messagix/syncManager.go`, `pkg/messagix/client.go`, `pkg/messagix/sync_concurrency_test.go`,
`pkg/messagix/httpclient/http.go`, `pkg/instameow/graphql.go` and `pkg/instameow/streamcontroller.go`:

- `httpclient.SendOnce(ctx)` makes `makeRequest` give up after its first attempt. Upstream otherwise re-posts a request
  up to five times after a lost answer or a 5xx, which for a send or a group creation can act twice. The Instagram
  helper marks every call that changes the account with it (sends, media, first message, group creation and naming,
  reactions, edits, unsends, read markers); reads keep upstream's retries. The same mark also stops upstream's one
  retry through a new proxy after a 429 (neither helper sets a proxy, so that retry never runs here anyway). instameow's
  one reload-and-retry after "please reload page" (`instameow/graphql.go`) is left as it is: that answer is Instagram
  refusing the request before acting on it, so the retry cannot act twice. The Instagram helper's `sendonce_test.go`
  proves both on the real client, offline; its control fails if upstream's first retry no longer comes within the
  test's 4 s budget.
- Cursor metadata and thread ranges use a library-owned mutex. Internal sync workers and callers use the same lock.
  Upstream's own thread-range generation counter (`threadRangeLock`) is separate and never takes this one.
- Network sync workers receive owned metadata snapshots, including an owned cursor string. Locks do not span network
  requests or event callbacks, which deliver the answers those requests await.
- Thread-range getters and state dumps return owned snapshots. Received transaction cursors do not alias table fields,
  including the resync after a sync reset on the live connection (`handleLiveSyncResets`).
- Synthetic regressions cover snapshot ownership and overlapping cursor/range updates, readers and JSON state snapshots.
- Two upstream page reloads are left out, because each is an Instagram read nobody asked for, outside the owner's read
  budget:
  - `checkAccountNotice` (upstream `0b3104d`, #476): after a "content unavailable" answer (1357031) upstream reloads the
    inbox page to look for an account notice. Here the answer is returned as the error it is.
  - upstream `90e5fa1` (#596): when the typing stream fails and the inbox page carried no logged-in viewer, upstream
    reloads the page. Here the typing stream only backs off.

## Syncing

2026-11-26, from 7211485 to 16724d6: 25 upstream commits touch `pkg/messagix` or `pkg/instameow`, all taken. The same
three-way merge; the one conflict was `httpclient/http.go`, where upstream's `changeProxyAfter` now takes the context
and the send-once check stays beside it. `go.mau.fi/util` and `maunium.net/go/mautrix` moved to upstream's pins in
both helpers with `go get`; whatsmeow did not move. `8f2831e` corrects `LSUpdateOrInsertThread` from column 24 on and
types `CannotReplyReason` as `any`, so the Messenger helper reads it as present or absent, as on the other thread row.
`f29eb73` makes `GenerateEpochID` random below the millisecond, as Messenger web does; nothing here depended on its
counter.

2026-11-22, from 4b3889e (2026-10-12) to 7211485: about 95 upstream commits touch `pkg/messagix` or `pkg/instameow`.
Every one was taken except the two reloads above. The earlier hand-picked fixes (#409, #411, #418, #420, #421, #433,
#437, #476 in part, and parts of `eb0b769`, `da314fc`, `f37e982`, `cea2e38`) are upstream's own code now. The sync was
a three-way merge: upstream 4b3889e as the base, this copy as one side and upstream 7211485 as the other; the conflicts
(`httpclient/http.go`, `httpclient/graphql.go`, `syncManager.go`, `useragent/useragent.go`) took upstream's side except
where a local change above applies.

`whatsmeow` moved with it to upstream's pin, 19d61352 (2026-11-16, also `tulir/whatsmeow` main that day). That adds
device-store upgrade 16 (`whatsmeow_chat_settings.wasa_root_secret_id`, which whatsmeow marks compatible with v8+), so
the Messenger helper's `deviceSchema()` rises by one.

To move the pin again: export upstream at the new commit, merge this copy's local changes onto it the same way, keep
upstream's `go.mod`, then `go get` the new pseudo-version (and the packages it newly imports) in both helpers. Never
run `go mod tidy` on the Messenger helper. The helper build scripts run these package tests through the helpers'
dependency graphs. Remove the replacement only after verifying upstream supplies equivalent safety.

Source: https://github.com/mautrix/meta/tree/16724d697cdc74868695a4ee78e814bb13ef5b77
