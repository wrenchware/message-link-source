package main

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"testing"
	"time"
)

func noHostNetworks() []netip.Prefix { return nil }

// A global IPv6 address passes every range check and can still be a neighbour on this computer's own network. A name
// that resolves into one of this computer's networks, or such an address written in the link, is never dialled.
func TestLinkPreviewRefusesThisComputersNetworks(t *testing.T) {
	local := func() []netip.Prefix { return []netip.Prefix{netip.MustParsePrefix("2600:1f18:1:2::/64")} }
	lookup := func(context.Context, string, string) ([]netip.Addr, error) {
		return []netip.Addr{netip.MustParseAddr("2600:1f18:1:2::1")}, nil
	}
	dialled := 0
	dial := func(context.Context, string, string) (net.Conn, error) {
		dialled++
		return nil, errors.New("offline synthetic connection failure")
	}
	for _, address := range []string{"neighbour.example:443", "[2600:1f18:1:2::1]:443"} {
		if _, err := previewPublicDial(lookup, local, dial)(context.Background(), "tcp", address); !errors.Is(err, errPreviewDestination) {
			t.Fatalf("%s on this computer's network: %v", address, err)
		}
	}
	if dialled != 0 {
		t.Fatal("an address on this computer's network was dialled")
	}
	// The same address elsewhere is dialled as before.
	elsewhere := func(context.Context, string, string) ([]netip.Addr, error) {
		return []netip.Addr{netip.MustParseAddr("2606:4700:4700::1111")}, nil
	}
	if _, err := previewPublicDial(elsewhere, local, dial)(context.Background(), "tcp", "public.example:443"); !errors.Is(err, errPreviewRequest) || dialled != 1 {
		t.Fatalf("a public address off this computer's networks: %v, dialled %d", err, dialled)
	}
}

// An interface address given as /128 still has its neighbours in its /64; IPv4 addresses in IPv6 form keep their own
// network length; a length of zero is not taken as a network.
func TestHostNetworkPrefixesFromInterfaceAddresses(t *testing.T) {
	prefixes := hostNetworkPrefixes([]net.Addr{
		&net.IPNet{IP: net.ParseIP("2600:1f18:1:2::5"), Mask: net.CIDRMask(128, 128)},
		&net.IPNet{IP: net.ParseIP("203.0.113.7"), Mask: net.CIDRMask(24, 32)},
		&net.IPNet{IP: net.ParseIP("198.51.100.9"), Mask: net.CIDRMask(120, 128)},
		&net.IPNet{IP: net.ParseIP("2001:db8::1"), Mask: net.CIDRMask(0, 128)},
		&net.IPAddr{IP: net.ParseIP("192.0.2.1")},
	})
	want := []string{"2600:1f18:1:2::5/128", "2600:1f18:1:2::/64", "203.0.113.0/24", "198.51.100.0/24"}
	if len(prefixes) != len(want) {
		t.Fatalf("prefixes %v, want %v", prefixes, want)
	}
	for i, prefix := range prefixes {
		if prefix.String() != want[i] {
			t.Fatalf("prefixes %v, want %v", prefixes, want)
		}
	}
	if !onHostNetwork(netip.MustParseAddr("2600:1f18:1:2::1"), prefixes) || !onHostNetwork(netip.MustParseAddr("::ffff:203.0.113.200"), prefixes) ||
		onHostNetwork(netip.MustParseAddr("2600:1f18:1:3::1"), prefixes) {
		t.Fatal("membership of this computer's networks")
	}
}

func TestHostNetworksAreReadAtMostOnceAMinute(t *testing.T) {
	now := time.Now()
	reads := 0
	networks := hostNetworks(func() []netip.Prefix {
		reads++
		return []netip.Prefix{netip.MustParsePrefix("2600:1f18:1:2::/64")}
	}, func() time.Time { return now })
	networks()
	now = now.Add(59 * time.Second)
	if len(networks()) != 1 || reads != 1 {
		t.Fatalf("read %d times within a minute", reads)
	}
	now = now.Add(time.Second)
	if networks(); reads != 2 {
		t.Fatalf("read %d times after a minute", reads)
	}
}
