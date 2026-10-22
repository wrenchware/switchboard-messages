package main

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"sync"
	"time"
)

func lifecycleMethod(method string) bool {
	switch method {
	case "hello", "login", "connect", "disconnect", "session", "snapshot", "pair.start", "pair.finish", "pair.qrStart", "reauthenticate", "ui.encrypted":
		return true
	}
	return false
}

func mutationMethod(method string) bool {
	switch method {
	case "ui.sendText", "ui.sendMedia", "ui.mediaStage", "ui.react", "ui.markRead", "ui.typing", "ui.updateChat", "ui.deleteChat", "ui.deleteMessage", "ui.editMessage", "ui.startDirect", "ui.startGroup", "probe.receipt":
		return true
	}
	return false
}

func requestBudget(method string) time.Duration {
	// Google advertises the phone emoji challenge for five minutes. Keep a little
	// transport headroom instead of aborting the helper's finish request at the
	// generic two-minute deadline while the user is still choosing on the phone.
	if method == "pair.finish" {
		return 6 * time.Minute
	}
	// Six codes of thirty seconds (qr_pairing.go), with a little headroom. Not a lifecycle method: it must be cancellable
	// while it waits, which a request the reader runs itself cannot be.
	if method == "pair.qrWait" {
		return 4 * time.Minute
	}
	if method == "ui.typing" {
		return 3 * time.Second
	}
	return 2 * time.Minute
}

func dispatch(in io.Reader, out *output, call func(context.Context, request) (any, string)) {
	ctx, stop := context.WithCancel(context.Background())
	defer stop()
	var workers sync.WaitGroup
	var pending sync.Map // request ID -> cancel; includes queued mutations
	slots := make(chan struct{}, 64)
	tail := make(chan struct{})
	close(tail)
	scanner := bufio.NewScanner(in)
	scanner.Buffer(make([]byte, 4096), 1024*1024)
	run := func(deadline context.Context, r request) {
		var result any
		var code string
		if deadline.Err() == nil {
			result, code = call(deadline, r)
		} else {
			code = "request_cancelled"
		}
		out.write(response{ID: r.ID, Result: result, Error: code})
	}
	for scanner.Scan() {
		var cancellation struct {
			Cancel int `json:"cancel"`
		}
		if json.Unmarshal(scanner.Bytes(), &cancellation) == nil && cancellation.Cancel > 0 {
			if cancel, ok := pending.Load(cancellation.Cancel); ok {
				cancel.(context.CancelFunc)()
			}
			continue
		}
		var r request
		if json.Unmarshal(scanner.Bytes(), &r) != nil || r.ID <= 0 {
			out.write(response{Error: "invalid_request"})
			continue
		}
		if lifecycleMethod(r.Method) {
			workers.Wait()
			deadline, cancel := context.WithTimeout(ctx, requestBudget(r.Method))
			run(deadline, r)
			cancel()
			continue
		}
		slots <- struct{}{}
		budget := requestBudget(r.Method)
		deadline, cancel := context.WithTimeout(ctx, budget)
		pending.Store(r.ID, cancel)
		workers.Add(1)
		previous := tail
		var finished chan struct{}
		if mutationMethod(r.Method) {
			finished = make(chan struct{})
			tail = finished
		}
		go func(r request, previous, finished chan struct{}) {
			defer workers.Done()
			defer cancel()
			defer pending.Delete(r.ID)
			defer func() { <-slots }()
			if finished != nil {
				defer close(finished)
				<-previous
			}
			run(deadline, r)
		}(r, previous, finished)
	}
	// EOF is teardown, not permission to finish queued contact-visible actions.
	stop()
	workers.Wait()
}
