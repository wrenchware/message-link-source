// tailnet-helper is Message Link for Windows' own Tailscale node: the PC joins the owner's tailnet itself, as the phone does
// (android/tailnet, whose package this runs), without the Tailscale app, a driver or a service, and reaches the server's door
// through it when it is away from home. The app's connections go through the package's local door on 127.0.0.1, so the
// server's TLS, its pinned certificate and the device token stay the app's, end to end.
//
//	tailnet-helper.exe --state <absolute folder> --hostname <one DNS label>
//
// The app writes the door's secret (64 hex characters) as the first line of the input, then one JSON request per line; the
// output is JSON lines only (run.go). The input ending stops the node. Exit codes: 0 stopped, 1 failed, 2 bad arguments,
// 3 signed out (the app removes the state folder), 4 the saved state cannot be read.
package main

import (
	"os"
	"time"

	tailnet "nativebeeper.local/messages-android-tailnet"
)

func main() {
	// The output is the app's to read, one JSON line at a time, so nothing else may write there: anything in Tailscale that
	// prints to os.Stdout goes nowhere. A crash still reaches stderr.
	stdout := os.Stdout
	if null, err := os.OpenFile(os.DevNull, os.O_WRONLY, 0); err == nil {
		os.Stdout = null
	}
	os.Exit(run(os.Args[1:], os.Stdin, stdout, tsNode{}, time.Second))
}

// tsNode is the node of the package, as run sees it.
type tsNode struct{}

func (tsNode) OpenDoor(secret []byte) (int, error) { return tailnet.OpenDoor(secret) }
func (tsNode) Start(dir, hostname string) error    { return tailnet.Start(dir, hostname) }
func (tsNode) Unreadable(err error) bool           { return unreadable(err) }
func (tsNode) Status() string                      { return tailnet.Status() }
func (tsNode) Peers() (string, error)              { return tailnet.Peers() }
func (tsNode) Path(name, id string) string         { return tailnet.Path(name, id) }
func (tsNode) Login() error                        { return tailnet.Login() }
func (tsNode) NetworkChanged()                     { tailnet.NetworkChanged() }
func (tsNode) Logout() error                       { return tailnet.Logout() }
func (tsNode) Stop() error                         { return tailnet.Stop() }
