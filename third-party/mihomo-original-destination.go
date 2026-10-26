// SPDX-License-Identifier: GPL-3.0-only

package outbound

import C "github.com/metacubex/mihomo/constant"

// Mapped and sniffed names classify a captured flow; they must not select a
// different DIRECT peer. Clone so rule matching and reporting keep the names.
func preserveDirectDestination(metadata *C.Metadata) *C.Metadata {
	if metadata.DstIP.IsValid() && metadata.DNSMode != C.DNSFakeIP &&
		(metadata.DNSMode == C.DNSMapping || metadata.DNSMode == C.DNSHosts || metadata.SniffHost != "") {
		copy := metadata.Clone()
		copy.Host = ""
		return copy
	}
	return metadata
}
