package main

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"sort"
	"strconv"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
)

// Only existing read queries. No profile lookup, mutation, media download, or
// mark-read validation is needed to inspect a thread and its history.
type historyClient interface {
	GetOwnFBID() int64
	GetMailbox(context.Context) (*slidetypes.MailboxResponse, error)
	PaginateMailbox(context.Context, *slidetypes.PaginateMailboxRequest) (*slidetypes.PaginateMailboxResponse, error)
	GetThread(context.Context, *slidetypes.GetThreadInfoRequest) (*slidetypes.ThreadInfoResponse, error)
	PaginateMessages(context.Context, *slidetypes.PaginateMessagesRequest) (*slidetypes.PaginateMessagesResponse, error)
}

type scanLimits struct {
	MailboxPages   int `json:"mailboxPages"`
	DetailThreads  int `json:"detailThreads"`
	HistoryThreads int `json:"historyThreads"`
	HistoryPages   int `json:"historyPages"`
}

type scanReport struct {
	MailboxPages                    int    `json:"mailboxPages"`
	Threads                         int    `json:"threads"`
	DuplicateThreads                int    `json:"duplicateThreads"`
	UnsupportedWrappers             int    `json:"unsupportedWrappers"`
	InboxExhausted                  bool   `json:"inboxExhausted"`
	RequestFoldersExhausted         int    `json:"requestFoldersExhausted"`
	RequestFolderFailures           int    `json:"requestFolderFailures"`
	PendingThreads                  int    `json:"pendingThreads"`
	SpamThreads                     int    `json:"spamThreads"`
	Details                         int    `json:"details"`
	DetailFailures                  int    `json:"detailFailures"`
	DirectThreads                   int    `json:"directThreads"`
	GroupThreads                    int    `json:"groupThreads"`
	SelfThreadCandidates            int    `json:"selfThreadCandidates"`
	ThreadIdentityMismatches        int    `json:"threadIdentityMismatches"`
	ThreadFBIDMatchesID             int    `json:"threadFBIDMatchesID"`
	LongThreadIDs                   int    `json:"longThreadIDs"`
	GroupKeyMatchesID               int    `json:"groupKeyMatchesID"`
	DirectKeyMatchesParticipant     int    `json:"directKeyMatchesParticipant"`
	CanonicalCollisions             int    `json:"canonicalCollisions"`
	Participants                    int    `json:"participants"`
	ParticipantIdentityConflicts    int    `json:"participantIdentityConflicts"`
	ViewerObjectMatchesAccount      int    `json:"viewerObjectMatchesAccount"`
	ViewerIDMatchesIG               int    `json:"viewerIDMatchesIG"`
	ViewerIDMatchesFB               int    `json:"viewerIDMatchesFB"`
	HistoryThreads                  int    `json:"historyThreads"`
	HistoryPages                    int    `json:"historyPages"`
	Messages                        int    `json:"messages"`
	DuplicateMessages               int    `json:"duplicateMessages"`
	MissingMessageIDs               int    `json:"missingMessageIDs"`
	MessageThreadMismatches         int    `json:"messageThreadMismatches"`
	MessageIDMatchesID              int    `json:"messageIDMatchesID"`
	MessageSenderMatchesParticipant int    `json:"messageSenderMatchesParticipant"`
	OldestAgeDays                   int    `json:"oldestAgeDays"`
	MaxMessagesPerThread            int    `json:"maxMessagesPerThread"`
	HistoryExhausted                int    `json:"historyExhausted"`
	HistoryCapped                   int    `json:"historyCapped"`
	HistoryFailures                 int    `json:"historyFailures"`
	CursorStalls                    int    `json:"cursorStalls"`
	EmptyContinuingPages            int    `json:"emptyContinuingPages"`
	HistoryOrderViolations          int    `json:"historyOrderViolations"`
	NonzeroInputModes               int    `json:"nonzeroInputModes"`
	EncryptionVerified              bool   `json:"encryptionVerified"`
	Code                            string `json:"code"`
}

