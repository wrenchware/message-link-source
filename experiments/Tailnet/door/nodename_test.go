package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestDefaultNodeNameRetainedAcrossStateCreation(t *testing.T) {
	for _, old := range []string{"", "tailscaled.state", "tailscaled.state.sealed"} {
		t.Run(old, func(t *testing.T) {
			dir := t.TempDir()
			want := "messagelink-server"
			if old != "" {
				os.WriteFile(filepath.Join(dir, old), []byte("untouched"), 0600)
				want = "messages-server"
			}
			name, err := defaultNodeName(dir)
			if err != nil || name != want {
				t.Fatalf("first: %q %v", name, err)
			}
			os.WriteFile(filepath.Join(dir, "tailscaled.state"), []byte("untouched"), 0600)
			name, err = defaultNodeName(dir)
			if err != nil || name != want {
				t.Fatalf("restart: %q %v", name, err)
			}
			if data, _ := os.ReadFile(filepath.Join(dir, "tailscaled.state")); string(data) != "untouched" {
				t.Fatal("node state changed")
			}
		})
	}
}
