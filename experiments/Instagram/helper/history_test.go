package main

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/util/jsontime"
)

type fakeHistoryClient struct {
	fakeReadClient
	initial                    *slidetypes.Mailbox
	pages                      map[string]*slidetypes.MailboxPage
	details                    map[string]*slidetypes.ThreadInfo
	messages                   map[string]*slidetypes.SlideMessages
	mailboxCalls, historyCalls int
	// GetMailbox, the inbox's first page, counted apart from PaginateMailbox.
	firstCalls       int
	requestedFolders map[string]bool
	mailboxErr       error
}

func (c *fakeHistoryClient) GetOwnFBID() int64 { return 1234 }
func (c *fakeHistoryClient) GetMailbox(context.Context) (*slidetypes.MailboxResponse, error) {
	c.firstCalls++
	if c.mailboxErr != nil {
		return nil, c.mailboxErr
	}
	return &slidetypes.MailboxResponse{Mailbox: c.initial}, nil
}
func (c *fakeHistoryClient) PaginateMailbox(_ context.Context, r *slidetypes.PaginateMailboxRequest) (*slidetypes.PaginateMailboxResponse, error) {
	c.mailboxCalls++
	if c.requestedFolders == nil {
		c.requestedFolders = map[string]bool{}
	}
	c.requestedFolders[r.Folder] = true
	if r.ViewerFBID != 1234 {
		panic("wrong viewer identity")
	}
	page := c.pages[r.Folder+":"+r.Cursor]
	if page == nil {
		page = &slidetypes.MailboxPage{}
	}
	return &slidetypes.PaginateMailboxResponse{Mailbox: page}, nil
}
func (c *fakeHistoryClient) GetThread(_ context.Context, r *slidetypes.GetThreadInfoRequest) (*slidetypes.ThreadInfoResponse, error) {
	return &slidetypes.ThreadInfoResponse{ThreadInfo: slidetypes.WrappedThreadInfo{AsIGDirectThread: c.details[r.ThreadFBID]}}, nil
}
func (c *fakeHistoryClient) PaginateMessages(_ context.Context, r *slidetypes.PaginateMessagesRequest) (*slidetypes.PaginateMessagesResponse, error) {
	c.historyCalls++
	if r.BeforeCursor != nil || r.LastN != nil || r.NewerThanMessageID != nil || r.OlderThanMessageID != nil || r.FirstN != 20 {
		panic("incorrect history direction")
	}
	return &slidetypes.PaginateMessagesResponse{ThreadInfo: slidetypes.AsThread[slidetypes.MessagesOnlyThread]{AsIGDirectThread: &slidetypes.MessagesOnlyThread{ID: r.ThreadID, Messages: c.messages[*r.AfterCursor]}}}, nil
}

func threadPage(threads ...*slidetypes.ThreadInfo) slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]] {
	page := slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]]{}
	for _, thread := range threads {
		page.Edges = append(page.Edges, slidetypes.Node[slidetypes.WrappedThreadInfo]{Node: slidetypes.WrappedThreadInfo{AsIGDirectThread: thread}})
	}
	return page
}

func messagePage(threadID string, ids ...string) *slidetypes.SlideMessages {
	page := &slidetypes.SlideMessages{}
	for i, id := range ids {
		page.Edges = append(page.Edges, slidetypes.Node[*slidetypes.Message]{Node: &slidetypes.Message{ID: id, MessageID: id, ThreadFBID: threadID, SenderFBID: 456, TextBody: "synthetic-private-body", TimestampMS: jsontime.UnixMilliString(jsontime.UM(time.Now().Add(-time.Duration(i+1) * 24 * time.Hour)))}})
	}
	return page
}

