package main

import (
	"fmt"
	"net/netip"
)

// identity is who a tailnet node belongs to: a Tailscale user, or tags (a node the tailnet's admins own as a service).
type identity struct {
	user   int64
	tagged bool
}

// admit says whether a device may reach the server through this door. Only the owner's own devices: those signed in to
// Tailscale as the same user this door's node was signed in as (the owner, who opened its sign-in link). A tagged
// device is a service, never a person's phone or PC, and is refused. When the door's own node is tagged its owner is
// not known here, so untagged devices are let through and the tailnet's access rules decide alone (the log says
// owner_check=off). Either way the server still asks every connection for its device token or a pairing.
func admit(self, peer identity) (bool, string) {
	if peer.tagged {
		return false, "tagged"
	}
	if self.tagged {
		return true, ""
	}
	if self.user == 0 || peer.user != self.user {
		return false, "other_user"
	}
	return true, ""
}

// proxyLine is the PROXY protocol (version 1, haproxy.org's proxy-protocol.txt) line the server's relay listener reads
// before the connection's own bytes: the device's tailnet address and port, then this node's. Both are of one family.
func proxyLine(peer, local netip.AddrPort) string {
	from, to := peer.Addr().Unmap(), local.Addr().Unmap()
	family := "TCP4"
	if from.Is6() {
		family = "TCP6"
	}
	return fmt.Sprintf("PROXY %s %s %s %d %d\r\n", family, from, to, peer.Port(), local.Port())
}
