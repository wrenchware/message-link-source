package main

import (
	"context"
	"database/sql"
	"path/filepath"
	"testing"

	"go.mau.fi/whatsmeow/store/sqlstore"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// The number the app compares a server's device database with (2026-10-12) must be the one
// this build's store writes and the line past which it refuses to open one: read from a store the library made itself,
// not from the upgrade list alone.
func TestDeviceSchemaIsWhatTheStoreRecords(t *testing.T) {
	path := filepath.Join(t.TempDir(), "e2ee.db")
	address := "file:" + path + "?_pragma=foreign_keys(1)"
	devices, err := sqlstore.New(context.Background(), "sqlite", address, waLog.Noop)
	if err != nil {
		t.Fatal(err)
	}
	if err := devices.Close(); err != nil {
		t.Fatal(err)
	}
	db, err := sql.Open("sqlite", "file:"+path)
	if err != nil {
		t.Fatal(err)
	}
	// Closed on every way out, so Windows can remove the folder after a failure too.
	t.Cleanup(func() { _ = db.Close() })
	var version int
	if err := db.QueryRow(`SELECT version FROM whatsmeow_version LIMIT 1`).Scan(&version); err != nil {
		t.Fatal(err)
	}
	if version != deviceSchema() || version < 1 {
		t.Fatalf("a fresh store records version %d, the helper reports %d", version, deviceSchema())
	}

	// One past it, as a newer helper that declared no compatibility would leave it: this build refuses to open it,
	// which is the failure the app's check keeps from happening on the PC.
	if _, err := db.Exec(`UPDATE whatsmeow_version SET version = ?, compat = ?`, version+1, version+1); err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	if newer, err := sqlstore.New(context.Background(), "sqlite", address, waLog.Noop); err == nil {
		_ = newer.Close()
		t.Fatal("a store one schema past this build was opened")
	}
}

// hello carries the number, offline too: the app reads it from the helper it ships, before a move back holds anything.
func TestHelloReportsTheDeviceSchema(t *testing.T) {
	moveRoot(t)
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "1")
	a := moveAdapter(t)
	result, code := moveCall(t, a, "hello", "")
	if code != "" {
		t.Fatalf("hello answered %q", code)
	}
	hello := result.(map[string]any)
	if hello["offline"] != true || hello["deviceSchema"] != deviceSchema() {
		t.Fatalf("hello said deviceSchema=%v offline=%v", hello["deviceSchema"], hello["offline"])
	}
}
