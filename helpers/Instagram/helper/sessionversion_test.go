package main

import (
	"context"
	"encoding/json"
	"testing"
	"time"
)

// The envelope format the app compares a server's with before a move back (SWITCHBOARD.md slice 13, 2026-10-12): the
// one this build resumes, and a newer one is refused here, which is the failure the app's check keeps off the PC. The
// app's own copy of the number (InstagramEnvelope.Version) is pinned to savedSessionVersion by a harness check.
func TestOnlyThisBuildsEnvelopeFormatResumes(t *testing.T) {
	values := fakeCookies()
	values["ds_user_id"] = "123"
	for _, test := range []struct {
		version int
		want    string
	}{
		{savedSessionVersion, ""},
		{savedSessionVersion + 1, "identity_mismatch"},
	} {
		a, _, _ := testReadAdapter()
		saved := savedSession{Version: test.version, AccountKey: accountKey("123"), Cookies: values, State: syntheticState("123", time.Now()), SeqID: 42, SeqTS: time.Now()}
		params, _ := json.Marshal(map[string]any{"session": saved})
		if _, code := a.call(context.Background(), request{Method: "login", Params: params}); code != test.want {
			t.Fatalf("an envelope of format %d answered %q, want %q", test.version, code, test.want)
		}
		a.close()
	}
}
