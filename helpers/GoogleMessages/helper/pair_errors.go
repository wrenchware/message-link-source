package main

import (
	"errors"
	"regexp"
	"strconv"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/events"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// Why a pairing did not start or finish, as a fixed code, following upstream's reading (pkg/connector/login.go). Every
// failure used to be pair_start_failed or pair_finish_failed, which hid the one thing worth knowing: Google's own error
// numbers. 32, 33 and 35 are client attestation, which a Google Messages beta began requiring in August 2026
// (mautrix/gmessages#91); seeing one says the phone's app refuses this kind of client, not that anything was mistyped,
// and that trying again will not help.
func pairStartCode(err error) string {
	var request events.RequestError
	switch {
	case errors.Is(err, libgm.ErrNoDevicesFound):
		return "pair_no_devices"
	case errors.Is(err, libgm.ErrPairingInitTimeout) && errors.Is(err, libgm.ErrHadMultipleDevices):
		return "pair_multiple_devices"
	case errors.Is(err, libgm.ErrPairingInitTimeout):
		return "pair_phone_not_responding"
	case errors.Is(err, events.ErrCallerNoPermission):
		return "pair_no_permission"
	case errors.As(err, &request) && request.Data != nil:
		return "pair_start_google_" + strconv.Itoa(int(request.Data.Type))
	}
	return "pair_start_failed"
}

// The finish step's timeouts (Google's codes 2, 3 and 6) arrive as "pairing timed out (code: type/code)"; only the two
// numbers are kept.
var finishNumbers = regexp.MustCompile(`code: (\d{1,4})/(\d{1,4})`)

// Every other refusal of the finish step, attestation among them, arrives as "unknown error pairing: type/NAME", NAME
// being Google's code by its name in the library's list (FinishGaiaPairing), or its number for one the list lacks.
var finishUnknown = regexp.MustCompile(`unknown error pairing: -?\d{1,10}/([A-Z0-9_]{1,64})\b`)

func pairFinishCode(err error) string {
	var request events.RequestError
	switch {
	case errors.Is(err, libgm.ErrIncorrectEmoji):
		return "pair_wrong_emoji"
	case errors.Is(err, libgm.ErrPairingCancelled):
		return "pair_cancelled"
	case errors.Is(err, libgm.ErrPairingTimeout):
		if found := finishNumbers.FindStringSubmatch(err.Error()); found != nil {
			return "pair_refused_" + found[1] + "_" + found[2]
		}
		return "pair_timeout"
	case errors.As(err, &request) && request.Data != nil:
		return "pair_finish_google_" + strconv.Itoa(int(request.Data.Type))
	}
	if found := finishUnknown.FindStringSubmatch(err.Error()); found != nil {
		if code, known := finishErrorCode(found[1]); known {
			switch code {
			case gmproto.GaiaPairingErrorCode_CLIENT_ATTESTATION_MISSING,
				gmproto.GaiaPairingErrorCode_CLIENT_ATTESTATION_MISMATCH,
				gmproto.GaiaPairingErrorCode_CLIENT_ATTESTATION_REVISION_MISMATCH:
				return "pair_attestation_" + strconv.Itoa(int(code))
			}
			return "pair_failed_code_" + strconv.Itoa(int(code))
		}
	}
	return "pair_finish_failed"
}

// Google's code from its name in the library's list, or from its number (up to four digits) when the list lacks it.
func finishErrorCode(name string) (gmproto.GaiaPairingErrorCode, bool) {
	if number, known := gmproto.GaiaPairingErrorCode_value[name]; known {
		return gmproto.GaiaPairingErrorCode(number), true
	}
	if number, err := strconv.Atoi(name); err == nil && len(name) <= 4 && number >= 0 {
		return gmproto.GaiaPairingErrorCode(number), true
	}
	return 0, false
}
