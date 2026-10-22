package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"testing"
)

func TestReactionOwnershipAndActions(t *testing.T) {
	c := &gmproto.Conversation{ConversationID: "chat", DefaultOutgoingID: "self", Type: gmproto.ConversationType_RCS}
	m := &gmproto.Message{ConversationID: "chat", MessageID: "message"}
	sim := &gmproto.SIMCard{SIMParticipant: &gmproto.SIMParticipant{ID: "self"}, SIMData: &gmproto.SIMData{SIMPayload: &gmproto.SIMPayload{}}}
	r := reactionRequest(c, m, sim, "👍", false)
	if r == nil || r.Action != gmproto.SendReactionRequest_ADD || r.MessageID != "message" {
		t.Fatal("add")
	}
	m.Reactions = []*gmproto.ReactionEntry{{Data: gmproto.MakeReactionData("👍"), ParticipantIDs: []string{"other"}}}
	if reactionRequest(c, m, sim, "👍", true) != nil {
		t.Fatal("removed another participant reaction")
	}
	m.Reactions[0].ParticipantIDs = append(m.Reactions[0].ParticipantIDs, "self")
	r = reactionRequest(c, m, sim, "❤️", false)
	if r == nil || r.Action != gmproto.SendReactionRequest_SWITCH {
		t.Fatal("switch")
	}
	r = reactionRequest(c, m, sim, "👍", true)
	if r == nil || r.Action != gmproto.SendReactionRequest_REMOVE {
		t.Fatal("remove")
	}
	mapped := mapMessage(m)
	if len(mapped.Reactions) != 2 || mapped.Reactions[0].Key != "👍" {
		t.Fatal("reaction mapping")
	}
	c.ConversationID = "other-chat"
	if reactionRequest(c, m, sim, "👍", false) != nil {
		t.Fatal("cross chat")
	}
	c.ConversationID = "chat"
	c.ReadOnly = true
	if reactionRequest(c, m, sim, "👍", false) != nil {
		t.Fatal("read-only")
	}
}
