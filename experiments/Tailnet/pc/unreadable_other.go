//go:build !windows

package main

// unreadable: the state is sealed only on Windows, the one place this program runs.
func unreadable(error) bool { return false }