type realtimeReport struct {
	ObservedSeconds          int    `json:"observedSeconds"`
	DeltaEvents              int    `json:"deltaEvents"`
	DuplicateOrOldDeltas     int    `json:"duplicateOrOldDeltas"`
	OutOfOrderDeltas         int    `json:"outOfOrderDeltas"`
	UnknownEvents            int    `json:"unknownEvents"`
	ResnapshotEvents         int    `json:"resnapshotEvents"`
	ChangedThreads           int    `json:"changedThreads"`
	ComparedThreads          int    `json:"comparedThreads"`
	StableThreads            int    `json:"stableThreads"`
	ChangedWithoutDelta      int    `json:"changedWithoutDelta"`
	EventMessagesChecked     int    `json:"eventMessagesChecked"`
	EventMessagesMatched     int    `json:"eventMessagesMatched"`
	EventMessagesUnresolved  int    `json:"eventMessagesUnresolved"`
	SnapshotSequenceAdvanced bool   `json:"snapshotSequenceAdvanced"`
	Code                     string `json:"code"`
}

type messageExpectation struct {
	present  bool
	textHash *[32]byte
	sequence int64
}
type threadMetadata struct {
	key          int64
	longID, fbid string
	activity     int64
	unread       bool
}
type historyProbe struct {
	baseline    map[string]threadMetadata
	firstPage   map[string]threadMetadata
	changes     map[string]map[string]messageExpectation
	lastSeq     int64
	baselineSeq int64
	realtime    realtimeReport
	seen        map[[32]byte]bool
}

func qualifiedID(account, kind, raw string) string {
	return "instagram:" + account + ":" + kind + ":" + base64.RawURLEncoding.EncodeToString([]byte(raw))
}

func metadata(t *slidetypes.ThreadInfo) threadMetadata {
	return threadMetadata{t.ThreadKey, t.ThreadID, t.ThreadFBID, t.LastActivityTimestampMS.UnixMilli(), t.MarkedAsUnread}
}

