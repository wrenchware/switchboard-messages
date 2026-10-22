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

The helper build script runs these package tests through the helper's existing dependency graph. When moving the
upstream pin, rebase these changes or remove the replacement only after verifying upstream supplies equivalent safety.

Source: https://github.com/mautrix/meta/tree/4b3889e607c0f673cd3762fb6cbbf74a11e87794
