package main

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"errors"
	"io"
	"os"
	"sync/atomic"
)

// Linux server stores are intentionally distinct from Windows DPAPI stores; copying a Windows device is not a migration.
// The prefix and associated data differ from the .NET host's sealed values (FileKeyStateProtector) so the two formats
// can never be mistaken for each other.
var protectedPrefix = []byte{'M', 'L', 'S', 1}
var protectedEntropy = []byte("Message Link server Messenger retained history v1")

// The seal before the Message Link name (2026-11-28): the same key and layout under another prefix and associated data.
// Only the startup migration opens it (protectExisting), to seal each such cell again in the form above; a read refuses
// it, since by then no store holds one.
var previousSealPrefix = []byte{'M', 'S', 'P', 2}
var previousEntropy = []byte("Messages server Messenger retained history v2")

// The key is read for every operation and zeroed straight after, so the file's mode is checked on the open handle (not
// by path): a loosened or swapped file takes effect at once and cannot change between the check and the read.
func protectionKey() ([]byte, error) {
	path := os.Getenv("MESSAGES_HELPER_KEY_FILE")
	if path == "" {
		return nil, errors.New("server protection key is required")
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, errors.New("server protection key must be a private file")
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() || info.Mode().Perm()&0077 != 0 {
		return nil, errors.New("server protection key must be a private file")
	}
	// One byte past the key length, so a longer file is rejected without reading all of it.
	key, err := io.ReadAll(io.LimitReader(file, 33))
	if err != nil || len(key) != 32 {
		clear(key)
		return nil, errors.New("server protection key is invalid")
	}
	return key, nil
}

func protectionAEAD() (cipher.AEAD, error) {
	key, err := protectionKey()
	if err != nil {
		return nil, err
	}
	defer clear(key)
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}

func protectLocal(value []byte) ([]byte, error) {
	gcm, err := protectionAEAD()
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err = rand.Read(nonce); err != nil {
		return nil, err
	}
	result := make([]byte, 0, len(protectedPrefix)+len(nonce)+len(value)+gcm.Overhead())
	result = append(append(result, protectedPrefix...), nonce...)
	return gcm.Seal(result, nonce, value, protectedEntropy), nil
}

func unprotectLocal(value []byte) ([]byte, error) {
	if !bytes.HasPrefix(value, protectedPrefix) {
		if bytes.HasPrefix(value, previousSealPrefix) {
			refuseSeal(sealPrevious)
		}
		return nil, errors.New("value is not protected for this server")
	}
	return openSealed(value[len(protectedPrefix):], protectedEntropy)
}

// A cell in the previous seal, opened for the startup migration alone.
func unprotectPrevious(value []byte) ([]byte, error) {
	if !bytes.HasPrefix(value, previousSealPrefix) {
		return nil, errors.New("value is not in the previous seal")
	}
	return openSealed(value[len(previousSealPrefix):], previousEntropy)
}

func openSealed(data, associated []byte) ([]byte, error) {
	gcm, err := protectionAEAD()
	if err != nil {
		return nil, err
	}
	if len(data) < gcm.NonceSize()+gcm.Overhead() {
		return nil, errors.New("protected value is incomplete")
	}
	plain, err := gcm.Open(nil, data[:gcm.NonceSize()], data[gcm.NonceSize():], associated)
	if err != nil {
		return nil, errors.New("protected value failed authentication")
	}
	return plain, nil
}

func protectString(value string) ([]byte, error) { return protectLocal([]byte(value)) }
func protectStrings(values ...string) ([][]byte, error) {
	result := make([][]byte, len(values))
	for index, value := range values {
		sealed, err := protectString(value)
		if err != nil {
			return nil, err
		}
		result[index] = sealed
	}
	return result, nil
}

// Same counters as the Windows file: a restore reports what share of its unsealing was of sealed "".
var unsealedCells, unsealedEmpty atomic.Int64

func unprotectString(value []byte) (string, error) {
	// A column default from a schema migration can be the empty TEXT value until the row is rewritten; it holds
	// nothing sensitive, and every non-empty value must carry the protected format.
	if len(value) == 0 {
		return "", nil
	}
	plain, err := unprotectLocal(value)
	if err != nil {
		return "", err
	}
	defer clear(plain)
	unsealedCells.Add(1)
	if len(plain) == 0 {
		unsealedEmpty.Add(1)
	}
	return string(plain), nil
}
