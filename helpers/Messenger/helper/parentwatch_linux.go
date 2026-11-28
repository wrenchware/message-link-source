//go:build linux

package main

import (
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"
)

// A helper started by the Linux server ends with it however the server ends, a crash or a SIGKILL included, rather than
// running on beside the next server on the same account's stores (SWITCHBOARD.md 4.2). End of input says the same, but
// a helper inside a lifecycle call reads no input until that call ends (dispatch.go). The server names itself in
// MESSAGES_HELPER_PARENT_PID; without it (tests, a helper started by hand) nothing here applies.
func init() {
	if given := os.Getenv("MESSAGES_HELPER_PARENT_PID"); given != "" {
		watchParent(given)
	}
}

func watchParent(given string) {
	parent, err := strconv.Atoi(given)
	if err != nil || parent < 1 {
		os.Exit(3)
	}
	// Only the server stops its helpers while it runs: it lets a send already on the wire finish first (BackendShutdown).
	// A terminal's Ctrl+C, or a stop that signals the whole group, reaches the helpers too, and would cut that send off.
	signal.Ignore(syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	// SIGKILL, since the two above are ignored. Linux sends it when the thread that started this process ends; the
	// server starts its helpers from one thread that lives as long as it does (HelperJob.Start).
	_, _, _ = syscall.RawSyscall(syscall.SYS_PRCTL, syscall.PR_SET_PDEATHSIG, uintptr(syscall.SIGKILL), 0)
	// The server may have ended before the signal was asked for, and this helper been handed to another parent already.
	// The same look every second covers a parent whose signal could not be set.
	if os.Getppid() != parent {
		os.Exit(3)
	}
	go func() {
		for range time.Tick(time.Second) {
			if os.Getppid() != parent {
				os.Exit(3)
			}
		}
	}()
}
