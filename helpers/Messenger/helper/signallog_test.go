package main

import (
	"errors"
	"io"
	"os"
	"testing"

	signallogger "go.mau.fi/libsignal/logger"
)

func TestSignalLibraryLeavesTheAppPipeAlone(t *testing.T) {
	read, write, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	real := os.Stdout
	os.Stdout = write
	signallogger.Warning(errors.New("failed to verify ciphertext MAC"))
	signallogger.Error("Unable to verify ciphertext mac: ", errors.New("bad"))
	signallogger.Info("info")
	signallogger.Debug("debug")
	os.Stdout = real
	_ = write.Close()
	said, err := io.ReadAll(read)
	if err != nil {
		t.Fatal(err)
	}
	if len(said) != 0 {
		t.Fatalf("the library wrote to standard output: %d bytes", len(said))
	}
}
