//go:build !windows

package tailnet

import "tailscale.com/ipn"

// stateStore is tsnet's own file store everywhere but Windows (nil): on Android the node's folder is the app's own,
// which no other app can read (store_windows.go seals it on Windows).
func stateStore(string) (ipn.StateStore, error) { return nil, nil }
