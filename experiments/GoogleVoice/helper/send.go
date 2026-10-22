package main

import (
	"context"
	"errors"
	"net/http"
	"sync"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Voice splits long texts itself; this only keeps an unreasonable request from leaving the app.
const maxTextBytes = 10000

type sendAttemptKey struct{}

// What happened to one send's request. libgv retries a request after a network error or a server error, which for a
// send could deliver the text twice. A send is asked exactly once: after that answer the retry is stopped, and the
// outcome is decided from what the one request returned.
type sendAttempt struct {
	mu       sync.Mutex
	attempts int
	status   int
	failed   bool
	stop     context.CancelFunc
}

var errSendNotRepeated = errors.New("a send is not repeated")

type sendGuard struct{ base http.RoundTripper }

func (g *sendGuard) RoundTrip(req *http.Request) (*http.Response, error) {
	base := g.base
	if base == nil {
		base = http.DefaultTransport
	}
	attempt, guarded := req.Context().Value(sendAttemptKey{}).(*sendAttempt)
	// Other requests, and redirects within the one send, pass as they are.
	if !guarded || req.Response != nil {
		return base.RoundTrip(req)
	}
	attempt.mu.Lock()
	attempt.attempts++
	first := attempt.attempts == 1
	attempt.mu.Unlock()
	if !first {
		attempt.stop()
		return nil, errSendNotRepeated
	}
	resp, err := base.RoundTrip(req)
	attempt.mu.Lock()
	if err != nil {
		attempt.failed = true
	} else {
		attempt.status = resp.StatusCode
	}
	attempt.mu.Unlock()
	// The answers libgv would retry: end the send here instead.
	if err != nil || resp.StatusCode >= 500 {
		attempt.stop()
	}
	return resp, err
}

// Sends a text, without Google's optional anti-abuse signature (the user's choice, as a hosted upstream bridge sends).
// The text is never logged.
func (a *adapter) sendText(ctx context.Context, client *libgv.Client, chatID, text string) (any, string) {
	return a.sendRequest(ctx, client, &gvproto.ReqSendSMS{ThreadID: chatID, Text: text})
}

// One message, text or photo, sent exactly once with a new transaction id.
func (a *adapter) sendRequest(ctx context.Context, client *libgv.Client, req *gvproto.ReqSendSMS) (any, string) {
	ctx, stop := context.WithCancel(ctx)
	defer stop()
	attempt := &sendAttempt{stop: stop}
	ctx = context.WithValue(ctx, sendAttemptKey{}, attempt)
	send := a.sendSMS
	if send == nil {
		send = func(ctx context.Context, client *libgv.Client, req *gvproto.ReqSendSMS) (*gvproto.RespSendSMS, error) {
			return client.SendMessage(ctx, req)
		}
	}
	req.TransactionID = &gvproto.ReqSendSMS_WrappedTxnID{ID: libgv.GenerateTransactionID()}
	resp, err := send(ctx, client, req)
	attempt.mu.Lock()
	attempts, status, failed := attempt.attempts, attempt.status, attempt.failed
	attempt.mu.Unlock()
	if err == nil && resp.GetThreadItemID() != "" {
		a.diagnose(map[string]any{"send": "accepted", "status": status})
		// The sent message arrives through the ordinary refresh, like any other.
		a.wake()
		return map[string]any{"id": resp.GetThreadItemID(), "timestamp": resp.GetTimestampMS()}, ""
	}
	outcome := sendOutcome(err, attempts, status, failed)
	a.diagnose(map[string]any{"send": outcome, "status": status})
	return nil, outcome
}

// Only a request that never left, or one Google answered with a client error, is certainly not sent. A network error,
// a server error, or an accepted answer without a message id may have sent it.
func sendOutcome(err error, attempts, status int, failed bool) string {
	switch {
	case err != nil && attempts == 0 && !failed && errors.Is(err, context.Canceled):
		return "request_cancelled"
	case err != nil && attempts == 0 && !failed:
		return "send_rejected"
	case !failed && (status == http.StatusUnauthorized || status == http.StatusForbidden):
		return "auth_required"
	case !failed && status >= 400 && status < 500:
		return "send_rejected"
	default:
		return "send_failed"
	}
}
