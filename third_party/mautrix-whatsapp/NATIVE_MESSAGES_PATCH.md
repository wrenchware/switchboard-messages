# Pinned WhatsApp library source

This is the source of `go.mau.fi/mautrix-whatsapp v0.2610.0` (upstream tag `v0.2610.0`, commit 1067291), exported from
the upstream repository. Only `pkg/`, `go.mod`, `go.sum`, `LICENSE`, `LICENSE.exceptions`, `ROADMAP.md` and
`CHANGELOG.md` were exported; the bridge's `cmd/`, Docker and CI files were left out. It remains AGPL-3.0; upstream
`LICENSE` and `LICENSE.exceptions` are retained. It pins `go.mau.fi/whatsmeow v0.0.0-20261016130820-0b0a1cdac3e2`
(MPL-2.0), `maunium.net/go/mautrix v0.32.0` and `go.mau.fi/util v0.11.0`, with Go 1.27.2. These are newer than the
Messenger helper's pins; each helper keeps its own `go.mod`.

There are no local changes yet. The WhatsApp helper (`experiments/WhatsApp/helper`, `WHATSAPP.md` stage 1) will
replace the module with this directory, so fresh clones build the same source. List every change here when one is
made, with the reason, so that moving the pin can rebase it.

Source: https://github.com/mautrix/whatsapp/tree/1067291a89f4949887b328104005abe4f7163ddf
