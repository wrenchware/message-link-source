package main

import (
	"bytes"
	"image"
	"image/color"
	"image/draw"
	"image/jpeg"
	"image/png"
	"math"
	"math/rand"
	"strconv"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// A letter-like shape: an "L" of two bars, antialiased at its edges as a rendered glyph is.
func drawTile(size int, ground, ink color.NRGBA, circle bool) *image.NRGBA {
	img := image.NewNRGBA(image.Rect(0, 0, size, size))
	centre := float64(size) / 2
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			if circle && math.Hypot(float64(x)+0.5-centre, float64(y)+0.5-centre) > centre {
				continue // Clear outside the circle.
			}
			fx, fy := float64(x)/float64(size), float64(y)/float64(size)
			// Coverage of the glyph, with a one-pixel ramp at each edge.
			cover := func(lo, hi, v float64) float64 {
				ramp := 1 / float64(size)
				return math.Max(0, math.Min(1, math.Min((v-lo)/ramp, (hi-v)/ramp)))
			}
			a := math.Max(cover(0.35, 0.45, fx)*cover(0.28, 0.72, fy), cover(0.35, 0.65, fx)*cover(0.62, 0.72, fy))
			mix := func(g, i uint8) uint8 { return uint8(float64(g)*(1-a) + float64(i)*a) }
			img.SetNRGBA(x, y, color.NRGBA{mix(ground.R, ink.R), mix(ground.G, ink.G), mix(ground.B, ink.B), 255})
		}
	}
	return img
}

func encodeJPEG(t *testing.T, img image.Image, quality int) []byte {
	var out bytes.Buffer
	if err := jpeg.Encode(&out, img, &jpeg.Options{Quality: quality}); err != nil {
		t.Fatal(err)
	}
	return out.Bytes()
}

func encodePNG(t *testing.T, img image.Image) []byte {
	var out bytes.Buffer
	if err := png.Encode(&out, img); err != nil {
		t.Fatal(err)
	}
	return out.Bytes()
}

func TestLetterTilesAreRecognised(t *testing.T) {
	white := color.NRGBA{255, 255, 255, 255}
	for _, ground := range []color.NRGBA{{26, 115, 232, 255}, {217, 48, 37, 255}, {30, 142, 62, 255}, {249, 171, 0, 255}, {161, 66, 244, 255}} {
		// As the phone sends them: a 94-pixel PNG.
		if tile, shape := letterTile(encodePNG(t, drawTile(tileSide, ground, white, false))); !tile {
			t.Errorf("tile %v missed: %s", ground, shape)
		}
		if tile, shape := letterTile(encodeJPEG(t, drawTile(tileSide, ground, white, false), 90)); !tile {
			t.Errorf("jpeg tile %v missed: %s", ground, shape)
		}
		// The same picture at the size the phone sends photos is not taken for one: the size is the phone's tell.
		if tile, shape := letterTile(encodePNG(t, drawTile(96, ground, white, false))); tile {
			t.Errorf("96-pixel picture %v taken for a tile: %s", ground, shape)
		}
	}
	// A plain saturated ground with no letter at all is no photo either.
	blank := image.NewNRGBA(image.Rect(0, 0, tileSide, tileSide))
	draw.Draw(blank, blank.Bounds(), image.NewUniform(color.NRGBA{26, 115, 232, 255}), image.Point{}, draw.Src)
	if tile, shape := letterTile(encodePNG(t, blank)); !tile {
		t.Errorf("blank tile missed: %s", shape)
	}
	// A pale ground is left as it came: none was seen on the phone, and white or pale is what a headshot's plain
	// background is.
	if tile, shape := letterTile(encodePNG(t, drawTile(tileSide, color.NRGBA{232, 240, 254, 255}, color.NRGBA{25, 103, 210, 255}, false))); tile || !strings.Contains(shape, "ground=pale") {
		t.Errorf("pale tile: %s", shape)
	}
}

// A shaded face with hair, lit from one side, on the given background, with the sensor noise every photo has. A
// negative wall is a digital cut-out on flat blue.
func drawHeadshot(random *rand.Rand, side int, radius, wall float64) *image.NRGBA {
	headshot := image.NewNRGBA(image.Rect(0, 0, side, side))
	clamp := func(v float64) uint8 { return uint8(math.Max(0, math.Min(255, v))) }
	for y := 0; y < side; y++ {
		for x := 0; x < side; x++ {
			noise := func() float64 { return random.NormFloat64() * 3 }
			r, g, b := wall, wall, wall
			if wall < 0 {
				r, g, b = 26, 115, 232
			}
			dx, dy := (float64(x)-float64(side)/2)/radius, (float64(y)-float64(side)/2)/(radius*1.25)
			if d := dx*dx + dy*dy; d < 1 {
				light := 0.7 + 0.3*(float64(x)/float64(side)) - 0.2*d
				r, g, b = 225*light+noise(), 170*light+noise(), 140*light+noise()
				if dy < -0.55 {
					r, g, b = 60*light+noise(), 42*light+noise(), 30*light+noise()
				}
			}
			headshot.SetNRGBA(x, y, color.NRGBA{clamp(r), clamp(g), clamp(b), 255})
		}
	}
	return headshot
}

