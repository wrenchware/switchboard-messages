package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"unicode/utf8"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
	"go.mau.fi/util/pblite"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

// libgv lists only a conversation list's first 20 conversations: its request has no field 4 and its answer no field 2,
// where Voice keeps the page token. Protobuf keeps fields it does not know, so the token can be read from the answer
// without changing the library, and the next page asked for with it in field 4. A live probe confirmed it: the next page
// held 20 other conversations and a new token.
func unknownFields(message proto.Message) (shape string, token string) {
	raw := message.ProtoReflect().GetUnknown()
	for len(raw) > 0 {
		number, kind, n := protowire.ConsumeTag(raw)
		if n < 0 {
			return shape + "invalid", ""
		}
		raw = raw[n:]
		size := protowire.ConsumeFieldValue(number, kind, raw)
		if size < 0 {
			return shape + "invalid", ""
		}
		value := raw[:size]
		raw = raw[size:]
		if shape != "" {
			shape += "_"
		}
		shape += fmt.Sprintf("f%d", number)
		if kind == protowire.BytesType {
			if bytes, m := protowire.ConsumeBytes(value); m > 0 {
				shape += fmt.Sprintf("b%d", len(bytes))
				if number == 2 && len(bytes) > 0 && len(bytes) <= 2000 && utf8.Valid(bytes) && token == "" {
					token = string(bytes)
				}
			}
		} else {
			shape += fmt.Sprintf("t%d", kind)
		}
	}
	return shape, token
}

// The list request as pblite, the encoding the website sends, with the page token in field 4.
func listPageBody(token string) ([]byte, error) {
	encoded, err := pblite.Marshal(&gvproto.ReqListThreads{
		Folder:         gvproto.ThreadFolder_ALL_THREADS,
		UnknownInt2:    20,
		UnknownInt3:    15,
		UnknownWrapper: &gvproto.UnknownWrapper{UnknownInt2: 1, UnknownInt3: 1},
	})
	if err != nil {
		return nil, err
	}
	var fields []any
	if err = json.Unmarshal(encoded, &fields); err != nil {
		return nil, err
	}
	for len(fields) < 6 {
		fields = append(fields, nil)
	}
	fields[3] = token
	return json.Marshal(fields)
}

func (a *adapter) listPage(ctx context.Context, client *libgv.Client, token string) (*gvproto.RespListThreads, error) {
	if a.listPageAfter != nil {
		return a.listPageAfter(ctx, token)
	}
	body, err := listPageBody(token)
	if err != nil {
		return nil, err
	}
	return libgv.ReadProtoResponse[*gvproto.RespListThreads](client.MakeRequest(ctx, http.MethodPost, libgv.EndpointListThreads, nil,
		http.Header{"Content-Type": {libgv.ContentTypePBLite}}, body))
}

type chatPage struct {
	Chats []voiceChat `json:"chats"`
	Next  string      `json:"next,omitempty"`
}

// One page of the conversation list. The first page is the main list with the archived folder; each later page is the
// main list after the given token. Conversations are named from contacts, and their read state is remembered for change detection.
func (a *adapter) chatPage(ctx context.Context, client *libgv.Client, cursor string) (*chatPage, error) {
	var resp *gvproto.RespListThreads
	var err error
	if cursor == "" {
		resp, err = a.listWithArchivedFor(ctx, client)
	} else {
		resp, err = a.listPage(ctx, client, cursor)
	}
	if err != nil {
		return nil, err
	}
	_, next := unknownFields(resp)
	// A page that points back at itself would never end.
	if next == cursor {
		next = ""
	}
	if cursor != "" {
		a.diagnose(map[string]any{"olderPage": "ok", "threads": len(resp.GetThreads()), "nextToken": map[bool]string{true: "new", false: "none"}[next != ""]})
	}
	if cursor == "" {
		a.noteThreadIds(resp.GetThreads())
	}
	named, stats := a.contacts.infoFor(ctx, client, participantNumbers(resp.GetThreads()))
	if stats != nil {
		a.diagnose(stats)
	}
	page := &chatPage{Chats: make([]voiceChat, 0, len(resp.GetThreads())), Next: next}
	a.mu.Lock()
	for _, thread := range resp.GetThreads() {
		chat := convertThreadNamed(thread, named)
		a.lastRead[chat.ID] = !chat.Unread
		page.Chats = append(page.Chats, chat)
	}
	// Only change detection moves what counts as new. The first list sets the starting point, so a message that lands
	// before the first detection is still announced and what was already there is not.
	if cursor == "" && !a.watermarked {
		for _, chat := range page.Chats {
			a.seenThrough = max(a.seenThrough, chat.LastActivity)
		}
		a.watermarked = true
	}
	a.mu.Unlock()
	return page, nil
}

// Whether one-to-one conversation ids are "t." and the other person's number, which New chat relies on to start a
// conversation. Reported once per helper, as counts.
func (a *adapter) noteThreadIds(threads []*gvproto.Thread) {
	a.mu.Lock()
	done := a.idsNoted
	a.idsNoted = true
	a.mu.Unlock()
	if done {
		return
	}
	direct, matching := 0, 0
	for _, thread := range threads {
		numbers := map[string]bool{}
		for _, contact := range thread.GetContacts() {
			if contact.GetPhoneNumber() != "" {
				numbers[contact.GetPhoneNumber()] = true
			}
		}
		for _, number := range thread.GetPhoneNumbers() {
			if number != "" {
				numbers[number] = true
			}
		}
		if len(numbers) != 1 {
			continue
		}
		direct++
		for number := range numbers {
			if thread.GetID() == "t."+number {
				matching++
			}
		}
	}
	a.diagnose(map[string]any{"direct": direct, "directIdsMatch": matching})
}
