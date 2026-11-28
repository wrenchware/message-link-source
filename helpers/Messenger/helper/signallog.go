package main

import signallogger "go.mau.fi/libsignal/logger"

// Left to itself the Signal library prints its warnings to standard output, which here is the pipe to the app. One
// message that would not decrypt put a line that isn't a frame on it and cost the helper its connection (2026-11-08).
// Only where it spoke is kept: its words can carry a remote address.
type signalLog struct{}

func (signalLog) Debug(caller, message string)   {}
func (signalLog) Info(caller, message string)    {}
func (signalLog) Warning(caller, message string) { logf("signal warning at=%s", caller) }
func (signalLog) Error(caller, message string)   { logf("signal error at=%s", caller) }
func (signalLog) Configure(settings string)      {}

func init() {
	var quiet signallogger.Loggable = signalLog{}
	signallogger.Setup(&quiet)
}
