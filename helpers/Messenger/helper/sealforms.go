package main

import (
	"bytes"
	"sync"
	"sync/atomic"
)

// Every form a kept cell has been sealed in, on either platform: this build's own (protectedPrefix), Windows' before
// the seal was renamed (NBP 1, 2026-11), Windows' now (MLP 1) and the Linux server's (MSP 2). A store copied from
// another platform or an older build can hold any of them. None of the others opens here, and none is plaintext either:
// the startup migration leaves such a cell exactly as it is, and a read refuses it.
var retiredSealPrefix = []byte{'N', 'B', 'P', 1}
var knownSealPrefixes = [][]byte{{'M', 'L', 'P', 1}, retiredSealPrefix, {'M', 'S', 'P', 2}}

// What CryptProtectData output begins with: dwVersion 1, then the provider GUID df9d8cd0-1501-11d1-8c7a-00c04fc297eb.
// A cell that carries it, bare or after a four-byte prefix, is a DPAPI seal of some form even when its prefix is unknown.
var dpapiBlobHeader = []byte{1, 0, 0, 0, 0xd0, 0x8c, 0x9d, 0xdf, 0x01, 0x15, 0xd1, 0x11, 0x8c, 0x7a, 0x00, 0xc0, 0x4f, 0xc2, 0x97, 0xeb}

const (
	sealOwn     = ""
	sealPlain   = "plain"
	sealRetired = "retired-seal"
	sealUnknown = "unknown-seal"
)

// Which form a cell is in: this build's own seal, plaintext (never sealed, or empty), or a refusal reason.
func sealForm(value []byte) string {
	switch {
	case bytes.HasPrefix(value, protectedPrefix):
		return sealOwn
	case bytes.HasPrefix(value, retiredSealPrefix):
		return sealRetired
	}
	for _, prefix := range knownSealPrefixes {
		if bytes.HasPrefix(value, prefix) {
			return sealUnknown
		}
	}
	if bytes.HasPrefix(value, dpapiBlobHeader) || len(value) > 4 && bytes.HasPrefix(value[4:], dpapiBlobHeader) {
		return sealUnknown
	}
	return sealPlain
}

// Cells refused because they are sealed in a form this build does not open, by why. Each reason is logged once per
// run with its count so far; the rest are counted. Plaintext met by a read is counted under "plain".
var (
	refusedRetired, refusedUnknown, refusedPlain atomic.Int64
	refusalLogged                                sync.Map
)

func refuseSeal(reason string) {
	counter := &refusedUnknown
	switch reason {
	case sealRetired:
		counter = &refusedRetired
	case sealPlain:
		counter = &refusedPlain
	}
	count := counter.Add(1)
	if _, seen := refusalLogged.LoadOrStore(reason, true); !seen {
		logf("kept cell refused reason=%s count=%d", reason, count)
	}
}
