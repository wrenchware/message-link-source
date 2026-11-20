package main

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
)

func TestShapeOfPicksMessengerMessageKind(t *testing.T) {
	for _, want := range []struct {
		mime   string
		shape  string
		upload whatsmeow.MediaType
		gif    bool
	}{
		{"image/jpeg", "image", whatsmeow.MediaImage, false},
		{"IMAGE/PNG", "image", whatsmeow.MediaImage, false},
		// A gif goes as a video that plays by itself, which is what Messenger's own clients expect.
		{"image/gif", "video", whatsmeow.MediaVideo, true},
		{"video/mp4", "video", whatsmeow.MediaVideo, false},
		{"audio/mpeg", "audio", whatsmeow.MediaAudio, false},
		{"application/pdf", "document", whatsmeow.MediaDocument, false},
		{"", "document", whatsmeow.MediaDocument, false},
	} {
		got := shapeOf(want.mime)
		if got.shape != want.shape || got.upload != want.upload || got.gif != want.gif {
			t.Fatalf("%q became %+v, wanted %s/%v/%v", want.mime, got, want.shape, want.upload, want.gif)
		}
	}
}

func TestClampToCoverNeverDescribesNothing(t *testing.T) {
	if w, h := clampToCover(1200, 600); w != 400 || h != 200 {
		t.Fatalf("1200x600 clamped to %dx%d", w, h)
	}
	if w, h := clampToCover(600, 1200); w != 200 || h != 400 {
		t.Fatalf("600x1200 clamped to %dx%d", w, h)
	}
	if w, h := clampToCover(320, 240); w != 320 || h != 240 {
		t.Fatalf("a small picture was resized to %dx%d", w, h)
	}
	// An unreadable file still has to be described as something: iOS draws nothing for an empty thumbnail size.
	if w, h := clampToCover(0, 0); w != 400 || h != 400 {
		t.Fatalf("unknown size became %dx%d", w, h)
	}
}

func TestPreparePictureReadsSizeAndFixesRefusedPNG(t *testing.T) {
	// A paletted PNG is one of the colour models Messenger will not draw.
	paletted := image.NewPaletted(image.Rect(0, 0, 7, 3), color.Palette{color.Black, color.White})
	var encoded bytes.Buffer
	if png.Encode(&encoded, paletted) != nil {
		t.Fatal("could not build the test picture")
	}
	data, width, height, ok := preparePicture(encoded.Bytes(), "image/png")
	if !ok || width != 7 || height != 3 {
		t.Fatalf("size read as %dx%d", width, height)
	}
	config, err := png.DecodeConfig(bytes.NewReader(data))
	if err != nil {
		t.Fatalf("the re-encoded picture is not a png: %v", err)
	}
	if _, ok := config.ColorModel.(color.Palette); ok {
		t.Fatal("the refused colour model survived")
	}
	// Anything that is not a picture is handed on untouched and unmeasured.
	same, w, h, ok := preparePicture([]byte("not a picture"), "application/pdf")
	if !ok || string(same) != "not a picture" || w != 0 || h != 0 {
		t.Fatalf("a document was altered: %q %dx%d", same, w, h)
	}
}

// A PNG that would have to be decoded to be re-encoded, but claims more pixels than are ever decoded, is refused.
func TestPreparePictureRefusesOversizedPNG(t *testing.T) {
	wide := image.NewPaletted(image.Rect(0, 0, pictureMaxSide+1, 1), color.Palette{color.Black, color.White})
	var encoded bytes.Buffer
	if png.Encode(&encoded, wide) != nil {
		t.Fatal("could not build the test picture")
	}
	if _, _, _, ok := preparePicture(encoded.Bytes(), "image/png"); ok {
		t.Fatal("an oversized picture was accepted")
	}
}

// What is sent has to be readable by the same decoder that reads what arrives, or a sent file draws as nothing.
func TestWrappedMediaReadsBackAsAnAttachment(t *testing.T) {
	uploaded := &whatsmeow.UploadResponse{
		DirectPath:    "/v/t62.000-24/test",
		ObjectID:      "1",
		MediaKey:      bytes.Repeat([]byte{1}, 32),
		FileEncSHA256: bytes.Repeat([]byte{2}, 32),
		FileSHA256:    bytes.Repeat([]byte{3}, 32),
	}
	for _, want := range []struct {
		mime     string
		name     string
		text     string
		item     string
		animated bool
		video    bool
	}{
		{mime: "image/jpeg", name: "photo.jpg", text: "with a caption", item: "Photo"},
		{mime: "video/mp4", name: "clip.mp4", item: "Video", video: true},
		{mime: "image/gif", name: "loop.gif", item: "GIF", animated: true},
		{mime: "audio/mpeg", name: "song.mp3", item: "Audio"},
		{mime: "application/pdf", name: "statement.pdf", item: "statement.pdf"},
	} {
		content, err := wrapEncryptedMedia(shapeOf(want.mime), uploaded, []byte("0123456789"), want.name, want.mime, want.text, 800, 600)
		if err != nil {
			t.Fatalf("%s could not be wrapped: %v", want.mime, err)
		}
		item, held, caption := encryptedMedia(content, "sent")
		if item == nil || held == nil {
			t.Fatalf("%s read back as nothing", want.mime)
		}
		if item.name != want.item {
			t.Fatalf("%s was named %q, wanted %q", want.mime, item.name, want.item)
		}
		if item.mime != want.mime || item.size != 10 {
			t.Fatalf("%s read back as %q at %d bytes", want.mime, item.mime, item.size)
		}
		if item.animated != want.animated || item.video != want.video {
			t.Fatalf("%s read back animated=%v video=%v", want.mime, item.animated, item.video)
		}
		if caption != want.text {
			t.Fatalf("%s caption read back as %q", want.mime, caption)
		}
		if held.kind == "" {
			t.Fatalf("%s carries no download directions", want.mime)
		}
	}
}

