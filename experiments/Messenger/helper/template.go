package main

import (
	"context"
	"encoding/json"
	"reflect"
	"sort"
	"strconv"
	"strings"
	"sync"

	"go.mau.fi/mautrix-meta/pkg/messagix/bloks"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
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
	// Per choice, the text its press carries besides its words (the 600-760 characters seen 2026-11-14). Held in memory to
	// compare with what a pressed answer carries (template.go's notePress); never logged or kept.
	hidden [][]string
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
		card.hidden = append(card.hidden, hiddenArgs(press, words))
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

// Facebook's name for an action ("bk.action.xs.SendTextMessageCTA"), only when it is one the Bloks library itself
// names (its unminifier's table); anything else is not repeated.
func functionWord(fn bloks.BloksFunctionID) string {
	knownFunctionsOnce.Do(func() {
		knownFunctions = map[bloks.BloksFunctionID]bool{}
		if table, err := bloks.GetUnminifier(&bloks.BloksBundle{}); err == nil {
			for _, name := range table.Functions {
				knownFunctions[name] = true
			}
		}
	})
	if !knownFunctions[fn] {
		return "other"
	}
	return string(fn)
}

var (
	knownFunctionsOnce sync.Once
	knownFunctions     map[bloks.BloksFunctionID]bool
)

func (card cardTemplate) logShape(message string) {
	hidden := ""
	if len(card.hidden) > 0 && len(card.hidden[0]) > 0 {
		hidden = hiddenShape(card.hidden[0][0])
	}
	logf("share template message=%s lines=%d choices=%d picture=%v link=%v pressed=%s hidden=%s", tagOf(message), len(card.lines),
		len(card.choices), card.picture != "", card.link != "", strings.Join(card.pressed, ","), hidden)
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

// Whether a button press is only a typed reply (owner, 2026-11-14): in a conversation with laid-out cards, each message
// row says which of Facebook's columns it fills, by name, never a value. A pressed answer and a typed one from the
// account, side by side, show whether a press carries anything a typed message does not.
func logCardThreadRows(s *store, tbl *table.LSTable) {
	for _, row := range tbl.LSInsertMessage {
		if s.cardThreads[row.ThreadKey] {
			s.notePress(row)
		}
		if s.cardThreads[row.ThreadKey] {
			logf("card thread row kind=insert message=%s own=%v filled=%s unknown=%s", tagOf(row.MessageId), s.self != 0 && row.SenderId == s.self,
				filledColumns(row), unknownColumns(row.Unrecognized))
		}
	}
	for _, row := range tbl.LSUpsertMessage {
		if s.cardThreads[row.ThreadKey] {
			logf("card thread row kind=upsert message=%s own=%v filled=%s unknown=%s", tagOf(row.MessageId), s.self != 0 && row.SenderId == s.self,
				filledColumns(row), unknownColumns(row.Unrecognized))
		}
	}
}

// The names of a row's columns that hold something, from the table's own field names; never what they hold.
func filledColumns(row any) string {
	value := reflect.Indirect(reflect.ValueOf(row))
	if value.Kind() != reflect.Struct {
		return ""
	}
	names := []string{}
	for index := 0; index < value.NumField(); index++ {
		field := value.Type().Field(index)
		if !field.IsExported() || field.Name == "Unrecognized" || value.Field(index).IsZero() {
			continue
		}
		names = append(names, field.Name)
	}
	return strings.Join(names, ",")
}

func unknownColumns(unrecognized map[int]any) string {
	indexes := make([]int, 0, len(unrecognized))
	for index := range unrecognized {
		indexes = append(indexes, index)
	}
	sort.Ints(indexes)
	kinds := make([]string, 0, len(indexes))
	for _, index := range indexes {
		kinds = append(kinds, strconv.Itoa(index)+":"+valueKind(unrecognized[index]))
	}
	return strings.Join(kinds, ",")
}

// The text a press carries besides its words: every string argument of the press that is not the label.
func hiddenArgs(call *bloks.BloksScriptFuncall, words string) []string {
	var hidden []string
	for _, arg := range call.Args {
		if literal, ok := arg.Content.(*bloks.BloksScriptLiteral); ok {
			if text, ok := literal.Value().(string); ok && strings.TrimSpace(text) != words && text != "" {
				hidden = append(hidden, text)
			}
		}
	}
	return hidden
}

type pressChoice struct {
	message string
	index   int
	words   string
	hidden  []string
}

// What a pressed answer carries (2026-11-14: a press filled PlatformXmdEncoded and a typed answer did not, and only the
// press got the bot's answer). Says whether that value is exactly, or holds, or is held in, a choice's hidden text, and
// whether the row's words are that choice's: yes or no, kinds and lengths, never a value. Call with the lock held.
func (s *store) notePress(row *table.LSInsertMessage) {
	if s.self == 0 || row.SenderId != s.self || row.PlatformXmdEncoded == nil {
		return
	}
	encoded, isText := row.PlatformXmdEncoded.(string)
	if isText && encoded == "" {
		// Empty is held in every text, so it would match any choice (code review, 2026-11-14).
		logf("card press message=%s xmd=empty match=none", tagOf(row.MessageId))
		return
	}
	match, message, index, words := "none", "none", -1, false
	for _, choice := range s.pressChoices[row.ThreadKey] {
		for _, hidden := range choice.hidden {
			found := ""
			switch {
			case !isText:
			case encoded == hidden:
				found = "exact"
			case strings.Contains(encoded, hidden):
				found = "holds"
			case strings.Contains(hidden, encoded):
				found = "within"
			}
			if found != "" && (match == "none" || found == "exact") {
				match, message, index, words = found, tagOf(choice.message), choice.index, strings.TrimSpace(row.Text) == choice.words
			}
		}
	}
	logf("card press message=%s xmd=%s match=%s choiceOf=%s choice=%d words=%v", tagOf(row.MessageId),
		valueKind(row.PlatformXmdEncoded), match, message, index, words)
}

// A card's choices, replacing what an earlier reading of the same message held. Call with the lock held.
func (s *store) rememberChoices(key shareMessageKey, template cardTemplate) {
	if s.pressChoices == nil {
		s.pressChoices = map[int64][]pressChoice{}
	}
	// Bounded (code review, 2026-11-14): the newest choices of a few conversations are all a press is compared with.
	if _, known := s.pressChoices[key.thread]; !known && len(s.pressChoices) >= maxPressThreads {
		return
	}
	kept := s.pressChoices[key.thread][:0:0]
	for _, choice := range s.pressChoices[key.thread] {
		if choice.message != key.message {
			kept = append(kept, choice)
		}
	}
	for index, words := range template.choices {
		var hidden []string
		if index < len(template.hidden) {
			hidden = template.hidden[index]
		}
		kept = append(kept, pressChoice{message: key.message, index: index, words: words, hidden: hidden})
	}
	if len(kept) > maxPressChoices {
		kept = kept[len(kept)-maxPressChoices:]
	}
	s.pressChoices[key.thread] = kept
}

// The shape of a press's hidden text: its JSON keys two levels down, sorted, never a value; "text" when it is not JSON.
// Facebook's older web client sent a quick-reply answer as platform_xmd, {"quick_replies": {"content_type", "title",
// "payload", ...}}, and read it back as platform_xmd_encoded (fbchat's _message.py), the column a press fills.
func hiddenShape(hidden string) string {
	var decoded any
	if json.Unmarshal([]byte(hidden), &decoded) != nil {
		return "text"
	}
	return jsonKeys(decoded, 2)
}

func jsonKeys(value any, depth int) string {
	switch typed := value.(type) {
	case map[string]any:
		keys := make([]string, 0, len(typed))
		for key := range typed {
			if !jsonKeyWord(key) {
				key = "other"
			}
			keys = append(keys, key)
		}
		sort.Strings(keys)
		if depth > 1 {
			for index, key := range keys {
				if inner, ok := typed[key].(map[string]any); ok {
					keys[index] = key + "{" + jsonKeys(inner, depth-1) + "}"
				} else if list, ok := typed[key].([]any); ok && len(list) > 0 {
					keys[index] = key + "[" + jsonKeys(list[0], depth-1) + "]"
				}
			}
		}
		return strings.Join(keys, ";")
	case []any:
		if len(typed) == 0 {
			return "[]"
		}
		return "[" + jsonKeys(typed[0], depth) + "]"
	default:
		return valueKind(value)
	}
}

// A JSON key repeated in the log: only a field name a quick reply is known to have (fbchat's _message.py and
// _quick_reply.py); any other key is "other", since a key can be a value (code review, 2026-11-14).
func jsonKeyWord(key string) bool {
	switch key {
	case "quick_replies", "content_type", "payload", "external_payload", "data", "title", "image_url", "ignore_for_webhook":
		return true
	}
	return false
}

const (
	maxPressThreads = 50
	maxPressChoices = 30
)
