package main

import (
	"bytes"
	"image"
	"image/color"
	"strconv"
)

// Google answers a contact that has no photo with a picture of its own: the initial, in one colour, on a flat coloured
// ground. The app draws its own initials in its own palette for everyone else, so such a picture is treated as no photo.
//
// Measured on the user's phone on 2026-09-23 (the 0.1.284 probe, 30 answers): every letter tile was a 94 by 94 PNG of
// 1 to 3.5 KB, 94 to 100 percent flat colour, on a saturated ground; every photo was a 96 by 96 PNG of 6.5 to 22 KB and
// 40 to 87 percent flat. The phone draws its tiles two pixels smaller than its photos, and that is the tell. Flatness
// and a coloured ground are required as well, so a photo would have to be 94 pixels square, nearly all one colour and
// not white, grey or pale to be taken for one. The rule before, set from made-up pictures (exact colours, letter shape),
// recognised 4 of the 16 real tiles.
//
// The picture is sampled on a coarse grid: 0.1 ms at 96 pixels, in the helper, once per fetched avatar. Anything
// unreadable stays a photo. The shape goes to the probe: sizes and percentages only, never a colour.
const tileGrid, tileSide = 32, 94

type tileSample struct {
	c      color.NRGBA
	border bool
}

// The largest side a picture is decoded at to be looked at. Tiles and photos are under 100 pixels a side; the phone's
// bytes are read for their size first, so a header claiming a vast picture never has one allocated for it.
const tileDecodeSide = 1024

func letterTile(data []byte) (bool, string) {
	config, format, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil {
		return false, "img=unreadable"
	}
	shape := "img=" + format + " w=" + strconv.Itoa(config.Width) + " h=" + strconv.Itoa(config.Height)
	if config.Width > tileDecodeSide || config.Height > tileDecodeSide {
		return false, shape
	}
	picture, _, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		return false, "img=unreadable"
	}
	bounds := picture.Bounds()
	shape = "img=" + format + " w=" + strconv.Itoa(bounds.Dx()) + " h=" + strconv.Itoa(bounds.Dy())
	if bounds.Dx() < 8 || bounds.Dy() < 8 {
		return false, shape
	}
	samples := make([]tileSample, 0, tileGrid*tileGrid)
	for row := 0; row < tileGrid; row++ {
		for column := 0; column < tileGrid; column++ {
			x := bounds.Min.X + (2*column+1)*bounds.Dx()/(2*tileGrid)
			y := bounds.Min.Y + (2*row+1)*bounds.Dy()/(2*tileGrid)
			c := color.NRGBAModel.Convert(picture.At(x, y)).(color.NRGBA)
			if c.A < 128 {
				continue // Outside a circle cut from a clear square.
			}
			samples = append(samples, tileSample{c, row == 0 || column == 0 || row == tileGrid-1 || column == tileGrid-1})
		}
	}
	if len(samples) < tileGrid*tileGrid/4 {
		return false, shape + " tile=0"
	}
	// Up to three flat colours, commonest first, each unlike the ones before it.
	flats := []color.NRGBA{}
	for len(flats) < 3 {
		next, ok := commonest(samples, func(s tileSample) bool { return nearAny(s.c, flats) < 0 })
		if !ok {
			break
		}
		flats = append(flats, next)
	}
	flat, exact, edge, other := 0, 0, 0, 0
	for _, s := range samples {
		switch index := nearAny(s.c, flats); {
		case index >= 0:
			flat++
			if distance(s.c, flats[index]) <= exactTolerance {
				exact++
			}
		case onAnyBlend(s.c, flats):
			edge++
		default:
			other++
		}
	}
	// The border is one colour, or clear: a tile's ground reaches every edge.
	ground, _ := commonest(samples, func(s tileSample) bool { return s.border })
	borders, borderGround := 0, 0
	for _, s := range samples {
		if s.border {
			borders++
			if distance(s.c, ground) <= flatTolerance {
				borderGround++
			}
		}
	}
	contrast := false
	for i := range flats {
		for j := i + 1; j < len(flats); j++ {
			contrast = contrast || distance(flats[i], flats[j]) >= 60
		}
	}
	percent := func(part int) int { return part * 100 / len(samples) }
	borderShare := 100
	if borders > 0 {
		borderShare = borderGround * 100 / borders
	}
	// The letter is drawn in one colour too, where whatever stands on a photo's plain background is shaded: of what is not
	// ground, how much is exactly one of the other flat colours.
	figure, figureExact := 0, 0
	for _, s := range samples {
		if distance(s.c, ground) <= flatTolerance {
			continue
		}
		figure++
		for _, f := range flats {
			if distance(f, ground) > flatTolerance && distance(s.c, f) <= exactTolerance {
				figureExact++
				break
			}
		}
	}
	figureShare := 100
	if figure > 0 {
		figureShare = figureExact * 100 / figure
	}
	kind := groundKind(ground)
	tile := bounds.Dx() == tileSide && bounds.Dy() == tileSide && percent(flat) >= 90 && percent(other) <= 5 && kind == "saturated"
	shape += " colors=" + strconv.Itoa(len(flats)) + " flat=" + strconv.Itoa(percent(flat)) + " exact=" + strconv.Itoa(percent(exact)) + " edge=" + strconv.Itoa(percent(edge)) +
		" other=" + strconv.Itoa(percent(other)) + " border=" + strconv.Itoa(borderShare) + " contrast=" + bit(contrast) +
		" ground=" + kind + " figure=" + strconv.Itoa(percent(figure)) + " figure_exact=" + strconv.Itoa(figureShare) + " tile=" + bit(tile)
	return tile, shape
}

