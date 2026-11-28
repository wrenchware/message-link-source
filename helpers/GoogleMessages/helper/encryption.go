package main

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"google.golang.org/protobuf/encoding/protowire"
)

// Whether Google reports that this message went end-to-end encrypted. libgm names no such field. Found by comparison
// (2026-09-27, 0.1.314's per-message signals): the status of every RCS message in conversations the phone and Messages
// for Web show as encrypted carries thirdCode 1 and an undeclared field 8 of 1 or 2 (2 alongside the newer protocol's
// conversation marker), in a group only the messages after its switch to encryption do, and no SMS or MMS message ever
// does. Anything else is not a claim: an unknown value of field 8 says nothing.
func messageEncrypted(m *gmproto.Message) bool {
	status := m.GetMessageStatus()
	if status == nil || status.GetThirdCode() != 1 || m.GetType() != 4 {
		return false
	}
	protocol, ok := statusField8(status.ProtoReflect().GetUnknown())
	return ok && (protocol == 1 || protocol == 2)
}

func statusField8(raw []byte) (uint64, bool) {
	var found uint64
	seen := false
	for len(raw) > 0 {
		number, kind, n := protowire.ConsumeTag(raw)
		if n < 0 {
			return 0, false
		}
		raw = raw[n:]
		if number == 8 && kind == protowire.VarintType {
			v, m := protowire.ConsumeVarint(raw)
			if m < 0 {
				return 0, false
			}
			found, seen = v, true
			raw = raw[m:]
			continue
		}
		m := protowire.ConsumeFieldValue(number, kind, raw)
		if m < 0 {
			return 0, false
		}
		raw = raw[m:]
	}
	return found, seen
}
