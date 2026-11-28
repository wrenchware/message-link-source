//go:build linux

package main

import (
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"
)

// The door ends with the server however the server ends, a crash or a SIGKILL included: a door left behind would hold
// the tailnet node beside the next server's door, which starts from the same state. End of input says the same when
// the server stops on purpose (main.go). The server names itself in MESSAGES_HELPER_PARENT_PID; without it (tests, a
// door started by hand) nothing here applies. The same watch as the helpers' (helpers/Instagram/helper/parentwatch_linux.go).
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
	// Only the server stops the door, by ending its input after the connections it carries have been answered. A
	// terminal's Ctrl+C, or a stop that signals the whole group, would cut those answers off.
	signal.Ignore(syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	// SIGKILL, since the two above are ignored. Linux sends it when the thread that started this process ends; the
	// server starts its children from one thread that lives as long as it does (HelperJob.Start).
	_, _, _ = syscall.RawSyscall(syscall.SYS_PRCTL, syscall.PR_SET_PDEATHSIG, uintptr(syscall.SIGKILL), 0)
	// The server may have ended before the signal was asked for, and this door been handed to another parent already.
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