// JPEG's noise around a flat colour, as the largest difference in any one channel, and how close a drawn colour stays
// to itself away from the letter's edges.
const flatTolerance, exactTolerance = 24, 6

// What kind of ground, never which colour: saturated, or else white, black, pale or grey by its lightness.
func groundKind(c color.NRGBA) string {
	high, low := max(c.R, c.G, c.B), min(c.R, c.G, c.B)
	switch {
	case int(high)-int(low) >= 48:
		return "saturated"
	case low >= 235:
		return "white"
	case high <= 24:
		return "black"
	case low >= 180:
		return "pale"
	default:
		return "grey"
	}
}

// The commonest colour among the chosen samples, bucketed to 16 levels a channel and averaged within its bucket.
func commonest(samples []tileSample, chosen func(tileSample) bool) (color.NRGBA, bool) {
	type total struct{ n, r, g, b int }
	buckets := map[int]*total{}
	best, bestCount := -1, 0
	for _, s := range samples {
		if !chosen(s) {
			continue
		}
		key := int(s.c.R>>4)<<8 | int(s.c.G>>4)<<4 | int(s.c.B>>4)
		t := buckets[key]
		if t == nil {
			t = &total{}
			buckets[key] = t
		}
		t.n++
		t.r += int(s.c.R)
		t.g += int(s.c.G)
		t.b += int(s.c.B)
		if t.n > bestCount {
			best, bestCount = key, t.n
		}
	}
	if best < 0 {
		return color.NRGBA{}, false
	}
	t := buckets[best]
	return color.NRGBA{uint8(t.r / t.n), uint8(t.g / t.n), uint8(t.b / t.n), 255}, true
}

func nearAny(c color.NRGBA, flats []color.NRGBA) int {
	for i, f := range flats {
		if distance(c, f) <= flatTolerance {
			return i
		}
	}
	return -1
}

func distance(a, b color.NRGBA) int {
	largest := 0
	for _, d := range []int{int(a.R) - int(b.R), int(a.G) - int(b.G), int(a.B) - int(b.B)} {
		if d < 0 {
			d = -d
		}
		largest = max(largest, d)
	}
	return largest
}

// A pixel on an antialiased edge between two of the flat colours, give or take the same noise.
func onAnyBlend(c color.NRGBA, flats []color.NRGBA) bool {
	for i := range flats {
		for j := i + 1; j < len(flats); j++ {
			for step := 1; step < 16; step++ {
				mix := color.NRGBA{
					uint8((int(flats[i].R)*(16-step) + int(flats[j].R)*step) / 16),
					uint8((int(flats[i].G)*(16-step) + int(flats[j].G)*step) / 16),
					uint8((int(flats[i].B)*(16-step) + int(flats[j].B)*step) / 16), 255,
				}
				if distance(c, mix) <= flatTolerance {
					return true
				}
			}
		}
	}
	return false
}
