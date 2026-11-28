//go:build windows

package main

import (
	"errors"
	"os"
	"path/filepath"
	"sync"
	"unsafe"

	"golang.org/x/sys/windows"
	"tailscale.com/ipn"
	"tailscale.com/ipn/store/mem"
)

// sealedStateFile holds the node's state (its machine and node keys, which are the door's identity on the tailnet) sealed
// to the signed-in Windows user with DPAPI (SWITCHBOARD.md 10.7). The Windows server runs as that user, in that user's data
// folder: another account on the PC, or a copy of the folder taken to another machine, cannot open it and so cannot be this
// node. tsnet's own store would keep the same state as plain JSON beside it.
const sealedStateFile = "tailscaled.state.sealed"

type sealedStore struct {
	path string
	mu   sync.Mutex
	mem  *mem.Store
}

// newStateStore opens the sealed state in dir, or starts an empty one. A file that is there and cannot be unsealed (another
// user's, or damaged) is an error, never an empty state: a new node would be signed in beside the old one.
func newStateStore(dir string) (ipn.StateStore, error) {
	store := &sealedStore{path: filepath.Join(dir, sealedStateFile), mem: new(mem.Store)}
	sealed, err := os.ReadFile(store.path)
	if errors.Is(err, os.ErrNotExist) {
		return store, nil
	}
	if err != nil {
		return nil, err
	}
	plain, err := unseal(sealed)
	if err != nil {
		return nil, err
	}
	if err := store.mem.LoadFromJSON(plain); err != nil {
		return nil, err
	}
	return store, nil
}

func (s *sealedStore) String() string { return "sealedStore" }

func (s *sealedStore) ReadState(id ipn.StateKey) ([]byte, error) { return s.mem.ReadState(id) }

// WriteState keeps the whole state sealed after each change: written beside the file, then moved over it, so a stop half
// way leaves the last whole state.
func (s *sealedStore) WriteState(id ipn.StateKey, bs []byte) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.mem.WriteState(id, bs); err != nil {
		return err
	}
	plain, err := s.mem.ExportToJSON()
	if err != nil {
		return err
	}
	sealed, err := seal(plain)
	if err != nil {
		return err
	}
	temporary := s.path + ".tmp"
	if err := os.WriteFile(temporary, sealed, 0o600); err != nil {
		return err
	}
	return os.Rename(temporary, s.path)
}

func seal(plain []byte) ([]byte, error) {
	var in, out windows.DataBlob
	in.Size = uint32(len(plain))
	if len(plain) > 0 {
		in.Data = &plain[0]
	}
	if err := windows.CryptProtectData(&in, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return append([]byte(nil), unsafe.Slice(out.Data, out.Size)...), nil
}

func unseal(sealed []byte) ([]byte, error) {
	if len(sealed) == 0 {
		return nil, errors.New("empty state")
	}
	var in, out windows.DataBlob
	in.Size = uint32(len(sealed))
	in.Data = &sealed[0]
	if err := windows.CryptUnprotectData(&in, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return append([]byte(nil), unsafe.Slice(out.Data, out.Size)...), nil
}
