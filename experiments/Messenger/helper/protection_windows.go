package main

import (
	"bytes"
	"errors"
	"sync/atomic"
	"unsafe"

	"golang.org/x/sys/windows"
)

var protectedPrefix = []byte{'N', 'B', 'P', 1}
var protectedEntropy = []byte("NativeBeeper Messenger retained history v1")

func dataBlob(value []byte) windows.DataBlob {
	if len(value) == 0 {
		return windows.DataBlob{}
	}
	return windows.DataBlob{Size: uint32(len(value)), Data: &value[0]}
}

// DPAPI binds each retained value to the current Windows user. A fixed prefix distinguishes protected values from
// pre-migration plaintext without ever treating a failed DPAPI operation as readable content.
func protectLocal(value []byte) ([]byte, error) {
	// CryptProtectData rejects a zero-length input on supported Windows builds. A private format marker makes every
	// payload non-empty and also lets unprotect reject data written by an incompatible future format.
	payload := make([]byte, len(value)+1)
	payload[0] = 1
	copy(payload[1:], value)
	in, entropy := dataBlob(payload), dataBlob(protectedEntropy)
	var out windows.DataBlob
	if err := windows.CryptProtectData(&in, nil, &entropy, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	result := make([]byte, len(protectedPrefix)+int(out.Size))
	copy(result, protectedPrefix)
	copy(result[len(protectedPrefix):], unsafe.Slice(out.Data, int(out.Size)))
	return result, nil
}

func unprotectLocal(value []byte) ([]byte, error) {
	if !bytes.HasPrefix(value, protectedPrefix) {
		return nil, errors.New("value is not protected")
	}
	in, entropy := dataBlob(value[len(protectedPrefix):]), dataBlob(protectedEntropy)
	var out windows.DataBlob
	if err := windows.CryptUnprotectData(&in, nil, &entropy, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	result := make([]byte, int(out.Size))
	copy(result, unsafe.Slice(out.Data, int(out.Size)))
	if len(result) == 0 || result[0] != 1 {
		return nil, errors.New("protected value has an unknown format")
	}
	return result[1:], nil
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
// Every cell unprotectString has opened with DPAPI, and how many of those held a sealed "". Counted so a restore can
// say what share of its time went on unsealing nothing, before anyone decides to stop sealing empty fields.
var unsealedCells, unsealedEmpty atomic.Int64

func unprotectString(value []byte) (string, error) {
	// SQLite column defaults introduced by a schema migration can be the empty TEXT value until that row is rewritten.
	// It contains no sensitive data; every non-empty value must carry the protected format.
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