func historyFixture() (*readOnlyAdapter, *fakeHistoryClient) {
	a, _, _ := testReadAdapter()
	values := fakeCookies()
	values["ds_user_id"] = "123"
	a.jar, _ = readCookies(values)
	a.key = accountKey("123")
	one := &slidetypes.ThreadInfo{ID: "thread-one", ThreadFBID: "thread-one", ThreadKey: 456, ThreadID: "long-private-id", ThreadTitle: "synthetic-private-title", ViewerID: "123",
		Viewer: &slidetypes.User{ID: "123", InteropMessagingUserFBID: 1234}, Users: []*slidetypes.User{{ID: "private-participant", InteropMessagingUserFBID: 456}}, SlideMessages: messagePage("thread-one", "m2", "m1")}
	one.SlideMessages.PageInfo = slidetypes.PageInfo{HasNextPage: true, EndCursor: "older"}
	two := &slidetypes.ThreadInfo{ID: "789", ThreadFBID: "789", ThreadKey: 789, ThreadID: "long-group", IsGroup: true, ViewerID: "123", Viewer: one.Viewer, Users: one.Users, SlideMessages: messagePage("789", "g1")}
	three := &slidetypes.ThreadInfo{ID: "self", ThreadFBID: "self", ThreadKey: 1234, ThreadID: "long-self", ViewerID: "123", Viewer: one.Viewer, SlideMessages: messagePage("self")}
	initial := &slidetypes.Mailbox{UQSeqID: 40, ThreadsByFolder: threadPage(one, two)}
	initial.ThreadsByFolder.PageInfo = slidetypes.PageInfo{HasNextPage: true, EndCursor: "page2"}
	older := messagePage(one.ID, "m1", "m0")
	older.Edges[1].Node.TimestampMS = jsontime.UnixMilliString(jsontime.UM(time.Now().Add(-3 * 24 * time.Hour)))
	client := &fakeHistoryClient{initial: initial, pages: map[string]*slidetypes.MailboxPage{"INBOX:page2": {ThreadsByFolder: threadPage(one, three)}},
		details: map[string]*slidetypes.ThreadInfo{one.ID: one, two.ID: two, three.ID: three}, messages: map[string]*slidetypes.SlideMessages{"older": older}}
	a.client = client
	return a, client
}

func TestHistoryScanBoundariesIdentityAndPrivacy(t *testing.T) {
	a, c := historyFixture()
	result, code := a.scan(context.Background(), json.RawMessage(`{"mailboxPages":3,"detailThreads":3,"historyThreads":2,"historyPages":3}`))
	if code != "" {
		t.Fatal(code)
	}
	r := result.(scanReport)
	if r.Code != "ok" || r.Threads != 3 || r.Details != 3 || r.DuplicateThreads != 1 || !r.InboxExhausted || r.RequestFoldersExhausted != 2 {
		t.Fatalf("bad scan counts: %+v", r)
	}
	if r.Messages != 4 || r.HistoryExhausted != 2 || r.DuplicateMessages != 1 || r.HistoryOrderViolations != 0 || c.historyCalls != 1 {
		t.Fatalf("bad history counts: %+v", r)
	}
	if r.GroupKeyMatchesID != 1 || r.DirectKeyMatchesParticipant != 1 || r.SelfThreadCandidates != 1 || r.ViewerObjectMatchesAccount != 3 || r.Participants != 2 {
		t.Fatal("identity mapping failed")
	}
	if r.ThreadIdentityMismatches != 0 || r.CanonicalCollisions != 0 || r.MessageThreadMismatches != 0 || r.EncryptionVerified {
		t.Fatal("incorrect evidence")
	}
	raw, _ := json.Marshal(result)
	for _, secret := range []string{"thread-one", "private-participant", "synthetic-private-title", "synthetic-private-body", "long-private-id"} {
		if strings.Contains(string(raw), secret) {
			t.Fatal("private data escaped")
		}
	}
	if c.seq != 40 || a.probe.baselineSeq != 40 {
		t.Fatal("snapshot cursor not used for catchup")
	}
	if qualifiedID("a", "chat", "x:y") == qualifiedID("b", "chat", "x:y") || qualifiedID("a", "chat", "x") == qualifiedID("a", "user", "x") {
		t.Fatal("provider identities collide")
	}
}

func TestHistoryStopsOnCursorStallAndPageCap(t *testing.T) {
	a, c := historyFixture()
	c.pages["INBOX:page2"].ThreadsByFolder.PageInfo = slidetypes.PageInfo{HasNextPage: true, EndCursor: "page2"}
	result, code := a.scan(context.Background(), json.RawMessage(`{"mailboxPages":5,"detailThreads":1,"historyThreads":1,"historyPages":1}`))
	if code != "" {
		t.Fatal(code)
	}
	r := result.(scanReport)
	if r.CursorStalls != 1 || r.InboxExhausted || r.HistoryCapped != 1 || r.HistoryExhausted != 0 || c.historyCalls != 0 || c.mailboxCalls != 3 {
		t.Fatalf("paging was not bounded: %+v", r)
	}
}

