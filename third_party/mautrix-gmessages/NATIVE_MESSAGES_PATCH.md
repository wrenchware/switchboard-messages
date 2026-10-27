# Pinned Google Messages library source

This is `pkg/libgm` of `go.mau.fi/mautrix-gmessages v0.2609.1-0.20261010212621-186284eea622`, with that module's
`go.mod`, `go.sum`, `LICENSE` and `LICENSE.exceptions`, exported from the upstream repository at that commit. It remains AGPL-3.0.
The bridge's `pkg/connector` is left out because the helper does not import it. No dependency version is upgraded. The
helper's `go.mod` replaces the module with this directory so fresh clones build the same patched source.

Since then `pkg/libgm` carries every upstream change to it up to `d49e4d7` (2026-10-26), taken without moving the pin,
because upstream's later `go.mod` also moves `maunium.net/go/mautrix` to 0.32 and `go.mau.fi/util` to 0.11: the long
poll's stored connection held in an atomic pointer and read with a 32 KiB buffer, the cookies locked while `AuthData` is
marshalled, a context on media upload and download, `HTTPError` for media answers, location messages in the protocol,
and the re-pair changes (`DestPhoneID`, `HasOtherGaiaPhone`, `CopyForRepair`). The helper pairs from a fresh `AuthData`
every time, so the re-pair filter never applies to it. When moving the pin, compare `pkg/libgm` with upstream at that
commit and only the local changes below should remain.

Local changes:

- The added `pkg/libgm/contacts_page.go`: `ListContactsPage`, which sends `LIST_CONTACTS` with the request's three
  numbers chosen by the caller. Upstream's `ListContacts` always sends 1, 350, 50, and my phone answered exactly
  50 contacts of about 410 (2026-10-05). Go refuses `-overlay` for files in the module cache, so an added file needs this
  copy.
- A request to the phone is posted again after a 5xx only when its action is on a list of actions that change nothing
  (the added `pkg/libgm/relay_retry.go`; `http.go` takes the number of attempts and `session_handler.go` passes it by
  action). Upstream posts every request up to three times while Google answers 5xx, with the same body. Every action,
  read or write, goes through the same relay endpoint, and a 5xx does not say whether the relay had already passed the
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

The helper build script runs the `libgm` package tests through the helper's dependency graph. When moving the upstream
pin, copy the new version's `pkg/libgm` and module files here and keep `contacts_page.go`, `relay_retry.go` and its
test, and the two call sites in `http.go` and `session_handler.go`, or remove the replacement if upstream gains
equivalents. Check any action upstream added against the list above before adding it.

Source: https://github.com/mautrix/gmessages/tree/186284eea622