func (a *readOnlyAdapter) scan(ctx context.Context, raw json.RawMessage) (any, string) {
	client, ok := a.client.(historyClient)
	if !ok || a.jar == nil {
		return nil, "login_required"
	}
	if a.stop != nil {
		return nil, "disconnect_required"
	}
	if client.GetOwnFBID() <= 0 {
		return nil, "identity_mismatch"
	}
	limits := scanLimits{10, 30, 5, 10}
	if len(raw) > 0 && json.Unmarshal(raw, &limits) != nil {
		return nil, "invalid_params"
	}
	if limits.MailboxPages < 1 || limits.MailboxPages > 20 || limits.DetailThreads < 1 || limits.DetailThreads > 120 || limits.HistoryThreads < 1 || limits.HistoryThreads > limits.DetailThreads || limits.HistoryPages < 1 || limits.HistoryPages > 50 {
		return nil, "invalid_params"
	}
	report := scanReport{Code: "ok"}
	mailbox, err := client.GetMailbox(ctx)
	if err != nil {
		return nil, failureCode(err)
	}
	if mailbox == nil || mailbox.Mailbox == nil {
		return nil, "invalid_response"
	}
	probe := &historyProbe{baseline: map[string]threadMetadata{}, firstPage: map[string]threadMetadata{}, changes: map[string]map[string]messageExpectation{}, baselineSeq: mailbox.Mailbox.UQSeqID, lastSeq: mailbox.Mailbox.UQSeqID}
	threads := map[string]*slidetypes.ThreadInfo{}
	order := []string{}
	add := func(page slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]], folder string, first bool) {
		for _, edge := range page.Edges {
			t := edge.Node.AsIGDirectThread
			if t == nil || t.ID == "" {
				report.UnsupportedWrappers++
				continue
			}
			if first {
				probe.firstPage[t.ID] = metadata(t)
			}
			if _, exists := threads[t.ID]; exists {
				report.DuplicateThreads++
				continue
			}
			threads[t.ID], probe.baseline[t.ID] = t, metadata(t)
			order = append(order, t.ID)
			report.Threads++
			if folder == "PENDING" || t.SystemFolder == "PENDING" {
				report.PendingThreads++
			}
			if folder == "SPAM" || t.SystemFolder == "SPAM" {
				report.SpamThreads++
			}
		}
	}
	pageFolder := func(folder string, initial *slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]]) bool {
		cursor := ""
		seen := map[string]bool{}
		for pageNumber := 0; pageNumber < limits.MailboxPages; pageNumber++ {
			var page slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]]
			if initial != nil && pageNumber == 0 {
				page = *initial
			} else {
				if !readPause(ctx) {
					report.Code = failureCode(ctx.Err())
					return false
				}
				response, err := client.PaginateMailbox(ctx, slidetypes.MakePaginateMailboxRequest(client.GetOwnFBID(), cursor, folder, nil))
				if err != nil {
					report.Code = failureCode(err)
					return false
				}
				if response == nil || response.Mailbox == nil {
					report.Code = "invalid_response"
					return false
				}
				page = response.Mailbox.ThreadsByFolder
			}
			report.MailboxPages++
			add(page, folder, initial != nil && pageNumber == 0)
			if !page.PageInfo.HasNextPage {
				return true
			}
			if len(page.Edges) == 0 {
				report.EmptyContinuingPages++
			}
			if page.PageInfo.EndCursor == "" || seen[page.PageInfo.EndCursor] {
				report.CursorStalls++
				return false
			}
			cursor = page.PageInfo.EndCursor
			seen[cursor] = true
		}
		return false
	}
	report.InboxExhausted = pageFolder("INBOX", &mailbox.Mailbox.ThreadsByFolder)
	// These folder values are modeled by igconnector/chatinfo.go. The same read
	// pagination query is used; no request is accepted or moved between folders.
	for _, folder := range []string{"PENDING", "SPAM"} {
		previousCode := report.Code
		if pageFolder(folder, nil) {
			report.RequestFoldersExhausted++
		} else if report.Code != "ok" {
			report.RequestFolderFailures++
		}
		if previousCode != "ok" {
			report.Code = previousCode
		}
	}
	participantIDs := map[int64]string{}
	canonical := map[string]bool{}
	for index, id := range order {
		if index >= limits.DetailThreads || ctx.Err() != nil {
			break
		}
		if !readPause(ctx) {
			break
		}
		response, err := client.GetThread(ctx, slidetypes.MakeGetThreadInfoRequest(id))
		if err != nil {
			report.DetailFailures++
			report.Code = failureCode(err)
			continue
		}
		if response == nil || response.ThreadInfo.AsIGDirectThread == nil {
			report.DetailFailures++
			continue
		}
		t := response.ThreadInfo.AsIGDirectThread
		report.Details++
		old := threads[id]
		if t.ID != id || t.ThreadKey != old.ThreadKey || (old.ThreadID != "" && old.ThreadID != t.ThreadID) {
			report.ThreadIdentityMismatches++
			continue
		}
		mapped := qualifiedID(a.key, "chat", t.ID)
		if canonical[mapped] {
			report.CanonicalCollisions++
		}
		canonical[mapped] = true
		if t.ThreadFBID == t.ID {
			report.ThreadFBIDMatchesID++
		}
		if t.ThreadID != "" {
			report.LongThreadIDs++
		}
		if t.InputMode != 0 {
			report.NonzeroInputModes++
		}
		if t.IsGroup {
			report.GroupThreads++
			if strconv.FormatInt(t.ThreadKey, 10) == t.ID {
				report.GroupKeyMatchesID++
			}
		} else {
			report.DirectThreads++
			if len(t.Users) == 1 && t.Users[0] != nil && t.Users[0].InteropMessagingUserFBID == t.ThreadKey {
				report.DirectKeyMatchesParticipant++
			}
		}
		ownIG := a.jar.Get(cookies.IGCookieDSUserID)
		if t.ViewerID == ownIG {
			report.ViewerIDMatchesIG++
		}
		if t.ViewerID == strconv.FormatInt(client.GetOwnFBID(), 10) {
			report.ViewerIDMatchesFB++
		}
		if t.Viewer != nil && t.Viewer.ID == ownIG {
			report.ViewerObjectMatchesAccount++
			if !t.IsGroup && len(t.Users) == 0 && t.ThreadKey == t.Viewer.InteropMessagingUserFBID {
				report.SelfThreadCandidates++
			}
		}
		members := map[int64]bool{}
		users := append([]*slidetypes.User{}, t.Users...)
		if t.Viewer != nil {
			users = append(users, t.Viewer)
		}
		for _, user := range users {
			if user == nil || user.InteropMessagingUserFBID == 0 || user.ID == "" {
				report.ParticipantIdentityConflicts++
				continue
			}
			if previous, found := participantIDs[user.InteropMessagingUserFBID]; found && previous != user.ID {
				report.ParticipantIdentityConflicts++
			}
			participantIDs[user.InteropMessagingUserFBID] = user.ID
			members[user.InteropMessagingUserFBID] = true
		}
		if report.HistoryThreads < limits.HistoryThreads {
			inspectHistory(ctx, client, t, members, limits.HistoryPages, &report)
		}
	}
	report.Participants = len(participantIDs)
	if ctx.Err() != nil {
		report.Code = failureCode(ctx.Err())
	}
	// Cursor starts at the mailbox snapshot, so changes during the bounded scan
	// are delivered when connect starts. No sequence state is written to disk.
	a.client.SetSeqID(probe.baselineSeq, time.Now())
	a.mu.Lock()
	a.probe = probe
	a.mu.Unlock()
	return report, ""
}

