package main

import (
	"net"
	"net/netip"
	"sync"
	"time"
)

// The networks of this computer's own interfaces, which the anonymous media fetcher refuses as it refuses private
// ranges (mediafetch.go): a global IPv6 address on the user's own /64 is a router, a NAS or a printer as much as
// 192.168.1.1 is. A copy of the Messenger helper's link-preview check (its linkpreview_fetch.go); each helper is its
// own module, so the two are kept alike by hand.

func onHostNetwork(ip netip.Addr, networks []netip.Prefix) bool {
	ip = ip.Unmap()
	for _, network := range networks {
		if network.Contains(ip) {
			return true
		}
	}
	return false
}

// The networks of this computer's interface addresses, and for a global IPv6 address also the /64 it sits in: an
// address given as /128 (DHCPv6, a temporary address) still has its neighbours in that /64.
func hostNetworkPrefixes(addresses []net.Addr) []netip.Prefix {
	prefixes := []netip.Prefix{}
	for _, address := range addresses {
		network, ok := address.(*net.IPNet)
		if !ok {
			continue
		}
		ip, ok := netip.AddrFromSlice(network.IP)
		if !ok {
			continue
		}
		ones, bits := network.Mask.Size()
		if ip.Is4In6() {
			ip = ip.Unmap()
			if bits == 128 {
				ones -= 96
			}
		}
		// A length of zero (or a mask that is not one) says nothing about which addresses are neighbours, and taken as a
		// network it would refuse every address there is.
		if bits == 0 || ones <= 0 {
			continue
		}
		prefix, err := ip.Prefix(ones)
		if err != nil {
			continue
		}
		prefixes = append(prefixes, prefix)
		if ip.Is6() && ip.IsGlobalUnicast() && ones > 64 {
			if wide, err := ip.Prefix(64); err == nil {
				prefixes = append(prefixes, wide)
			}
		}
	}
	return prefixes
}

// What the interfaces say now. If they cannot be read, the other checks still stand.
func readHostNetworks() []netip.Prefix {
	addresses, err := net.InterfaceAddrs()
	if err != nil {
		return nil
	}
	return hostNetworkPrefixes(addresses)
}

type mediaLocalNetworks func() []netip.Prefix

// This computer's networks, read at most once a minute.
func hostNetworks(read func() []netip.Prefix, now func() time.Time) mediaLocalNetworks {
	var mu sync.Mutex
	var held []netip.Prefix
	var readAt time.Time
	return func() []netip.Prefix {
		mu.Lock()
		defer mu.Unlock()
		if at := now(); readAt.IsZero() || at.Sub(readAt) >= time.Minute || at.Before(readAt) {
			held, readAt = read(), at
		}
		return held
	}
}
