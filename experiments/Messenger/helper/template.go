package main

import (
	"context"
	"encoding/json"
	"strings"

	"go.mau.fi/mautrix-meta/pkg/messagix/bloks"
)

// A card Facebook lays out itself (2026-11-14, Marketplace Assistant: a listing with its picture, title and price, and
// a question with three buttons) carries nothing in the card's own columns: all of it is a Bloks layout inside
// XmaDataclass. Read as upstream's xmaBloksTemplateToMatrix does (pinned msgconv/from-meta.go): every Text is a line,
// unless something above it sends a text message when pressed, which makes it a choice; the first Image is the picture
// and the first OpenUrlV2 where it leads.
type cardTemplate struct {
	lines   []string
	choices []string
	picture string
	link    string
	// What each choice's press carries, for the log (template log line): the action's arguments by kind, never a value.
	pressed []string
}

const sendTextChoice bloks.BloksFunctionID = "bk.action.xs.SendTextMessageCTA"

func parseCardTemplate(dataclass string) (cardTemplate, bool) {
	var card cardTemplate
	var outer struct {
		Content struct {
			CustomTemplateData *struct {
				Payload string `json:"payload"`
			} `json:"custom_template_data"`
		} `json:"content"`
	}
	var bundle bloks.BloksBundle
	if json.Unmarshal([]byte(dataclass), &outer) != nil || outer.Content.CustomTemplateData == nil ||
		json.Unmarshal([]byte(outer.Content.CustomTemplateData.Payload), &bundle) != nil {
		return card, false
	}
	for _, text := range bundle.FindDescendants(func(comp *bloks.BloksTreeComponent) bool {
		return comp.ComponentID == "bk.components.Text"
	}) {
		words := strings.TrimSpace(text.GetAttribute("text"))
		if words == "" {
			continue
		}
		var press *bloks.BloksScriptFuncall
		text.FindAncestor(func(comp *bloks.BloksTreeComponent) bool {
			press = onClickCall(comp, sendTextChoice)
			return press != nil
		})
		if press == nil {
			card.lines = append(card.lines, words)
			continue
		}
		card.choices = append(card.choices, words)
		card.pressed = append(card.pressed, describeArgs(press, words))
	}
	bundle.FindDescendant(func(comp *bloks.BloksTreeComponent) bool {
		if call := onClickCall(comp, "bk.action.navigation.OpenUrlV2"); call != nil && len(call.Args) > 0 {
			if literal, ok := call.Args[0].Content.(*bloks.BloksScriptLiteral); ok {
				card.link, _ = literal.Value().(string)
			}
		}
		return card.link != ""
	})
	card.picture = bundle.FindDescendant(func(comp *bloks.BloksTreeComponent) bool {
		return comp.ComponentID == "bk.components.Image"
	}).GetAttribute("url")
	if !strings.HasPrefix(card.link, "https://") {
		card.link = ""
	}
	if !strings.HasPrefix(card.picture, "https://") {
		card.picture = ""
	}
	return card, len(card.lines) > 0 || len(card.choices) > 0 || card.picture != ""
}

func onClickCall(comp *bloks.BloksTreeComponent, fn bloks.BloksFunctionID) *bloks.BloksScriptFuncall {
	script := comp.GetScript("on_click")
	if script == nil {
		return nil
	}
	return findCall(&script.AST, fn)
}

func findCall(node *bloks.BloksScriptNode, fn bloks.BloksFunctionID) *bloks.BloksScriptFuncall {
	if node == nil {
		return nil
	}
	call, ok := node.Content.(*bloks.BloksScriptFuncall)
	if !ok {
		return nil
	}
	if call.Function == fn {
		return call
	}
	for index := range call.Args {
		if found := findCall(&call.Args[index], fn); found != nil {
			return found
		}
	}
	return nil
}

// A press's arguments by kind: "label" for the choice's own words, s<length> for other text, n, b, null, a nested
// action by Facebook's name for it, and l/m with a count. Whether a press is more than its words is the question this
// answers before the app sends one.
func describeArgs(call *bloks.BloksScriptFuncall, words string) string {
	kinds := make([]string, 0, len(call.Args))
	for _, arg := range call.Args {
		switch content := arg.Content.(type) {
		case *bloks.BloksScriptFuncall:
			kinds = append(kinds, "f:"+functionWord(content.Function))
		case *bloks.BloksScriptLiteral:
			if text, ok := content.Value().(string); ok && strings.TrimSpace(text) == words {
				kinds = append(kinds, "label")
			} else {
				kinds = append(kinds, valueKind(content.Value()))
			}
		default:
			kinds = append(kinds, "other")
		}
	}
	return strings.Join(kinds, "+")
}

// Facebook's name for an action ("bk.action.xs.SendTextMessageCTA"); anything else is not repeated.
func functionWord(fn bloks.BloksFunctionID) string {
	value := string(fn)
	if value == "" || len(value) > 60 || strings.Trim(value, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.") != "" {
		return "other"
	}
	return value
}

func (card cardTemplate) logShape(message string) {
	logf("share template message=%s lines=%d choices=%d picture=%v link=%v pressed=%s", tagOf(message), len(card.lines),
		len(card.choices), card.picture != "", card.link != "", strings.Join(card.pressed, ","))
}

// A laid-out card's words and choices share the kept card_text cell (and a backup's cardText): the words, then each
// choice after a record separator. Meta AI's card text never holds one, so it reads back as it was written.
const choiceMark = "\x1e"

func packCard(text string, choices []string) string {
	if len(choices) == 0 {
		return text
	}
	return text + choiceMark + strings.Join(choices, choiceMark)
}

func unpackCard(kept string) (string, []string) {
	parts := strings.Split(kept, choiceMark)
	if len(parts) == 1 {
		return kept, nil
	}
	return parts[0], parts[1:]
}

// Card words and choices of an ordinary message, against the copy already written down, so a restart keeps them.
// Written only for such messages; Meta AI's go through rememberOrdinaryAIOn.
func (m *messageStore) rememberCardOn(ctx context.Context, on execer, thread int64, id string, from *store) {
	from.mu.Lock()
	current := from.messages[thread][id]
	kept := ""
	if current != nil && !current.encrypted && current.sender != metaAIMessengerID {
		kept = packCard(current.cardText, current.choices)
	}
	from.mu.Unlock()
	if kept == "" {
		return
	}
	sealed, err := protectStrings(kept)
	if err != nil {
		logf("card text could not be protected")
		return
	}
	if _, err := on.ExecContext(ctx, `UPDATE encrypted_message SET card_text = ? WHERE id = ? AND thread = ? AND encrypted = 0`,
		sealed[0], id, thread); err != nil {
		logf("card text could not be kept")
	}
}
