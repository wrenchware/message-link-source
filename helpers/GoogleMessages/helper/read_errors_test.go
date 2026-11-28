package main

import (
	"context"
	"errors"
	"fmt"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"net/http"
	"testing"
)

func TestReadErrorPrivacy(t *testing.T) {
	for _, status := range []int{401, 403, 429, 503} {
		err := fmt.Errorf("private outer detail: %w", events.HTTPError{Resp: &http.Response{StatusCode: status}, Body: []byte("private response")})
		want := "remote_unavailable"
		if status == 401 {
			want = "authentication_required"
		} else if status == 403 {
			want = "access_denied"
		} else if status == 429 {
			want = "rate_limited"
		}
		if got := readErrorCode(err); got != want {
			t.Fatalf("status %d: %s", status, got)
		}
	}
	if readErrorCode(context.DeadlineExceeded) != "phone_unavailable" {
		t.Fatal("timeout classification")
	}
	if readErrorCode(errors.New("private upstream message")) != "history_failed" {
		t.Fatal("private detail exposed")
	}
}
