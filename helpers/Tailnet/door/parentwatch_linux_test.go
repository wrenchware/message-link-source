//go:build linux

package main

import (
	"errors"
	"os"
	"os/exec"
	"strconv"
	"testing"
)

// The watch is in the door as built: this test binary is started again with no tests to run, so only init runs.
// Named after this process, it runs to the end; named after anything else, or nothing a pid can be, it stops at once.
func TestParentWatch(t *testing.T) {
	run := func(parent string) int {
		child := exec.Command(os.Args[0], "-test.run=^$")
		child.Env = append(os.Environ(), "MESSAGES_HELPER_PARENT_PID="+parent)
		err := child.Run()
		var exit *exec.ExitError
		if errors.As(err, &exit) {
			return exit.ExitCode()
		}
		if err != nil {
			t.Fatalf("start: %v", err)
		}
		return 0
	}
	if code := run(strconv.Itoa(os.Getpid())); code != 0 {
		t.Fatalf("a helper started by its server stopped: exit %d", code)
	}
	for _, other := range []string{"1", "0", "-5", "server"} {
		if code := run(other); code != 3 {
			t.Fatalf("a door told its server is %q ran on: exit %d", other, code)
		}
	}
}