func TestHistoryWrongThreadAndMissingMessagesAreNotExhaustion(t *testing.T) {
	a, c := historyFixture()
	c.details["thread-one"].SlideMessages = nil
	changed := *c.details["789"]
	changed.ThreadKey = 111
	c.details["789"] = &changed
	result, code := a.scan(context.Background(), nil)
	if code != "" {
		t.Fatal(code)
	}
	r := result.(scanReport)
	if r.HistoryFailures != 1 || r.ThreadIdentityMismatches != 1 {
		t.Fatal("missing message page treated as exhaustion")
	}
}

func TestRealtimeReconciliationAndDuplicateGuard(t *testing.T) {
	a, c := historyFixture()
	if _, code := a.scan(context.Background(), nil); code != "" {
		t.Fatal(code)
	}
	p := a.probe
	p.apply(&slidetypes.Delta{UQSeqID: 41, ThreadIGID: "thread-one", Data: &slidetypes.NewMessageEvent{Message: &slidetypes.Message{ID: "m2", TextBody: "private-event-body"}}})
	p.apply(&slidetypes.Delta{UQSeqID: 41, ThreadIGID: "thread-one", Data: &slidetypes.NewMessageEvent{Message: &slidetypes.Message{ID: "m2", TextBody: "private-event-body"}}})
	p.apply(&slidetypes.Delta{UQSeqID: 41, ThreadIGID: "789", Data: &slidetypes.EditMessageEvent{MessageID: "g1", TextBody: "synthetic-private-body"}})
	p.apply(&slidetypes.Delta{UQSeqID: 43, ThreadIGID: "789", Data: slidetypes.UnknownEvent{"private": "never output"}})
	a.stop = func() {}
	a.done = make(chan struct{})
	close(a.done)
	c.initial.UQSeqID = 43
	result, code := a.observe(context.Background(), json.RawMessage(`{"seconds":0}`))
	if code != "" {
		t.Fatal(code)
	}
	r := result.(realtimeReport)
	if r.DeltaEvents != 4 || r.DuplicateOrOldDeltas != 1 || r.UnknownEvents != 1 || r.EventMessagesChecked != 2 || r.EventMessagesMatched != 2 || r.StableThreads != 2 || !r.SnapshotSequenceAdvanced {
		t.Fatalf("bad convergence counts: %+v", r)
	}
}

func TestRealtimeOutOfOrderDoesNotOverwriteNewerMessage(t *testing.T) {
	p := &historyProbe{baselineSeq: 40, lastSeq: 40, changes: map[string]map[string]messageExpectation{}}
	p.apply(&slidetypes.Delta{UQSeqID: 44, ThreadIGID: "thread", Data: &slidetypes.EditMessageEvent{MessageID: "message", TextBody: "latest-synthetic"}})
	p.apply(&slidetypes.Delta{UQSeqID: 43, ThreadIGID: "thread", Data: &slidetypes.DeleteMessageEvent{MessageID: "message"}})
	p.apply(&slidetypes.Delta{UQSeqID: 40, ThreadIGID: "thread", Data: &slidetypes.DeleteMessageEvent{MessageID: "message"}})
	p.apply(&slidetypes.Delta{UQSeqID: 45, Data: slidetypes.UnknownEvent{"synthetic": "private"}})
	if !p.changes["thread"]["message"].present || p.changes["thread"]["message"].sequence != 44 || p.realtime.OutOfOrderDeltas != 1 || p.realtime.DuplicateOrOldDeltas != 1 || p.realtime.UnknownEvents != 1 {
		t.Fatal("sequence/replay policy lost the newest expectation")
	}
}

// The probes are the diagnostics' and nobody else's. In --app mode a scan would page the live account's mailbox and
// history for nothing the application asks for, so the method does not exist there; in --read-only mode it still runs.
func TestProbesExistOnlyInTheDiagnosticsMode(t *testing.T) {
	a, c := historyFixture()
	a.canMutate = true
	for _, method := range []string{"probe.scan", "probe.observe"} {
		if _, code := a.call(context.Background(), request{Method: method}); code != "unknown_method" {
			t.Fatalf("%s answered %q in the application's mode", method, code)
		}
	}
	if c.firstCalls != 0 || c.mailboxCalls != 0 || c.historyCalls != 0 || a.probe != nil {
		t.Fatal("a probe refused in the application's mode reached the client")
	}
	a.canMutate = false
	if _, code := a.call(context.Background(), request{Method: "probe.scan"}); code != "" || c.firstCalls != 1 {
		t.Fatalf("the diagnostics' scan no longer runs: %q", code)
	}
}
