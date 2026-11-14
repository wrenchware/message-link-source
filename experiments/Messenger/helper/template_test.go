package main

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// A synthetic laid-out card in the shape upstream reads (XmaDataclass, content.custom_template_data.payload, a Bloks
// layout): Text lines, choices whose press sends a text message, an Image and an OpenUrlV2.
func syntheticTemplate(t *testing.T, tree string) string {
	t.Helper()
	payload := `{"layout":{"bloks_payload":{"tree":` + tree + `}}}`
	outer, err := json.Marshal(map[string]any{"content": map[string]any{"custom_template_data": map[string]any{"payload": payload}}})
	if err != nil {
		t.Fatal(err)
	}
	return string(outer)
}

const syntheticQuestion = `{"bk.components.Flexbox":{"children":[
	{"bk.components.Text":{"text":"Synthetic question?"}},
	{"bk.components.Flexbox":{"on_click":"(bk.action.xs.SendTextMessageCTA \"Synthetic yes\" 7)","children":[{"bk.components.Text":{"text":"Synthetic yes"}}]}},
	{"bk.components.Flexbox":{"on_click":"(bk.action.xs.SendTextMessageCTA \"Synthetic no\" 8)","children":[{"bk.components.Text":{"text":"Synthetic no"}}]}}]}}`

const syntheticListing = `{"bk.components.Flexbox":{"on_click":"(bk.action.navigation.OpenUrlV2 \"https://www.facebook.com/marketplace/item/1/\")","children":[
	{"bk.components.Image":{"url":"https://example.test/listing.jpg"}},
	{"bk.components.Text":{"text":"Synthetic listing"}},
	{"bk.components.Text":{"text":"$25"}}]}}`

func TestCardTemplateReadsLinesChoicesPictureAndLink(t *testing.T) {
	question, ok := parseCardTemplate(syntheticTemplate(t, syntheticQuestion))
	if !ok || !slices.Equal(question.lines, []string{"Synthetic question?"}) ||
		!slices.Equal(question.choices, []string{"Synthetic yes", "Synthetic no"}) || question.picture != "" {
		t.Fatalf("question card read as %+v", question)
	}
	if !slices.Equal(question.pressed, []string{"label+n", "label+n"}) {
		t.Fatalf("a press must be described by kind: %q", question.pressed)
	}
	listing, ok := parseCardTemplate(syntheticTemplate(t, syntheticListing))
	if !ok || listing.picture != "https://example.test/listing.jpg" || listing.link != "https://www.facebook.com/marketplace/item/1/" ||
		!slices.Equal(listing.lines, []string{"Synthetic listing", "$25"}) || len(listing.choices) != 0 {
		t.Fatalf("listing card read as %+v", listing)
	}
	for _, broken := range []string{"", "{}", `{"content":{"custom_template_data":{"payload":"not json"}}}`} {
		if _, ok := parseCardTemplate(broken); ok {
			t.Fatalf("%q must not read as a card", broken)
		}
	}
	if text, choices := unpackCard(packCard("Words", []string{"A", "B"})); text != "Words" || !slices.Equal(choices, []string{"A", "B"}) {
		t.Fatal("card words and choices must read back as written")
	}
	if text, choices := unpackCard("Meta AI words"); text != "Meta AI words" || choices != nil {
		t.Fatal("card text without choices must read back unchanged")
	}
}

