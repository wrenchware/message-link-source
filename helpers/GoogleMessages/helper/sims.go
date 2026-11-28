package main

import (
	"cmp"
	"slices"
	"strings"
	"unicode"
	"unicode/utf8"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// Which SIM a conversation sends from, for a phone with more than one (2026-10-04). A conversation names its SIM by the
// SIM's own participant id (Conversation.defaultOutgoingID, the id the send path already matches against the phone's SIM
// list), so the app is told the phone's SIMs once, keyed by that id, and looks each conversation up itself. That keeps a
// conversation listed before the phone's settings arrive from showing nothing until it is listed again.
//
// Only the SIM's place among the phone's SIMs, its carrier name and its colour leave the helper: never its phone number,
// which SIMData also carries. A phone with one SIM is told an empty list, and the app shows nothing.
type simChoice struct {
	ID      string `json:"id"`
	Slot    int    `json:"slot"`
	Carrier string `json:"carrier,omitempty"`
	Color   string `json:"color,omitempty"`
}

// The SIMs a settings event lists, numbered 1, 2, ... in the order of Google's SIM number. Android's SIM number is a
// subscription id rather than the tray slot, so the numbering is the phone's order of its SIMs, which on a phone that
// has kept the same SIMs is the slot order. Empty unless at least two distinct SIMs are listed.
func simChoices(cards []*gmproto.SIMCard) []simChoice {
	type listed struct {
		card   *gmproto.SIMCard
		number int32
	}
	seen := map[string]bool{}
	found := []listed{}
	for _, card := range cards {
		id := card.GetSIMParticipant().GetID()
		if id == "" || len(id) > 64 || seen[id] {
			continue
		}
		seen[id] = true
		found = append(found, listed{card, card.GetSIMData().GetSIMPayload().GetSIMNumber()})
	}
	if len(found) < 2 {
		return []simChoice{}
	}
	slices.SortStableFunc(found, func(a, b listed) int {
		if c := cmp.Compare(a.number, b.number); c != 0 {
			return c
		}
		return strings.Compare(a.card.GetSIMParticipant().GetID(), b.card.GetSIMParticipant().GetID())
	})
	choices := make([]simChoice, 0, len(found))
	for i, f := range found {
		data := f.card.GetSIMData()
		choices = append(choices, simChoice{
			ID:      f.card.GetSIMParticipant().GetID(),
			Slot:    i + 1,
			Carrier: carrierLabel(data.GetCarrierName()),
			Color:   simColor(data.GetColorHex()),
		})
	}
	return choices
}

// The carrier's name as the phone gives it, cut to a chip's length. Android lets a SIM be renamed, so a name that looks
// like a number is left out rather than risk showing the SIM's own number.
func carrierLabel(name string) string {
	var b strings.Builder
	digits := 0
	for _, r := range strings.TrimSpace(name) {
		if unicode.IsControl(r) || r == utf8.RuneError {
			continue
		}
		if unicode.IsDigit(r) {
			digits++
		}
		b.WriteRune(r)
	}
	label := strings.TrimSpace(b.String())
	if digits >= 5 {
		return ""
	}
	if utf8.RuneCountInString(label) > 32 {
		label = strings.TrimSpace(string([]rune(label)[:32]))
	}
	return label
}

// The SIM's colour as #RRGGBB, or nothing for anything else.
func simColor(hex string) string {
	hex = strings.TrimPrefix(strings.TrimSpace(hex), "#")
	if len(hex) != 6 {
		return ""
	}
	for _, r := range hex {
		if !strings.ContainsRune("0123456789abcdefABCDEF", r) {
			return ""
		}
	}
	return "#" + strings.ToUpper(hex)
}

// Keeps the SIMs a settings event lists for the send path, and says whether the app should be told about them: on the
// first event that lists any, so a restarted helper replaces what the app held, and after that only when they changed.
func (a *adapter) noteSIMs(cards []*gmproto.SIMCard) ([]simChoice, bool) {
	choices := simChoices(cards)
	a.simMu.Lock()
	defer a.simMu.Unlock()
	a.sims = make(map[string]*gmproto.SIMCard)
	for _, sim := range cards {
		a.sims[sim.GetSIMParticipant().GetID()] = sim
	}
	if a.simsTold && slices.Equal(choices, a.simsShown) {
		return choices, false
	}
	a.simsTold, a.simsShown = true, choices
	return choices, true
}