// The caption belongs on the kinds that show one, and an empty one must not become the word "".
func TestWrappedMediaOmitsAnEmptyCaption(t *testing.T) {
	uploaded := &whatsmeow.UploadResponse{DirectPath: "/v/t62.000-24/test", MediaKey: bytes.Repeat([]byte{1}, 32)}
	content, err := wrapEncryptedMedia(shapeOf("image/jpeg"), uploaded, []byte("x"), "p.jpg", "image/jpeg", "", 10, 10)
	if err != nil {
		t.Fatal(err)
	}
	image, ok := content.Content.(*waConsumerApplication.ConsumerApplication_Content_ImageMessage)
	if !ok {
		t.Fatal("a photo was not wrapped as an image message")
	}
	if image.ImageMessage.GetCaption().GetText() != "" {
		t.Fatalf("an empty caption became %q", image.ImageMessage.GetCaption().GetText())
	}
}

// A recording made in the composer reads back as a voice message; the same audio chosen as a file stays audio, and
// the voice mark never reaches anything that is not audio.
func TestRecordedAudioIsSentAsAVoiceMessage(t *testing.T) {
	uploaded := &whatsmeow.UploadResponse{DirectPath: "/v/t62.000-24/test", MediaKey: bytes.Repeat([]byte{1}, 32)}
	for _, want := range []struct {
		voice bool
		name  string
	}{{voice: true, name: "Voice message"}, {voice: false, name: "Audio"}} {
		kind := shapeOf("audio/mp4")
		kind.voice = want.voice
		content, err := wrapEncryptedMedia(kind, uploaded, []byte("0123"), "Voice message.m4a", "audio/mp4", "", 0, 0)
		if err != nil {
			t.Fatal(err)
		}
		audio, ok := content.Content.(*waConsumerApplication.ConsumerApplication_Content_AudioMessage)
		if !ok || audio.AudioMessage.GetPTT() != want.voice {
			t.Fatalf("voice=%v was wrapped with push-to-talk %v", want.voice, ok && audio.AudioMessage.GetPTT())
		}
		if item, _, _ := encryptedMedia(content, "sent"); item == nil || item.name != want.name {
			t.Fatalf("voice=%v read back as %+v", want.voice, item)
		}
	}
	for _, mime := range []string{"image/jpeg", "video/mp4", "application/pdf", ""} {
		if voiceClip(true, mime) {
			t.Errorf("%q was marked a voice clip", mime)
		}
	}
	if !voiceClip(true, "audio/mp4") || !voiceClip(true, "Audio/MP4") || voiceClip(false, "audio/mp4") {
		t.Error("only audio asked for as a voice message is a voice clip")
	}
}

// A recording's length goes with it, as upstream's bridge sends one, so the other end can draw it before it is played.
// An audio message whose length was not given carries none rather than a made-up zero.
func TestVoiceMessageCarriesItsLength(t *testing.T) {
	uploaded := &whatsmeow.UploadResponse{DirectPath: "/v/t62.000-24/test", MediaKey: bytes.Repeat([]byte{1}, 32)}
	for _, want := range []struct {
		given float64
		sent  uint32
	}{{given: 7.4, sent: 7}, {given: 7.6, sent: 8}, {given: 0, sent: 0}, {given: -3, sent: 0}, {given: 1e9, sent: 0}} {
		kind := shapeOf("audio/mp4")
		kind.voice = true
		kind.seconds = wholeSeconds(want.given)
		content, err := wrapEncryptedMedia(kind, uploaded, []byte("0123"), "Voice message.m4a", "audio/mp4", "", 0, 0)
		if err != nil {
			t.Fatal(err)
		}
		audio, ok := content.Content.(*waConsumerApplication.ConsumerApplication_Content_AudioMessage)
		if !ok {
			t.Fatal("a recording was not wrapped as an audio message")
		}
		transport, err := audio.AudioMessage.Decode()
		if err != nil {
			t.Fatal(err)
		}
		ancillary := transport.GetAncillary()
		if ancillary.GetSeconds() != want.sent || (want.sent == 0) != (ancillary.GetSeconds() == 0 && ancillary.Seconds == nil) {
			t.Fatalf("a recording of %v seconds carried %v (set=%v)", want.given, ancillary.GetSeconds(), ancillary.Seconds != nil)
		}
	}
}
