// SPDX-License-Identifier: GPL-3.0-only

package outbound

import (
	"net/netip"
	"reflect"
	"testing"

	C "github.com/metacubex/mihomo/constant"
)

func TestIPv6LensPreserveDirectDestination(t *testing.T) {
	for _, test := range []struct {
		name, ip, host, sniff string
		mode                  C.DNSMode
		preserve              bool
	}{
		{"mapped_ipv4", "192.0.2.10", "cdn.example", "", C.DNSMapping, true},
		{"mapped_ipv6", "2001:db8::10", "cdn.example", "", C.DNSMapping, true},
		{"sniffer_resets_dns_mode", "192.0.2.10", "cdn.example", "cdn.example", C.DNSNormal, true},
		{"hosts", "192.0.2.10", "cdn.example", "", C.DNSHosts, true},
		{"domain_with_resolved_rule", "192.0.2.10", "cdn.example", "", C.DNSNormal, false},
		{"unresolved_domain", "", "cdn.example", "", C.DNSNormal, false},
		{"fake_ip", "198.18.0.10", "cdn.example", "cdn.example", C.DNSFakeIP, false},
		{"literal", "192.0.2.10", "", "", C.DNSNormal, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			metadata := &C.Metadata{Host: test.host, SniffHost: test.sniff, DNSMode: test.mode, DstPort: 443}
			if test.ip != "" {
				metadata.DstIP = netip.MustParseAddr(test.ip)
			}
			original := *metadata
			got := preserveDirectDestination(metadata)
			if !reflect.DeepEqual(*metadata, original) {
				t.Fatal("classification metadata was mutated")
			}
			if test.preserve {
				if got == metadata || got.Host != "" || got.DstIP != original.DstIP ||
					got.DstPort != original.DstPort || got.SniffHost != original.SniffHost {
					t.Fatalf("captured peer was not preserved: %#v", got)
				}
				if got.RemoteAddress() != netip.AddrPortFrom(original.DstIP, original.DstPort).String() {
					t.Fatal("dial address no longer matches the original peer")
				}
			} else if got != metadata {
				t.Fatal("ordinary domain, literal or fake-ip behavior changed")
			}
		})
	}
}