func TestPhotosStayPhotos(t *testing.T) {
	random := rand.New(rand.NewSource(7))
	// At the tile's own size, the hardest case the size rule allows: faces of every size on a white wall, blown out or
	// not. A cut-out on a flat saturated colour, 94 pixels square, would be taken for a tile; it is tried at the 96
	// pixels the phone sends photos at.
	for _, radius := range []float64{13, 19, 25, 34} {
		for _, wall := range []float64{255, 215} {
			if tile, shape := letterTile(encodePNG(t, drawHeadshot(random, tileSide, radius, wall))); tile {
				t.Errorf("headshot radius %v wall %v taken for a tile: %s", radius, wall, shape)
			}
		}
		if tile, shape := letterTile(encodePNG(t, drawHeadshot(random, 96, radius, -1))); tile {
			t.Errorf("96-pixel cut-out radius %v taken for a tile: %s", radius, shape)
		}
	}
	// An outdoor snapshot: nothing flat anywhere.
	scene := image.NewNRGBA(image.Rect(0, 0, tileSide, tileSide))
	for y := 0; y < tileSide; y++ {
		for x := 0; x < tileSide; x++ {
			scene.SetNRGBA(x, y, color.NRGBA{uint8(x*2 + random.Intn(30)), uint8(120 + y + random.Intn(30)), uint8(200 - y + random.Intn(30)), 255})
		}
	}
	if tile, shape := letterTile(encodePNG(t, scene)); tile {
		t.Errorf("scene taken for a tile: %s", shape)
	}
	// Unreadable bytes are kept, never dropped.
	if tile, shape := letterTile([]byte("not a picture")); tile || shape != "img=unreadable" {
		t.Errorf("unreadable: %v %s", tile, shape)
	}
}

func TestContactPictureSkipsTiles(t *testing.T) {
	tile := encodePNG(t, drawTile(tileSide, color.NRGBA{26, 115, 232, 255}, color.NRGBA{255, 255, 255, 255}, false))
	photo := []byte{0xFF, 0xD8, 1, 2, 3} // Unreadable, so kept as it came.
	entry := func(data []byte) *gmproto.GetThumbnailResponse_Thumbnail {
		return &gmproto.GetThumbnailResponse_Thumbnail{Identifier: "c1", Data: &gmproto.ThumbnailData{ImageBuffer: data}}
	}
	both := &gmproto.GetThumbnailResponse{Thumbnail: []*gmproto.GetThumbnailResponse_Thumbnail{entry(tile), entry(photo)}}
	data, shape := choosePicture(both, "c1", true)
	if !bytes.Equal(data, photo) || !strings.Contains(shape, "pictures=2 tiles=1 bytes=5") || !strings.Contains(shape, "pic1_tile=1") || !strings.Contains(shape, "pic2_img=unreadable") {
		t.Fatalf("got %v %s", data, shape)
	}
	data, shape = choosePicture(&gmproto.GetThumbnailResponse{Thumbnail: []*gmproto.GetThumbnailResponse_Thumbnail{entry(tile)}}, "c1", true)
	if len(data) != 0 || !strings.Contains(shape, "pictures=1 tiles=1 bytes=0") {
		t.Fatalf("tile kept: %v %s", len(data), shape)
	}
	// As shipped: the tile is left out.
	if data, shape := thumbnailPicture(both, "c1"); !bytes.Equal(data, photo) || !strings.Contains(shape, "tiles=1") {
		t.Fatalf("shipped build: %s", shape)
	}
}

// The cost the helper pays per fetched avatar.
func BenchmarkLetterTile(b *testing.B) {
	t := &testing.T{}
	for _, size := range []int{tileSide, 256} {
		data := encodePNG(t, drawTile(size, color.NRGBA{26, 115, 232, 255}, color.NRGBA{255, 255, 255, 255}, false))
		b.Run(strconv.Itoa(size), func(b *testing.B) {
			for b.Loop() {
				letterTile(data)
			}
		})
	}
}
