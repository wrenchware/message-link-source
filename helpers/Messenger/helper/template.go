package main

import (
	"context"
	"encoding/json"
	"sort"
	"strconv"
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
	// Which components the layout is made of, for the log only (layoutShape).
	parts string
}

const sendTextChoice bloks.BloksFunctionID = "bk.action.xs.SendTextMessageCTA"

// A layout this cannot read is no card, never a crash: upstream's decoder dereferences what Facebook sends (a null
// variable entry panics, code review 2026-11-14), and a panic here would end login or drop a whole socket batch.
func parseCardTemplate(dataclass string) (card cardTemplate, ok bool) {
	defer func() {
		if recover() != nil {
			logf("share template unreadable")
			card, ok = cardTemplate{}, false
		}
	}()
	return readCardTemplate(dataclass)
}

func readCardTemplate(dataclass string) (cardTemplate, bool) {
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
	card.parts = layoutShape(&bundle)
	for _, text := range bundle.FindDescendants(func(comp *bloks.BloksTreeComponent) bool {
		return comp.ComponentID == "bk.components.Text"
	}) {
		words := strings.TrimSpace(text.GetAttribute("text"))
		if words == "" {
			continue
		}
		if text.FindAncestor(func(comp *bloks.BloksTreeComponent) bool { return onClickCall(comp, sendTextChoice) != nil }) == nil {
			card.lines = append(card.lines, words)
			continue
		}
		card.choices = append(card.choices, words)
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

func (card cardTemplate) logShape(message string) {
	logf("share template message=%s lines=%d choices=%d picture=%v link=%v", tagOf(message), len(card.lines),
		len(card.choices), card.picture != "", card.link != "")
	if card.parts != "" {
		logf("share template parts message=%s %s", tagOf(message), card.parts)
	}
}

// A laid-out card's words and choices share the kept card_text cell (and a backup's cardText). Plain words are kept as
// they are, which is how Meta AI's card text always was; anything with choices, or words that could be mistaken for
// this form, is cardForm and JSON, which reads back exactly (code review, 2026-11-14). The first build of this, on
// 2026-11-14, kept words and choices joined by a record separator; those still read.
const (
	cardForm   = "\x00card1:"
	choiceMark = "\x1e"
)

type packedCard struct {
	Text    string   `json:"t,omitempty"`
	Choices []string `json:"c,omitempty"`
}

func packCard(text string, choices []string) string {
	if len(choices) == 0 && !strings.HasPrefix(text, cardForm) && !strings.Contains(text, choiceMark) {
		return text
	}
	encoded, _ := json.Marshal(packedCard{text, choices})
	return cardForm + string(encoded)
}

func unpackCard(kept string) (string, []string) {
	if encoded, ok := strings.CutPrefix(kept, cardForm); ok {
		var card packedCard
		if json.Unmarshal([]byte(encoded), &card) == nil {
			return card.Text, card.Choices
		}
		return "", nil
	}
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
	kept, carded := "", false
	if current != nil && !current.encrypted && current.sender != metaAIMessengerID {
		kept, carded = packCard(current.cardText, current.choices), current.carded
	}
	from.mu.Unlock()
	if kept == "" && !carded {
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

// Only the messages whose card was read, as history: redrawn and kept by the server, never announced (main.go's login).
func (c *changes) cardChanges() *changes {
	if c == nil || len(c.cards) == 0 {
		return nil
	}
	return &changes{Messages: c.cards, History: c.cards}
}

func countIDs(byChat map[string][]string) int {
	count := 0
	for _, ids := range byChat {
		count += len(ids)
	}
	return count
}

// What a layout is made of, for the log, never its words: each component kind and how many there are, how many
// text-like components carry their words as a literal (all readCardTemplate can read), and the attribute names they
// use. A business page's promotion (2026-11-25, #93) had a picture and a link but no literal Text, though Messenger
// shows its title, body and button; this says where they are when the next one comes.
func layoutShape(bundle *bloks.BloksBundle) string {
	counts := map[string]int{}
	named := map[string]bool{}
	texts, literal := 0, 0
	bundle.FindDescendants(func(comp *bloks.BloksTreeComponent) bool {
		kind := logWord(string(comp.ComponentID))
		counts[kind]++
		if strings.Contains(strings.ToLower(kind), "text") {
			texts++
			if strings.TrimSpace(comp.GetAttribute("text")) != "" {
				literal++
			}
			for name := range comp.Attributes {
				named[logWord(string(name))] = true
			}
		}
		return false
	})
	kinds := make([]string, 0, len(counts))
	for kind, count := range counts {
		kinds = append(kinds, kind+":"+strconv.Itoa(count))
	}
	sort.Strings(kinds)
	if len(kinds) > 24 {
		kinds = append(kinds[:24], "more:"+strconv.Itoa(len(kinds)-24))
	}
	names := make([]string, 0, len(named))
	for name := range named {
		names = append(names, name)
	}
	sort.Strings(names)
	return "kinds=" + strings.Join(kinds, ",") + " texts=" + strconv.Itoa(texts) + " literal=" + strconv.Itoa(literal) +
		" attributes=" + strings.Join(names, ",")
}

// A name Facebook gave a component or attribute, as it is when it looks like one, otherwise "other".
func logWord(value string) string {
	if value == "" || len(value) > 60 || strings.Trim(value, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.") != "" {
		return "other"
	}
	return value
}