func readPause(ctx context.Context) bool {
	select {
	case <-time.After(100 * time.Millisecond):
		return true
	case <-ctx.Done():
		return false
	}
}

func inspectHistory(ctx context.Context, client historyClient, thread *slidetypes.ThreadInfo, members map[int64]bool, maxPages int, report *scanReport) {
	report.HistoryThreads++
	page := thread.SlideMessages
	if page == nil {
		report.HistoryFailures++
		return
	}
	ids, cursors := map[string]bool{}, map[string]bool{}
	var previous time.Time
	for pageNumber := 0; pageNumber < maxPages; pageNumber++ {
		report.HistoryPages++
		for _, edge := range page.Edges {
			m := edge.Node
			if m == nil || m.ID == "" {
				report.MissingMessageIDs++
				continue
			}
			if ids[m.ID] {
				report.DuplicateMessages++
				continue
			}
			ids[m.ID] = true
			report.Messages++
			if m.MessageID == m.ID {
				report.MessageIDMatchesID++
			}
			if m.ThreadFBID != "" && m.ThreadFBID != thread.ID {
				report.MessageThreadMismatches++
			}
			if members[m.SenderFBID] {
				report.MessageSenderMatchesParticipant++
			}
			if !m.TimestampMS.IsZero() {
				if !previous.IsZero() && m.TimestampMS.After(previous) {
					report.HistoryOrderViolations++
				}
				previous = m.TimestampMS.Time
				age := int(time.Since(m.TimestampMS.Time).Hours() / 24)
				if age > report.OldestAgeDays {
					report.OldestAgeDays = age
				}
			}
		}
		if len(ids) > report.MaxMessagesPerThread {
			report.MaxMessagesPerThread = len(ids)
		}
		if !page.PageInfo.HasNextPage {
			report.HistoryExhausted++
			return
		}
		if len(page.Edges) == 0 {
			report.EmptyContinuingPages++
		}
		cursor := page.PageInfo.EndCursor
		if cursor == "" || cursors[cursor] {
			report.CursorStalls++
			return
		}
		cursors[cursor] = true
		if pageNumber+1 == maxPages {
			report.HistoryCapped++
			return
		}
		if !readPause(ctx) {
			report.HistoryFailures++
			return
		}
		response, err := client.PaginateMessages(ctx, &slidetypes.PaginateMessagesRequest{AfterCursor: &cursor, ThreadID: thread.ID, FirstN: 20, InitialMessagePageCount: 20})
		if err != nil {
			report.HistoryFailures++
			report.Code = failureCode(err)
			return
		}
		if response == nil || response.ThreadInfo.AsIGDirectThread == nil || response.ThreadInfo.AsIGDirectThread.Messages == nil {
			report.HistoryFailures++
			return
		}
		if response.ThreadInfo.AsIGDirectThread.ID != thread.ID {
			report.MessageThreadMismatches++
			return
		}
		page = response.ThreadInfo.AsIGDirectThread.Messages
	}
}

// Keep only identities and one-way text digests for event/snapshot comparison.
// Never retain event.Raw, titles, handles, message bodies or reaction strings.
func (p *historyProbe) apply(delta *slidetypes.Delta) {
	if delta == nil {
		return
	}
	p.realtime.DeltaEvents++
	if delta.UQSeqID <= p.baselineSeq {
		p.realtime.DuplicateOrOldDeltas++
		return
	}
	// Several changes can share a sequence. Deduplicate complete event digests,
	// not the sequence alone; no raw event is retained or reported.
	raw := delta.Raw
	if len(raw) == 0 {
		raw, _ = json.Marshal(struct {
			Sequence int64
			Thread   string
			Data     any
		}{delta.UQSeqID, delta.ThreadIGID, delta.Data})
	}
	digest := sha256.Sum256(raw)
	if p.seen == nil {
		p.seen = map[[32]byte]bool{}
	}
	if p.seen[digest] {
		p.realtime.DuplicateOrOldDeltas++
		return
	}
	p.seen[digest] = true
	if delta.UQSeqID < p.lastSeq {
		p.realtime.OutOfOrderDeltas++
	} else {
		p.lastSeq = delta.UQSeqID
	}
	id := delta.ThreadIGID
	var messageID string
	unknown := false
	expect := messageExpectation{present: true, sequence: delta.UQSeqID}
	switch event := delta.Data.(type) {
	case *slidetypes.NewMessageEvent:
		if event.Message != nil {
			messageID = event.Message.ID
			if id == "" {
				id = event.Message.ThreadFBID
			}
		}
	case *slidetypes.AdminMessageEvent:
		if event.Message != nil {
			messageID = event.Message.ID
			if id == "" {
				id = event.Message.ThreadFBID
			}
		}
	case *slidetypes.DeleteMessageEvent:
		messageID = event.MessageID
		expect.present = false
	case *slidetypes.EditMessageEvent:
		messageID = event.MessageID
		digest := sha256.Sum256([]byte(event.TextBody))
		expect.textHash = &digest
	case slidetypes.UnknownEvent:
		unknown = true
	}
	if id == "" {
		unknown = true
	}
	if unknown {
		p.realtime.UnknownEvents++
	}
	if id == "" {
		return
	}
	if p.changes[id] == nil {
		p.changes[id] = map[string]messageExpectation{}
	}
	if messageID != "" && p.changes[id][messageID].sequence <= delta.UQSeqID {
		p.changes[id][messageID] = expect
	}
}

