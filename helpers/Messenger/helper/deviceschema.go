package main

import "go.mau.fi/whatsmeow/store/sqlstore/upgrades"

// The newest schema of the encrypted-device database (e2ee.db) this build can open, as whatsmeow counts it: the length
// of its upgrade list, which is the number its store writes into whatsmeow_version.version once it is up to date. A
// store that records a higher number was written by a newer build, and whatsmeow refuses to open it unless that build
// declared it compatible.
//
// 2026-10-12: the server updates itself nightly from main while this PC updates only when released, so a move back can
// bring home a device database a newer helper has upgraded. The app asks hello for this number and refuses such a
// store before anything is changed (SWITCHBOARD.md slice 13). It is read from the library, never kept by hand, so it
// cannot drift from what this build actually opens.
func deviceSchema() int {
	return len(upgrades.Table)
}
