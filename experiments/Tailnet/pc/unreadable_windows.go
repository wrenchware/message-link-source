//go:build windows

package main

import (
	"errors"

	tailnet "nativebeeper.local/messages-android-tailnet"
)

// unreadable is a start that failed because the node's sealed state is there and cannot be opened.
func unreadable(err error) bool { return errors.Is(err, tailnet.ErrStateUnreadable) }