func (a *readOnlyAdapter) observe(ctx context.Context, raw json.RawMessage) (any, string) {
	client, ok := a.client.(historyClient)
	if !ok || a.probe == nil || a.stop == nil {
		return nil, "probe_not_ready"
	}
	params := struct {
		Seconds int `json:"seconds"`
	}{45}
	if len(raw) > 0 && json.Unmarshal(raw, &params) != nil {
		return nil, "invalid_params"
	}
	if params.Seconds < 0 || params.Seconds > 60 {
		return nil, "invalid_params"
	}
	select {
	case <-time.After(time.Duration(params.Seconds) * time.Second):
	case <-ctx.Done():
		return nil, failureCode(ctx.Err())
	}
	// Stop the socket before authoritative reads; don't race cached config reloads.
	a.stop()
	a.client.Disconnect()
	<-a.done
	a.mu.Lock()
	a.stop, a.done = nil, nil
	p := a.probe
	report := p.realtime
	report.ObservedSeconds, report.ChangedThreads, report.Code = params.Seconds, len(p.changes), "ok"
	a.mu.Unlock()
	response, err := client.GetMailbox(ctx)
	if err != nil {
		report.Code = failureCode(err)
		return report, ""
	}
	if response == nil || response.Mailbox == nil {
		return nil, "invalid_response"
	}
	report.SnapshotSequenceAdvanced = response.Mailbox.UQSeqID > p.baselineSeq
	for _, edge := range response.Mailbox.ThreadsByFolder.Edges {
		t := edge.Node.AsIGDirectThread
		if t == nil {
			continue
		}
		if old, found := p.firstPage[t.ID]; found {
			report.ComparedThreads++
			if old == metadata(t) {
				report.StableThreads++
			} else if p.changes[t.ID] == nil {
				report.ChangedWithoutDelta++
			}
		}
	}
	ids := make([]string, 0, len(p.changes))
	for id := range p.changes {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for index, id := range ids {
		if index >= 20 {
			report.EventMessagesUnresolved += len(p.changes[id])
			continue
		}
		if len(p.changes[id]) == 0 {
			continue
		}
		if !readPause(ctx) {
			report.Code = failureCode(ctx.Err())
			break
		}
		thread, err := client.GetThread(ctx, slidetypes.MakeGetThreadInfoRequest(id))
		if err != nil || thread == nil || thread.ThreadInfo.AsIGDirectThread == nil || thread.ThreadInfo.AsIGDirectThread.SlideMessages == nil {
			report.EventMessagesUnresolved += len(p.changes[id])
			continue
		}
		page := thread.ThreadInfo.AsIGDirectThread.SlideMessages
		current := map[string]*slidetypes.Message{}
		for _, edge := range page.Edges {
			if edge.Node != nil {
				current[edge.Node.ID] = edge.Node
			}
		}
		for messageID, expected := range p.changes[id] {
			m, found := current[messageID]
			if !found && page.PageInfo.HasNextPage {
				report.EventMessagesUnresolved++
				continue
			}
			report.EventMessagesChecked++
			if found == expected.present && (expected.textHash == nil || (found && sha256.Sum256([]byte(m.TextBody)) == *expected.textHash)) {
				report.EventMessagesMatched++
			}
		}
	}
	return report, ""
}
