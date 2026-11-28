package main

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
)

// Received voice messages, GIFs and stickers, built from the fields the helper decodes (slidetypes messagecontent.go)
// and chosen between as upstream chooses (igconv). No real sample exists; a shape outside these stays text.
func TestVoiceMessagesAreRecordingsWithTheirLength(t *testing.T) {
	store := newMediaStore()
	voice := &slidetypes.MessageContentAudio{AudioAttachments: []*slidetypes.AudioAttachment{
		{AttachmentFBID: "audio-1", AttachmentCDNURL: "https://cdn.example/voice.mp4", PlayableDurationMS: 4200},
		// No address: not a recording that failed, not a recording.
		{AttachmentFBID: "audio-2"},
		nil,
	}}
	items := mapMedia(voice, "msg-v", store)
	if len(items) != 1 || !items[0].Audio || items[0].ID != "audio-1" || items[0].Duration != 4200 || items[0].Video || items[0].Preview != "" {
		t.Fatalf("a voice message was not one recording with its length: %+v", items)
	}
	if url, ok := store.address("audio-1"); !ok || url != "https://cdn.example/voice.mp4" {
		t.Fatal("the recording's address was not kept for the download")
	}
	// No id from Instagram: named after the message and its place, the same in every session.
	unnamed := mapMedia(&slidetypes.MessageContentAudio{AudioAttachments: []*slidetypes.AudioAttachment{{AttachmentCDNURL: "https://cdn.example/v2.mp4"}}}, "msg-w", store)
	if len(unnamed) != 1 || unnamed[0].ID != "voice:msg-w:0" {
		t.Fatalf("an unnamed recording got an unstable id: %+v", unnamed)
	}
	if mapMedia(&slidetypes.MessageContentAudio{}, "msg-x", store) != nil {
		t.Fatal("a voice message with nothing to play carried media")
	}
}

func TestGifsAndStickersFollowUpstreamsChoice(t *testing.T) {
	store := newMediaStore()
	animated := &slidetypes.MessageContentAnimatedMedia{AnimatedMedia: []*slidetypes.AnimatedAttachment{
		// A GIF with a clip: the clip, over its still.
		{AttachmentMP4URL: "https://cdn.example/g.mp4", AttachmentWebpURL: "https://cdn.example/g.webp", PreviewCDNURL: "https://cdn.example/g.jpg", PreviewWidth: 200, PreviewHeight: 150},
		// A sticker: its WebP picture even when a clip exists.
		{IsSticker: true, AttachmentMP4URL: "https://cdn.example/s.mp4", AttachmentWebpURL: "https://cdn.example/s.webp", PreviewWidth: 160, PreviewHeight: 160},
		// No clip: the WebP picture.
		{AttachmentWebpURL: "https://cdn.example/w.webp"},
		// Only a still: an ordinary picture.
		{PreviewCDNURL: "https://cdn.example/still.jpg"},
		// Nothing addressable.
		{AltText: "nothing"},
	}}
	items := mapMedia(animated, "msg-g", store)
	if len(items) != 4 {
		t.Fatalf("expected four animated parts, got %d", len(items))
	}
	clip, sticker, picture, still := items[0], items[1], items[2], items[3]
	if !clip.Gif || clip.Mime != "video/mp4" || clip.Preview == "" || clip.Sticker || clip.Width != 200 || clip.Height != 150 {
		t.Fatalf("a GIF clip is wrong: %+v", clip)
	}
	if url, _ := store.address(clip.ID); url != "https://cdn.example/g.mp4" {
		t.Fatal("a GIF clip does not lead to its clip")
	}
	if url, _ := store.address(clip.Preview); url != "https://cdn.example/g.jpg" {
		t.Fatal("a GIF clip's still does not lead to its preview")
	}
	if !sticker.Sticker || sticker.Gif || sticker.Mime != "image/webp" || sticker.Preview != "" {
		t.Fatalf("an animated sticker is wrong: %+v", sticker)
	}
	if url, _ := store.address(sticker.ID); url != "https://cdn.example/s.webp" {
		t.Fatal("a sticker does not lead to its WebP picture")
	}
	if !picture.Gif || picture.Mime != "image/webp" || picture.Sticker {
		t.Fatalf("a GIF with no clip is wrong: %+v", picture)
	}
	if still.Gif || still.Sticker || still.Mime != "" {
		t.Fatalf("a still-only GIF is not an ordinary picture: %+v", still)
	}
	// Each part its own stable name.
	seen := map[string]bool{}
	for _, item := range items {
		if seen[item.ID] || !strings.HasPrefix(item.ID, "animated") || strings.Contains(item.ID, "http") {
			t.Fatalf("an animated part has a clashing or leaking id: %q", item.ID)
		}
		seen[item.ID] = true
	}

	stuck := mapMedia(&slidetypes.MessageContentSticker{PreviewURL: "https://cdn.example/st.png", PreviewWidth: 120, PreviewHeight: 110}, "msg-s", store)
	if len(stuck) != 1 || !stuck[0].Sticker || stuck[0].ID != "sticker:msg-s" || stuck[0].Width != 120 {
		t.Fatalf("a store sticker is wrong: %+v", stuck)
	}
	if mapMedia(&slidetypes.MessageContentSticker{AltText: "no picture"}, "msg-t", store) != nil {
		t.Fatal("a sticker with no picture carried media")
	}
	// A shape this build does not decode stays on the text fallback.
	if mapMedia(slidetypes.MessageContentUnknown{"audio_attachments": []any{}}, "msg-u", store) != nil {
		t.Fatal("an unknown content shape was given media")
	}
	if mapMedia(&slidetypes.MessageContentMusicSticker{PreviewURL: "https://cdn.example/m.jpg"}, "msg-m", store) != nil {
		t.Fatal("a music sticker, which is not built, was given media")
	}
}

