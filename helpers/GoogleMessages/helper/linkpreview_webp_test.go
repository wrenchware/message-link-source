package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"image"
	"image/jpeg"
	"image/png"
	"net/http"
	"strings"
	"testing"
)

// 1-pixel WebP samples (the widely used feature-detection set): lossy and opaque, lossy with an alpha channel, and animated.
const (
	webpLossy     = "UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA"
	webpAlpha     = "UklGRkoAAABXRUJQVlA4WAoAAAAQAAAAAAAAAAAAQUxQSAwAAAARBxAR/Q9ERP8DAABWUDggGAAAABQBAJ0BKgEAAQAAAP4AAA3AAP7mtQAAAA=="
	webpAnimation = "UklGRlIAAABXRUJQVlA4WAoAAAASAAAAAAAAAAAAQU5JTQYAAAD/////AABBTk1GJgAAAAAAAAAAAAAAAAAAAGQAAABWUDhMDQAAAC8AAAAQBxAREYiI/gcA"
)

func webpFetcher(t *testing.T, body []byte, accept *string) *previewFetcher {
	t.Helper()
	return newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
		if accept != nil {
			*accept = req.Header.Get("Accept")
		}
		return previewTestResponse(req, 200, "image/webp", string(body)), nil
	}))
}

func webpSample(t *testing.T, encoded string) []byte {
	t.Helper()
	data, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

// A site that publishes its sharing picture only as WebP still gets a card with a picture: opaque WebP is handed on as JPEG,
// WebP with transparency as PNG (JPEG would turn the transparent part black).
func TestLinkPreviewPictureConvertsWebP(t *testing.T) {
	var accept string
	data, mime, err := webpFetcher(t, webpSample(t, webpLossy), &accept).picture(context.Background(), "https://images.example/cover.webp")
	if err != nil || mime != "image/jpeg" {
		t.Fatalf("opaque WebP: mime=%q err=%v", mime, err)
	}
	if picture, err := jpeg.Decode(bytes.NewReader(data)); err != nil || picture.Bounds() != image.Rect(0, 0, 1, 1) {
		t.Fatalf("opaque WebP did not become a 1x1 JPEG: %v", err)
	}
	if !strings.Contains(accept, "image/webp") || !strings.HasPrefix(accept, "image/jpeg") {
		t.Fatalf("the request should prefer JPEG and still accept WebP: %q", accept)
	}

	data, mime, err = webpFetcher(t, webpSample(t, webpAlpha), nil).picture(context.Background(), "https://images.example/cover.webp")
	if err != nil || mime != "image/png" {
		t.Fatalf("WebP with transparency: mime=%q err=%v", mime, err)
	}
	picture, err := png.Decode(bytes.NewReader(data))
	if err != nil || picture.Bounds() != image.Rect(0, 0, 1, 1) {
		t.Fatalf("WebP with transparency did not become a 1x1 PNG: %v", err)
	}
	if _, _, _, a := picture.At(0, 0).RGBA(); a == 0xffff {
		t.Fatal("the PNG lost the WebP's transparency")
	}
}

// Animated WebP is not decoded, and a WebP claiming more than previewWebPPixelLimit is refused before any pixel is decoded.
func TestLinkPreviewPictureRefusesAnimatedAndOversizedWebP(t *testing.T) {
	if _, _, err := webpFetcher(t, webpSample(t, webpAnimation), nil).picture(context.Background(), "https://images.example/a.webp"); !errors.Is(err, errPreviewResponse) {
		t.Fatalf("animated WebP was accepted: %v", err)
	}
	// An extended (VP8X) header for a 5000 x 4000 canvas and nothing else: 20 million pixels, under the general 40 million
	// cap but over the WebP one.
	header := []byte("VP8X\x0a\x00\x00\x00\x00\x00\x00\x00")
	// Canvas width and height minus one, 24 bits each, little-endian.
	canvas := make([]byte, 6)
	canvas[0], canvas[1], canvas[2] = byte((5000-1)&0xff), byte((5000-1)>>8), byte((5000-1)>>16)
	canvas[3], canvas[4], canvas[5] = byte((4000-1)&0xff), byte((4000-1)>>8), byte((4000-1)>>16)
	body := append(header, canvas...)
	riff := append([]byte("RIFF"), 0, 0, 0, 0)
	riff = append(riff, []byte("WEBP")...)
	riff = append(riff, body...)
	binary.LittleEndian.PutUint32(riff[4:8], uint32(len(riff)-8))
	if config, format, err := image.DecodeConfig(bytes.NewReader(riff)); err != nil || format != "webp" || config.Width != 5000 || config.Height != 4000 {
		t.Fatalf("the oversized sample is not the header it means to be: %v %s %dx%d", err, format, config.Width, config.Height)
	}
	if _, _, err := webpFetcher(t, riff, nil).picture(context.Background(), "https://images.example/big.webp"); !errors.Is(err, errPreviewResponse) {
		t.Fatalf("a 20-megapixel WebP was decoded: %v", err)
	}
}
