# Pinned Messenger library source

This is the source of `go.mau.fi/mautrix-meta v0.2609.1-0.20261012222411-4b3889e607c0` (upstream `main` at 4b3889e), exported from the upstream repository.
It remains AGPL-3.0; upstream `LICENSE` and `LICENSE.exceptions` are retained. No dependency version is upgraded.
The helper's `go.mod` replaces that module with this directory so fresh clones build the same patched source.

Local changes are limited to `pkg/messagix/syncManager.go`, `pkg/messagix/client.go`,
`pkg/messagix/sync_concurrency_test.go` and `pkg/messagix/httpclient/http.go`:

- `httpclient.SendOnce(ctx)` makes `makeRequest` give up after its first attempt. Upstream otherwise re-posts a request
  up to five times after a lost answer or a 5xx, which for a send or a group creation can act twice. The Instagram
  helper marks every call that changes the account with it (sends, media, first message, group creation and naming,
  reactions, edits, unsends, read markers); reads keep upstream's retries. instameow's one reload-and-retry after
  "please reload page" (`instameow/graphql.go`) is left as it is: that answer is Instagram refusing the request before
  acting on it, so the retry cannot act twice. The Instagram helper's
  `sendonce_test.go` proves both on the real client, offline; its control fails if upstream's first retry no longer
  comes within the test's 4 s budget.

- Cursor metadata and thread ranges use a library-owned mutex. Internal sync workers and callers use the same lock.
- Network sync workers receive owned metadata snapshots, including an owned cursor string. Locks do not span network
  requests or event callbacks, which deliver the answers those requests await.
- Thread-range getters and state dumps return owned snapshots. Received transaction cursors do not alias table fields.
- Synthetic regressions cover snapshot ownership and overlapping cursor/range updates, readers and JSON state snapshots.

Upstream fixes taken since, without moving the pin (upstream's later `go.mod` moves mautrix, util and whatsmeow and adds
the encrypted-history dependencies), each applied as upstream wrote it to the library packages only:

- `cad8fd4` (#411) the typing stream reconnects when it closes;
- `51050ce` (#420) native DGW streams take the current HTTP client instead of one that may be dead;
- `04ee2cd` (#418) DGW writes go through one write loop, one-off streams wait 30 s for an answer, and a cancelled
  request no longer closes the socket;
- `d779029` (#421) a sync reset arriving on the live connection resyncs that database (`handleLiveSyncResets`). Here its
  cursor is copied rather than pointing into the received table, as above;
- `b0f22c4` (#437) Messenger media uploads use their own client with a 5-minute timeout instead of the 20 s answer
  timeout. `graphql.go` was merged by hand, and `makeRequest` keeps the `SendOnce` check;
- `eb0b769` (lightspeed part only) a malformed `STORE_ARRAY` step no longer ends the process with `os.Exit(1)`;
- `da314fc` (instameow `graphql.go` part) the reload after "please reload page" is not cancelled with its request;
- `f37e982` (useragent part) Messenger Android 584.1.0.61.98;
- `cea2e38` (instameow part) `LoadIndex` returns `ErrMailboxMissing` instead of dereferencing a missing mailbox;
- `a29b2bc` (#409) the cookie map's JSON encoding and decoding take its lock, and the Instagram claim is read and written
  through `GetWWWClaim` and `SetWWWClaim`;
- `0b3104d` (`client.go` part, #476) a failed page reload keeps the working configs, and a suspended account is reported
  like the other refusals. Its `checkAccountNotice` is left out on purpose: it reloads the page on its own after a
  "content unavailable" answer, an unprompted Instagram read the owner has not agreed to. So are its GraphQL error log
  fields, and the redirect path it adds to error text;
- `5d72315` (the `updateThreadRanges` part, #433) a thread list range saying there is nothing older ends the paging.
  The rest of that commit waits on ranges for the bridge's own backfill, which does not run here.

When moving the pin these are already upstream and need no rebase.

The helper build script runs these package tests through the helper's existing dependency graph. When moving the
upstream pin, rebase these changes or remove the replacement only after verifying upstream supplies equivalent safety.

Source: https://github.com/mautrix/meta/tree/4b3889e607c0f673cd3762fb6cbbf74a11e87794
