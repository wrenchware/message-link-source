package main

import (
	"context"
	"errors"
	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"net"
	"strings"
)

// Never return upstream URLs, response bodies, identifiers or credential details.
func readErrorCode(err error) string {
	var httpErr events.HTTPError
	if errors.As(err, &httpErr) && httpErr.Resp != nil {
		switch httpErr.Resp.StatusCode {
		case 401:
			return "authentication_required"
		case 403:
			return "access_denied"
		case 429:
			return "rate_limited"
		default:
			return "remote_unavailable"
		}
	}
	if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, libgm.ErrPhoneNotResponding) {
		return "phone_unavailable"
	}
	if errors.Is(err, libgm.ErrConnectionClosed) {
		return "connection_closed"
	}
	var network net.Error
	if errors.As(err, &network) {
		return "network_unavailable"
	}
	if err != nil && strings.HasPrefix(err.Error(), "unexpected response type") {
		return "unexpected_response"
	}
	return "history_failed"
}
