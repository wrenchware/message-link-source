package main

import (
	"context"
	"encoding/json"
	"testing"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
)

func TestAvatarURLAllowsOnlyGoogleProfilePhotoCDN(t *testing.T) {
	for _, address := range []string{
		"https://lh3.googleusercontent.com/a/photo",
		"https://googleusercontent.com/photo",
	} {
		if _, ok := allowedAvatarURL(address); !ok {
			t.Fatalf("expected Google avatar URL to be allowed: %s", address)
		}
	}
	for _, address := range []string{
		"http://lh3.googleusercontent.com/photo",
		"https://googleusercontent.com.evil.test/photo",
		"https://example.test/photo",
		"https://user@lh3.googleusercontent.com/photo",
	} {
		if _, ok := allowedAvatarURL(address); ok {
			t.Fatalf("unexpected avatar URL permission: %s", address)
		}
	}
}

func TestAvatarMediaReadUsesOnlyAnIssuedOpaqueHandle(t *testing.T) {
	const id = "avatar:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	asked := ""
	a := newAdapter(nil)
	defer a.stop()
	a.client = libgv.NewClient(map[string]string{})
	a.contacts.avatarURLs = map[string]string{id: "https://lh3.googleusercontent.com/synthetic"}
	a.downloadAvatar = func(_ context.Context, _ *libgv.Client, address string) ([]byte, string, error) {
		asked = address
		return []byte{1, 2, 3}, "image/png", nil
	}
	result, code := a.callContext(context.Background(), request{Method: "ui.mediaRead", Params: json.RawMessage(`{"id":"` + id + `","offset":0}`)})
	if code != "" || asked == "" || result.(map[string]any)["total"].(int) != 3 {
		t.Fatalf("issued avatar should be readable: code=%s asked=%q result=%#v", code, asked, result)
	}
	asked = ""
	if _, code = a.callContext(context.Background(), request{Method: "ui.mediaRead", Params: json.RawMessage(`{"id":"avatar:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","offset":0}`)}); code != "media_unavailable" || asked != "" {
		t.Fatalf("unknown avatar handle must not download: code=%s asked=%q", code, asked)
	}
}
