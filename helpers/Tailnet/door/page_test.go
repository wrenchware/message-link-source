package main

import (
	"encoding/json"
	"net/netip"
	"strings"
	"testing"

	"tailscale.com/tailcfg"
)

func TestReach(t *testing.T) {
	door := netip.MustParseAddr("100.64.0.10")
	phone := netip.MustParseAddr("100.64.0.20")
	pc := netip.MustParseAddr("100.64.0.21")
	stranger := "100.64.0.99"
	mine := []netip.Addr{door, netip.MustParseAddr("fd7a:115c:a1e0::a")}
	owned := map[netip.Addr]bool{door: true, phone: true, pc: true}
	to := func(ip string, first, last uint16) []tailcfg.NetPortRange {
		return []tailcfg.NetPortRange{{IP: ip, Ports: tailcfg.PortRange{First: first, Last: last}}}
	}
	cases := []struct {
		name     string
		rules    []tailcfg.FilterRule
		want     string
		others   int
		everyone bool
	}{
		// Tailscale's default policy: everything reaches everything.
		{"allow all", tailcfg.FilterAllowAll, "open", 0, true},
		// The README's grant, as the control server writes it for this node: the owner's devices, by address, to the one port.
		{"the owner's grant", []tailcfg.FilterRule{{SrcIPs: []string{"100.64.0.20", "100.64.0.21/32"}, DstPorts: to("100.64.0.10", 8787, 8787)}}, "yours", 0, false},
		{"a stranger too", []tailcfg.FilterRule{{SrcIPs: []string{"100.64.0.20", stranger}, DstPorts: to("100.64.0.10", 8787, 8787)}}, "open", 1, false},
		{"a range of sources", []tailcfg.FilterRule{{SrcIPs: []string{"100.64.0.0/10"}, DstPorts: to("*", 0, 65535)}}, "open", 0, true},
		// Rules about other machines or other ports do not reach the door's port.
		{"another machine", []tailcfg.FilterRule{{SrcIPs: []string{"*"}, DstPorts: to("100.64.0.50", 0, 65535)}}, "none", 0, false},
		{"another port", []tailcfg.FilterRule{{SrcIPs: []string{"*"}, DstPorts: to("100.64.0.10", 22, 22)}}, "none", 0, false},
		{"a destination range", []tailcfg.FilterRule{{SrcIPs: []string{"100.64.0.20"}, DstPorts: to("100.64.0.1-100.64.0.40", 8000, 9000)}}, "yours", 0, false},
		{"the door's IPv6", []tailcfg.FilterRule{{SrcIPs: []string{stranger}, DstPorts: to("fd7a:115c:a1e0::a", 8787, 8787)}}, "open", 1, false},
		{"nothing", nil, "none", 0, false},
	}
	for _, c := range cases {
		if got, others, everyone := reach(c.rules, mine, 8787, owned); got != c.want || others != c.others || everyone != c.everyone {
			t.Errorf("%s: reach = %q %d %v, want %q %d %v", c.name, got, others, everyone, c.want, c.others, c.everyone)
		}
	}
}

// The page's line is one JSON object after the fixed words, and says nothing it was not given.
func TestPageLine(t *testing.T) {
	line := pageView{State: "NeedsLogin", Login: "https://login.tailscale.com/a/abc123", Lock: "unknown", Rules: "unknown"}.line()
	if !strings.HasPrefix(line, "tailnet page {") || strings.Contains(line, "\n") {
		t.Fatalf("line = %q", line)
	}
	var back map[string]any
	if err := json.Unmarshal([]byte(strings.TrimPrefix(line, "tailnet page ")), &back); err != nil {
		t.Fatal(err)
	}
	if back["state"] != "NeedsLogin" || back["login"] != "https://login.tailscale.com/a/abc123" || back["owner"] != nil || back["address"] != nil {
		t.Errorf("unexpected members: %v", back)
	}
}
