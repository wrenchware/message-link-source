package main

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
)

// The app's data-root override is the boundary between installed and development copies. Account folders are derived
// only after Facebook has authenticated the session and supplied its viewer id; raw account ids never become paths.
// The one earlier reader is the kept restore beside the login (keptrestore.go), which only opens a folder an earlier
// verified login created and creates none.
//
// On the Linux server everything the helper writes (each account's messages.db and e2ee.db, the export copy, its log)
// lives in MESSAGES_HELPER_PRIVATE_ROOT instead: the keys volume, off every share. whatsmeow keeps the device's Signal
// keys unsealed in e2ee.db, and messages.db keeps who talks to whom (contact, thread and message ids, times) in clear
// columns, so neither may sit on the data volume. Without the variable (the Windows app) nothing changes.
const privateRootVariable = "MESSAGES_HELPER_PRIVATE_ROOT"

func messengerStorageRoot() string {
	if root := os.Getenv(privateRootVariable); root != "" {
		return root
	}
	if root := os.Getenv("NATIVEBEEPER_DATA_ROOT"); root != "" {
		return filepath.Join(root, "Messenger")
	}
	base, err := os.UserCacheDir()
	if err != nil {
		return ""
	}
	// Only a helper started by hand gets here: the app always passes its data root. The folder is the product's own
	// name, the one the app has used since it stopped being NativeBeeper, so even a helper started by hand no longer
	// recreates the old folder beside it (2026-10-04).
	return filepath.Join(base, productFolder, "Messenger")
}

// The data folder's name under %LOCALAPPDATA%, the same one the app uses. Nothing a person can see says NativeBeeper.
const productFolder = "Messages for Windows"

// Whether an account's store an earlier server build kept in the data folder is still there. The server moves every one
// into the private root before a helper starts; one that is still there is never opened beside a new store of the same
// name, because a new e2ee.db would register a second device on the account and a new messages.db would hide the history.
func strandedInData(viewer int64, name string) bool {
	if os.Getenv(privateRootVariable) == "" {
		return false
	}
	data := os.Getenv("NATIVEBEEPER_DATA_ROOT")
	if data == "" {
		return false
	}
	_, err := os.Lstat(filepath.Join(data, "Messenger", "accounts", messengerAccountKey(viewer), name))
	if err == nil {
		logf("account store still in the data folder")
	}
	return err == nil
}

func messengerAccountFolder(viewer int64) string {
	if viewer <= 0 {
		return ""
	}
	key := messengerAccountKey(viewer)
	root := messengerStorageRoot()
	if root == "" {
		return ""
	}
	folder := filepath.Join(root, "accounts", key)
	if os.MkdirAll(folder, 0o700) != nil {
		return ""
	}
	return folder
}

func messengerAccountKey(viewer int64) string {
	sum := sha256.Sum256([]byte(fmt.Sprintf("messenger:%d", viewer)))
	return hex.EncodeToString(sum[:16])
}

func messengerAccountPath(viewer int64, name string) string {
	if viewer > 0 && strandedInData(viewer, name) {
		return ""
	}
	folder := messengerAccountFolder(viewer)
	if folder == "" {
		return ""
	}
	return filepath.Join(folder, name)
}

// The account's file when it is already there, and "" otherwise. Unlike messengerAccountPath this never creates the
// account folder, so it may be asked before Facebook has accepted the session: it finds only what an earlier verified
// login made.
func existingAccountPath(viewer int64, name string) string {
	if viewer <= 0 || strandedInData(viewer, name) {
		return ""
	}
	root := messengerStorageRoot()
	if root == "" {
		return ""
	}
	path := filepath.Join(root, "accounts", messengerAccountKey(viewer), name)
	if info, err := os.Stat(path); err != nil || !info.Mode().IsRegular() {
		return ""
	}
	return path
}

// A file of this name in an account's folder says that account's device, session and history now live on another
// machine (the move to or from a Messages Server). The app writes it after a move completes; the helper only looks for
// it. While it is there the helper neither signs in nor starts the encrypted connection for the account, so the same
// device is never live in two places and an old copy cannot register a replacement.
const movedAwayName = "moved-away.json"

// A file of this name in an account's folder says the account arrived here by a move: its device is the one this
// computer's owner had, brought over whole. If Facebook later refuses that device, nothing here registers another on
// its own — the person is told and decides.
const movedInName = "moved-in.json"

func arrivedByMove(viewer int64) bool {
	return existingAccountPath(viewer, movedInName) != ""
}

func movedAway(viewer int64) bool {
	return existingAccountPath(viewer, movedAwayName) != ""
}

func messengerLegacyPath(name string) string {
	root := messengerStorageRoot()
	if root == "" || os.MkdirAll(root, 0o700) != nil {
		return ""
	}
	return filepath.Join(root, name)
}

// Earlier builds used one unpartitioned database. It may be claimed only when the host says these cookies came from
// its already protected saved session and the helper has independently verified the same login. A fresh sign-in can
// never adopt it, so account B cannot receive account A's retained state. Failure leaves the legacy copy untouched.
func adoptLegacyDatabase(viewer int64, name string, allowed bool) string {
	target := messengerAccountPath(viewer, name)
	if target == "" || !allowed {
		return target
	}
	if _, err := os.Stat(target); err == nil || !os.IsNotExist(err) {
		return target
	}
	legacy := messengerLegacyPath(name)
	if legacy == "" {
		return target
	}
	if _, err := os.Stat(legacy); err != nil {
		return target
	}
	if err := os.Rename(legacy, target); err != nil {
		return target
	}
	for _, suffix := range []string{"-wal", "-shm"} {
		_ = os.Rename(legacy+suffix, target+suffix)
	}
	return target
}
