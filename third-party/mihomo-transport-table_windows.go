// IPv6Lens modification, 2026-10-27. Licensed under GPL-3.0 like Mihomo.
package process

import "sync"

var transportTableReadMu sync.Mutex

func serialTransportTableRead(read func() ([]byte, error)) ([]byte, error) {
	// Concurrent IP Helper enumerations can contend heavily in the Windows kernel.
	// Wait in Go instead, but always read a fresh table to avoid stale port/PID data.
	transportTableReadMu.Lock()
	defer transportTableReadMu.Unlock()
	return read()
}
