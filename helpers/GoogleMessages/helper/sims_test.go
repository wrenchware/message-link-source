package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func syntheticSIM(id string, number int32, carrier, color, phone string) *gmproto.SIMCard {
	return &gmproto.SIMCard{
		SIMParticipant: &gmproto.SIMParticipant{ID: id},
		SIMData: &gmproto.SIMData{
			SIMPayload:  &gmproto.SIMPayload{Two: 1, SIMNumber: number},
			CarrierName: carrier, ColorHex: color,
			FormattedPhoneNumber: phone, InternationalPhoneNumber: phone,
		},
	}
}

func TestSIMChoicesNumberTwoOrMoreSIMsAndNeverCarryTheirNumbers(t *testing.T) {
	one := simChoices([]*gmproto.SIMCard{syntheticSIM("p1", 1, "Carrier A", "#00ff00", "+1 555-010-0001")})
	if one == nil || len(one) != 0 {
		t.Fatalf("a phone with one SIM must be told an empty list, got %+v", one)
	}
	// The same SIM listed twice is still one SIM.
	if twice := simChoices([]*gmproto.SIMCard{syntheticSIM("p1", 1, "A", "", ""), syntheticSIM("p1", 1, "A", "", "")}); len(twice) != 0 {
		t.Fatalf("a repeated SIM made a dual-SIM phone: %+v", twice)
	}
	if blank := simChoices([]*gmproto.SIMCard{syntheticSIM("", 1, "A", "", ""), syntheticSIM("p2", 2, "B", "", ""), nil}); len(blank) != 0 {
		t.Fatalf("a SIM without an id cannot be looked up and does not count: %+v", blank)
	}
	got := simChoices([]*gmproto.SIMCard{
		syntheticSIM("p9", 7, "Second Carrier", "1a2b3c", "+1 555-010-0002"),
		syntheticSIM("p4", 3, "", "not a colour", "+1 555-010-0001"),
	})
	want := []simChoice{{ID: "p4", Slot: 1}, {ID: "p9", Slot: 2, Carrier: "Second Carrier", Color: "#1A2B3C"}}
	if len(got) != 2 || got[0] != want[0] || got[1] != want[1] {
		t.Fatalf("expected SIMs numbered by Google's SIM order: %+v", got)
	}
	encoded, _ := json.Marshal(got)
	if strings.Contains(string(encoded), "555") || strings.Contains(string(encoded), "010") {
		t.Fatalf("a SIM's phone number must never leave the helper: %s", encoded)
	}
}

func TestCarrierLabelDropsNumbersAndControlCharacters(t *testing.T) {
	for name, want := range map[string]string{
		"  Carrier A  ":         "Carrier A",
		"T-Mobile":              "T-Mobile",
		"Work 5G":               "Work 5G",
		"+1 555 010 0001":       "",
		"My 5550100":            "",
		"Line\x00\x1bName":      "LineName",
		strings.Repeat("x", 40): strings.Repeat("x", 32),
		strings.Repeat("é", 40): strings.Repeat("é", 32),
		"":                      "",
	} {
		if got := carrierLabel(name); got != want {
			t.Fatalf("carrierLabel(%q) = %q, want %q", name, got, want)
		}
	}
	for hex, want := range map[string]string{"#a1b2c3": "#A1B2C3", "A1B2C3": "#A1B2C3", "#fff": "", "#ff000000": "", "#gg0000": "", "": ""} {
		if got := simColor(hex); got != want {
			t.Fatalf("simColor(%q) = %q, want %q", hex, got, want)
		}
	}
}

func TestSIMEventIsSentFirstThenOnlyOnChange(t *testing.T) {
	var b bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&b)}}
	dual := &gmproto.Settings{SIMCards: []*gmproto.SIMCard{syntheticSIM("p1", 1, "A", "", "+15550100001"), syntheticSIM("p2", 2, "B", "", "+15550100002")}}
	single := &gmproto.Settings{SIMCards: []*gmproto.SIMCard{syntheticSIM("p1", 1, "A", "", "+15550100001")}}
	a.handleEvent(dual)
	a.handleEvent(dual)
	a.handleEvent(&gmproto.Settings{})
	a.handleEvent(single)
	a.handleEvent(single)
	lines := strings.Split(strings.TrimSpace(b.String()), "\n")
	if len(lines) != 2 ||
		lines[0] != `{"event":"sims","result":{"sims":[{"id":"p1","slot":1,"carrier":"A"},{"id":"p2","slot":2,"carrier":"B"}]}}` ||
		lines[1] != `{"event":"sims","result":{"sims":[]}}` {
		t.Fatalf("expected the dual-SIM list once and then its removal once: %q", b.String())
	}
	if strings.Contains(b.String(), "555") {
		t.Fatal("a SIM's phone number reached the app")
	}
	// The send path still finds every SIM by its participant id, a single one included.
	if a.sims["p1"] == nil || len(a.sims) != 1 {
		t.Fatalf("the send path's SIMs were not kept: %d", len(a.sims))
	}
	// A restarted helper tells the app even a single SIM, so nothing a previous helper said is left standing.
	var fresh bytes.Buffer
	b2 := &adapter{out: &output{encoder: json.NewEncoder(&fresh)}}
	b2.handleEvent(single)
	if strings.TrimSpace(fresh.String()) != `{"event":"sims","result":{"sims":[]}}` {
		t.Fatalf("a fresh helper must say what it knows at once: %q", fresh.String())
	}
}
