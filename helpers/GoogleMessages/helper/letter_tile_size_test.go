package main

import (
	"bytes"
	"encoding/binary"
	"hash/crc32"
	"image"
	"runtime"
	"testing"
)

// A PNG whose header claims width by height, with no picture data after it.
func pngHeaderClaiming(t *testing.T, width, height uint32) []byte {
	t.Helper()
	data := encodePNG(t, image.NewGray(image.Rect(0, 0, 1, 1)))
	// The signature (8 bytes), then the IHDR chunk: length (4), type (4), width (4), height (4), the rest (5), CRC (4).
	header := data[:8+4+4+13+4]
	binary.BigEndian.PutUint32(header[16:20], width)
	binary.BigEndian.PutUint32(header[20:24], height)
	binary.BigEndian.PutUint32(header[29:33], crc32.ChecksumIEEE(header[12:29]))
	return header
}

// The phone's picture bytes are read for their size before they are decoded, and a picture over 1024 pixels a side is
// not decoded at all: it stays a photo, as an unreadable one does.
func TestLetterTileReadsTheSizeBeforeDecoding(t *testing.T) {
	if tile, shape := letterTile(pngHeaderClaiming(t, 50000, 50000)); tile || shape != "img=png w=50000 h=50000" {
		t.Errorf("a header claiming 50000 by 50000 pixels: tile %v, %q", tile, shape)
	}
	// A real 2000 by 2000 picture compresses to a few KB and takes 4 MB to decode.
	large := encodePNG(t, image.NewGray(image.Rect(0, 0, 2000, 2000)))
	least := ^uint64(0)
	for range 3 {
		var before, after runtime.MemStats
		runtime.ReadMemStats(&before)
		tile, shape := letterTile(large)
		runtime.ReadMemStats(&after)
		if tile || shape != "img=png w=2000 h=2000" {
			t.Fatalf("a 2000 pixel picture: tile %v, %q", tile, shape)
		}
		least = min(least, after.TotalAlloc-before.TotalAlloc)
	}
	if least > 1<<20 {
		t.Fatalf("looking at a 2000 pixel picture allocated %d bytes", least)
	}
	// At the bound it is still looked at.
	if _, shape := letterTile(encodePNG(t, image.NewGray(image.Rect(0, 0, tileDecodeSide, 8)))); !bytes.HasPrefix([]byte(shape), []byte("img=png w=1024 h=8 ")) {
		t.Fatalf("a picture at the bound was not looked at: %q", shape)
	}
}
