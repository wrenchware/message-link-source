package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// tsnet applies Hostname on every start. Keep the default chosen before its first start,
// so a brand change never renames an existing node or changes its DNS address.
func defaultNodeName(dir string) (string, error) {
	path := filepath.Join(dir, "node-name")
	if data, err := os.ReadFile(path); err == nil {
		name := strings.TrimSpace(string(data))
		if !labelPattern.MatchString(name) {
			return "", fmt.Errorf("invalid saved node name")
		}
		return name, nil
	} else if !os.IsNotExist(err) {
		return "", err
	}
	name := "messagelink-server"
	for _, file := range []string{"tailscaled.state", "tailscaled.state.sealed"} {
		if _, err := os.Stat(filepath.Join(dir, file)); err == nil {
			name = "messages-server"
		} else if !os.IsNotExist(err) {
			return "", err
		}
	}
	if err := os.MkdirAll(dir, 0700); err != nil {
		return "", err
	}
	// This is a public DNS label only; no node keys or saved session are opened.
	if err := os.WriteFile(path+".tmp", []byte(name), 0600); err != nil {
		return "", err
	}
	if err := os.Rename(path+".tmp", path); err != nil {
		return "", err
	}
	return name, nil
}
