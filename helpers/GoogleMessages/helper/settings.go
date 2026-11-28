package main

import "go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"

// What the app is told about the phone's own settings. Only whether Google Messages is the phone's default SMS app,
// because that is the one setting that changes what this app can do: a phone where it is not the default refuses every
// send (upstream's SendMessageResponse FAILURE_4, "Google Messages is not your default SMS app"), and without this the
// refusal looked like a connection problem.
type viewSettings struct {
	DefaultSMSApp bool `json:"defaultSmsApp"`
}

// The settings event reduced to what the app shows, or nil when it says nothing about the default SMS app. Read the
// way upstream reads it (pkg/connector/handlegmessages.go mergeSettings/handleSettings): an event without SIM cards is
// ignored, and the field has explicit presence because some events leave it out, which upstream notes would make a
// stored value flap if absence were read as false. Absence here is "unknown", and the app says nothing for unknown.
func settingsEvent(s *gmproto.Settings) *viewSettings {
	if s == nil || s.SIMCards == nil {
		return nil
	}
	rcs := s.GetRCSSettings()
	if rcs == nil || rcs.IsDefaultSMSApp == nil {
		return nil
	}
	return &viewSettings{DefaultSMSApp: rcs.GetIsDefaultSMSApp()}
}
