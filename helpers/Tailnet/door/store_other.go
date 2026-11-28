//go:build !windows

package main

import "tailscale.com/ipn"

// newStateStore is tsnet's own file store everywhere but Windows (nil): the Linux server keeps the door's state in its keys
// volume, which only the server's user can read (store_windows.go seals it on Windows).
func newStateStore(string) (ipn.StateStore, error) { return nil, nil }
