package main

import (
	"bytes"
	"context"
	"encoding/json"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func settingsWith(sims bool, rcs *gmproto.RCSSettings) *gmproto.Settings {
	s := &gmproto.Settings{RCSSettings: rcs}
	if sims {
		s.SIMCards = []*gmproto.SIMCard{{SIMParticipant: &gmproto.SIMParticipant{ID: "synthetic-sim"}}}
	}
	return s
}

func TestSettingsEventSaysOnlyWhatArrived(t *testing.T) {
	yes, no := true, false
	if s := settingsEvent(settingsWith(true, &gmproto.RCSSettings{IsDefaultSMSApp: &no})); s == nil || s.DefaultSMSApp {
		t.Fatal("a phone that says it is not the default was not reported")
	}
	if s := settingsEvent(settingsWith(true, &gmproto.RCSSettings{IsDefaultSMSApp: &yes})); s == nil || !s.DefaultSMSApp {
		t.Fatal("a phone that says it is the default was not reported")
	}
	// Absent is unknown, never false: upstream notes the field disappears from some settings events.
	for name, s := range map[string]*gmproto.Settings{
		"field absent":    settingsWith(true, &gmproto.RCSSettings{IsEnabled: true}),
		"no RCS settings": settingsWith(true, nil),
		"no SIM cards":    settingsWith(false, &gmproto.RCSSettings{IsDefaultSMSApp: &no}),
		"nil":             nil,
	} {
		if settingsEvent(s) != nil {
			t.Fatalf("%s produced a default-SMS answer", name)
		}
	}
}

func TestSettingsEventReachesTheApp(t *testing.T) {
	var b bytes.Buffer
	a := &adapter{out: &output{encoder: json.NewEncoder(&b)}}
	no := false
	a.handleEvent(settingsWith(true, &gmproto.RCSSettings{IsDefaultSMSApp: &no}))
	a.handleEvent(settingsWith(true, &gmproto.RCSSettings{}))
	lines := []string{}
	for _, line := range strings.Split(strings.TrimSpace(b.String()), "\n") {
		// The SIM list has its own event (sims_test.go).
		if !strings.HasPrefix(line, `{"event":"sims"`) {
			lines = append(lines, line)
		}
	}
	if len(lines) != 1 || lines[0] != `{"event":"settings","result":{"defaultSmsApp":false}}` {
		t.Fatalf("unexpected settings output: %q", b.String())
	}
	// The SIMs the send path needs are still taken from the same event.
	if a.sims["synthetic-sim"] == nil {
		t.Fatal("SIM cards were not kept")
	}
}

func TestTopContactsKeepThePhonesOrder(t *testing.T) {
	number := func(n string) *gmproto.ContactNumber { return &gmproto.ContactNumber{Number: n} }
	mapped := mapContacts([]*gmproto.Contact{
		{Name: "Second Name", Number: number("+1 202 555 0102")},
		{Name: "First Name", Number: number("+12025550101")},
		{Name: "Duplicate", Number: number("+1 (202) 555-0102")},
		{Name: "No Number", Number: number("")},
		{Number: &gmproto.ContactNumber{Number2: "2025550103"}},
	})
	if len(mapped) != 3 || mapped[0].Phone != "+12025550102" || mapped[0].Name != "Second Name" || mapped[1].Phone != "+12025550101" ||
		mapped[2].Phone != "2025550103" || mapped[2].Name != "2025550103" {
		t.Fatalf("top contacts mapped wrongly: %+v", mapped)
	}
}

func TestTopContactsNeedAPairing(t *testing.T) {
	a := &adapter{}
	if _, code := a.callContext(context.Background(), request{ID: 1, Method: "ui.topContacts"}); code != "pairing_required" {
		t.Fatalf("unpaired top contacts answered %q", code)
	}
	// A read, never queued behind or counted with contact-visible actions.
	if mutationMethod("ui.topContacts") || lifecycleMethod("ui.topContacts") {
		t.Fatal("top contacts classified as a mutation or lifecycle call")
	}
}
