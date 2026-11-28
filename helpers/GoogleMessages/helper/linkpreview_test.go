package main

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func textMessage(id, text string) *gmproto.Message {
	return &gmproto.Message{ConversationID: "chat", MessageID: id,
		MessageInfo: []*gmproto.MessageInfo{{Data: &gmproto.MessageInfo_MessageContent{MessageContent: &gmproto.MessageContent{Content: text}}}}}
}

func TestFirstPreviewURLTakesOneRealAddress(t *testing.T) {
	for text, want := range map[string]string{
		"look at https://example.com/article":            "https://example.com/article",
		"(see https://example.com/a)":                    "https://example.com/a",
		"https://example.com/a. and https://example.com/b": "https://example.com/a",
		"ends in a question https://example.com/x?":      "https://example.com/x",
		"no link here at all":                            "",
		"ftp://example.com/file":                         "",
		// Credentials in an address would be sent to the website; such a link is never fetched.
		"https://user:pass@example.com/x": "",
		// A bracket that closes the sentence rather than the address.
		"(https://example.com/wiki/Thing_(disambiguation))": "https://example.com/wiki/Thing_(disambiguation)",
	} {
		if got := firstPreviewURL(text); got != want {
			t.Fatalf("%q gave %q, wanted %q", text, got, want)
		}
	}
}

func TestPlayableLinkOnlyForThingsThatPlay(t *testing.T) {
	for link, want := range map[string]bool{
		"https://www.youtube.com/watch?v=x":        true,
		"https://youtu.be/x":                       true,
		"https://www.instagram.com/reel/x/":        true,
		"https://www.instagram.com/p/x/":           false,
		"https://www.facebook.com/share/v/x/":      true,
		"https://www.facebook.com/somepage/posts/x": false,
		"https://example.com/article":              false,
	} {
		if got := playableLink(link); got != want {
			t.Fatalf("%q playable=%v, wanted %v", link, got, want)
		}
	}
}

// The whole round trip with a stub fetcher: ask, wait, and find the card on the message.
func TestPreviewIsAttachedToTheMessageItWasAskedFor(t *testing.T) {
	a := &adapter{out: &output{encoder: json.NewEncoder(discardWriter{})}}
	a.messageRefs = map[string]*gmproto.Message{"m1": textMessage("m1", "read this https://example.com/a")}
	done := make(chan struct{}, 1)
	a.previewJobs = newPreviewJobs(a, context.Background(), func(context.Context, string) (*linkPreviewResult, error) {
		defer func() { done <- struct{}{} }()
		return &linkPreviewResult{title: "An article", imageURL: "https://example.com/cover.jpg", width: 800, height: 400}, nil
	})
	ask(t, a, []string{"m1"})
	waitFor(t, done)

	// The fetcher has returned, but the worker stores its result immediately
	// afterward. Wait for that handoff rather than racing the worker scheduler.
	var view viewMessage
	deadline := time.Now().Add(2 * time.Second)
	for {
		view = a.viewMessage(a.messageRefs["m1"])
		if len(view.Media) == 1 || time.Now().After(deadline) {
			break
		}
		time.Sleep(time.Millisecond)
	}
	if len(view.Media) != 1 {
		t.Fatalf("the message carries %d attachments", len(view.Media))
	}
	card := view.Media[0]
	if card.Name != "An article" || card.Link != "https://example.com/a" || card.Width != 800 || card.Play {
		t.Fatalf("the card came out as %+v", card)
	}
	if !generatedLinkPreview(card.ID) {
		t.Fatalf("the card id %q is not marked as generated", card.ID)
	}
	// The picture behind it is fetched from the website, by that id.
	if source, ok := a.previewSource(card.ID); !ok || source != "https://example.com/cover.jpg" {
		t.Fatalf("the picture resolved to %q (%v)", source, ok)
	}

	// An edited message is not the message the card was made for, so the old card is not shown.
	a.messageRefs["m1"] = textMessage("m1", "never mind")
	if len(a.viewMessage(a.messageRefs["m1"]).Media) != 0 {
		t.Fatal("an edited message kept a card made for its old text")
	}
}

// A page with nothing to offer is asked once, not every time the conversation is looked at.
func TestAPageWithNoPictureIsNotAskedAgain(t *testing.T) {
	a := &adapter{out: &output{encoder: json.NewEncoder(discardWriter{})}}
	a.messageRefs = map[string]*gmproto.Message{"m1": textMessage("m1", "https://example.com/empty")}
	asked := make(chan struct{}, 8)
	a.previewJobs = newPreviewJobs(a, context.Background(), func(context.Context, string) (*linkPreviewResult, error) {
		asked <- struct{}{}
		return nil, errors.New("no picture")
	})
	ask(t, a, []string{"m1"})
	waitFor(t, asked)
	ask(t, a, []string{"m1"})
	ask(t, a, []string{"m1"})
	select {
	case <-asked:
		t.Fatal("the same dead page was requested again")
	case <-time.After(150 * time.Millisecond):
	}
	if len(a.viewMessage(a.messageRefs["m1"]).Media) != 0 {
		t.Fatal("a page with no picture produced a card anyway")
	}
}

// Nothing is asked for a message the app has not been shown, whatever ids it names.
func TestOnlyKnownMessagesAreAskedAbout(t *testing.T) {
	a := &adapter{out: &output{encoder: json.NewEncoder(discardWriter{})}}
	a.messageRefs = map[string]*gmproto.Message{}
	a.previewJobs = newPreviewJobs(a, context.Background(), func(context.Context, string) (*linkPreviewResult, error) {
		t.Fatal("a website was requested for a message that was never shown")
		return nil, nil
	})
	if queued := ask(t, a, []string{"unknown"}); queued != 0 {
		t.Fatalf("%d requests were queued for an unknown message", queued)
	}
	time.Sleep(50 * time.Millisecond)
}

func ask(t *testing.T, a *adapter, ids []string) int {
	t.Helper()
	params, _ := json.Marshal(map[string]any{"chatId": "chat", "messageIds": ids})
	result, code := a.loadLinkPreviews(request{Method: "ui.linkPreviews", Params: params})
	if code != "" {
		t.Fatalf("the request was refused: %s", code)
	}
	return result.(map[string]any)["queued"].(int)
}

func waitFor(t *testing.T, done chan struct{}) {
	t.Helper()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("the request never happened")
	}
}

type discardWriter struct{}

func (discardWriter) Write(p []byte) (int, error) { return len(p), nil }