// A share whose primary preview is missing still has a picture: the fallback address, the one Instagram's own client
// would use.
func TestAShareFallsBackToItsPreviewsFallbackAddress(t *testing.T) {
	store := newMediaStore()
	for _, test := range []struct {
		name string
		xma  *slidetypes.XMAContent
		want string
	}{
		{"primary", &slidetypes.XMAContent{XMAPreviewImage: &slidetypes.XMAPreviewImage{URL: "https://cdn.example/a.jpg", FallbackURL: "https://cdn.example/b.jpg"}}, "https://cdn.example/a.jpg"},
		{"fallback", &slidetypes.XMAContent{XMAPreviewImage: &slidetypes.XMAPreviewImage{FallbackURL: "https://cdn.example/b.jpg", Width: 90, Height: 160}}, "https://cdn.example/b.jpg"},
		{"plain preview", &slidetypes.XMAContent{XMAPreviewImage: &slidetypes.XMAPreviewImage{}, PreviewImage: &slidetypes.XMAPreviewImage{URL: "https://cdn.example/c.jpg"}}, "https://cdn.example/c.jpg"},
		{"plain fallback", &slidetypes.XMAContent{PreviewImage: &slidetypes.XMAPreviewImage{FallbackURL: "https://cdn.example/d.jpg"}}, "https://cdn.example/d.jpg"},
		{"none", &slidetypes.XMAContent{XMAPreviewImage: &slidetypes.XMAPreviewImage{}}, ""},
	} {
		items := mapMedia(&slidetypes.MessageContentXMA{XMA: test.xma}, "msg-"+test.name, store)
		if test.want == "" {
			if items != nil {
				t.Fatalf("%s: a share with no address carried a picture", test.name)
			}
			continue
		}
		if len(items) != 1 || !items[0].Play {
			t.Fatalf("%s: no share picture", test.name)
		}
		if url, _ := store.address(items[0].ID); url != test.want {
			t.Fatalf("%s: the picture leads to %q", test.name, url)
		}
		if test.name == "fallback" && (items[0].Width != 90 || items[0].Height != 160) {
			t.Fatal("the fallback's size was not carried")
		}
	}
}

// Deltas are counted by kind and not acted on; the counts reach the app in the snapshot answer only.
func TestRealtimeDeltasAreCountedByKind(t *testing.T) {
	a, _ := chatFixture()
	incoming := &slidetypes.Delta{Data: &slidetypes.NewMessageEvent{Message: said("n1", "direct", 456, "synthetic-private-body", 1)}}
	own := &slidetypes.Delta{Data: &slidetypes.NewMessageEvent{Message: said("n2", "direct", 1234, "synthetic-private-body", 2)}}
	for _, delta := range []*slidetypes.Delta{incoming, own,
		{Data: &slidetypes.ReadReceiptEvent{}}, {Data: &slidetypes.MarkReadEvent{}},
		{Data: &slidetypes.CreateReactionEvent{}}, {Data: &slidetypes.DeleteMessageEvent{}},
		{Data: slidetypes.UnknownEvent{}}} {
		if err := a.handle(context.Background(), a.generation, delta); err != nil {
			t.Fatal(err)
		}
	}
	got := a.requests.deltas.counts()
	if got != (deltaCounts{Total: 7, Messages: 2, Incoming: 1, Reads: 2, Reactions: 1, Edits: 1, Other: 1}) {
		t.Fatalf("deltas miscounted: %+v", got)
	}
	result, _ := a.call(context.Background(), request{Method: "snapshot"})
	raw, _ := json.Marshal(result)
	if !strings.Contains(string(raw), `"realtime":{"total":7,"messages":2,"incoming":1,"reads":2,"reactions":1,"edits":1,"other":1}`) {
		t.Fatalf("snapshot does not carry the delta counts: %s", raw)
	}
	if strings.Contains(string(raw), "synthetic-private-body") {
		t.Fatal("a message body reached the snapshot")
	}
}