// The Marketplace Assistant's two cards (2026-11-14): the listing becomes a picture card, the question its words with
// its choices under them, and both come back after a restart.
func TestCardTemplatesDrawAndSurviveRestart(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "synthetic.db")
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	a := newAdapter(&output{})
	a.kept = kept
	a.chats.setSelf(1)
	a.keepMessages(ctx, a.chats.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.ONE_TO_ONE, ThreadName: "Synthetic assistant"}},
		LSInsertMessage: []*table.LSInsertMessage{
			{ThreadKey: 7, MessageId: "synthetic-listing", SenderId: 42, TimestampMs: 1000},
			{ThreadKey: 7, MessageId: "synthetic-question", SenderId: 42, TimestampMs: 2000},
		},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{ThreadKey: 7, MessageId: "synthetic-listing", AttachmentFbid: "synthetic-listing-card", XmaDataclass: syntheticTemplate(t, syntheticListing)},
			{ThreadKey: 7, MessageId: "synthetic-question", AttachmentFbid: "synthetic-question-card", XmaDataclass: syntheticTemplate(t, syntheticQuestion)},
		},
	}))
	check := func(s *store) {
		t.Helper()
		question, ok := s.messageByID(7, "synthetic-question")
		if !ok || question.Text != "Synthetic question?" || !slices.Equal(question.Choices, []string{"Synthetic yes", "Synthetic no"}) {
			t.Fatalf("the question must carry its words and choices: %q %q", question.Text, question.Choices)
		}
		listing, ok := s.messageByID(7, "synthetic-listing")
		if !ok || listing.Text != "" || len(listing.Choices) != 0 || len(listing.Media) != 1 {
			t.Fatalf("the listing must be one picture card: %+v", listing)
		}
		card := listing.Media[0]
		if card.Name != "Synthetic listing" || card.Description != "$25" || card.Link != "https://www.facebook.com/marketplace/item/1/" {
			t.Fatalf("the listing card must carry its title, price and link: %+v", card)
		}
	}
	// And on a page, as the app reads a conversation, not only by id.
	if page := a.chats.historyBefore(7, 0, 10); len(page) != 2 || page[1].Text != "Synthetic question?" || len(page[1].Choices) != 2 {
		t.Fatalf("the question must be on the conversation's page with its words and choices: %+v", page)
	}
	check(a.chats)
	if err = kept.db.Close(); err != nil {
		t.Fatal(err)
	}
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	back := newStore()
	back.setSelf(1)
	kept.restore(ctx, back)
	check(back)
}

func TestCardTemplateLogHoldsNoWords(t *testing.T) {
	path := filepath.Join(t.TempDir(), "synthetic-template.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	previous := diagnostics
	diagnostics = &logger{file: file}
	t.Cleanup(func() {
		closeLog()
		diagnostics = previous
	})
	s := newStore()
	s.apply(&table.LSTable{
		LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "m", SenderId: 42, TimestampMs: 1000}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{ThreadKey: 7, MessageId: "m", AttachmentFbid: "c", XmaDataclass: syntheticTemplate(t, syntheticQuestion)},
		},
	})
	closeLog()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	log := string(data)
	if strings.Contains(strings.ToLower(log), "synthetic") || strings.Contains(log, "https://") {
		t.Fatalf("the template log repeated a card's words or address:\n%s", log)
	}
	if !strings.Contains(log, "share template message=") || !strings.Contains(log, "lines=1 choices=2 picture=false link=false pressed=label+n,label+n") {
		t.Fatalf("the template log must say the card's shape:\n%s", log)
	}
}

// After a restart the first page is announced to nobody, so a question kept empty before its card could be read stayed
// blank in the app (2026-11-14). Login sends the first page's cards, as history. The chat list's preview is the card's
// words, not Facebook's snippet "text", even when Facebook's activity time is not the card's.
func TestCardTemplateReachesAppAfterLoginAndNamesThePreview(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, ThreadType: table.MARKETPLACE, ThreadName: "Synthetic assistant",
			Snippet: "text", LastActivityTimestampMs: 9000}},
		LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: "synthetic-question", SenderId: 42, TimestampMs: 2000}},
	})
	page := &table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "synthetic-question", SenderId: 42, TimestampMs: 2000}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{ThreadKey: 7, MessageId: "synthetic-question", AttachmentFbid: "synthetic-card", XmaDataclass: syntheticTemplate(t, syntheticQuestion)},
		},
	}
	cards := s.apply(page).cardChanges()
	if cards == nil || !slices.Equal(cards.Messages["7"], []string{"synthetic-question"}) ||
		!slices.Equal(cards.History["7"], []string{"synthetic-question"}) || len(cards.Chats) != 0 {
		t.Fatalf("login must send the question alone, as history: %+v", cards)
	}
	// Read again unchanged, as after a restart that restored its words from the kept store: login still sends it, since
	// the server may hold the copy from before (2026-11-14), but nothing else announces it.
	if again := s.apply(page).cardChanges(); again == nil || !slices.Equal(again.History["7"], []string{"synthetic-question"}) {
		t.Fatalf("a card read again must still go out at login: %+v", again)
	}
	s.mu.Lock()
	chat := s.view(s.threads[7])
	s.mu.Unlock()
	if chat.Preview != "Synthetic question?" {
		t.Fatalf("the preview must be the card's words: %q", chat.Preview)
	}
}
