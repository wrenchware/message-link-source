//go:build android

package tailnet

import "tailscale.com/net/netmon"

func init() {
	// Android refuses apps the netlink socket, so Go's net.Interfaces fails ("netlinkrib: permission denied", 7.0) and
	// tsnet cannot start. Tailscale's own fallback is left out of cgo builds such as this one, which are expected to supply
	// the list from Java, as Tailscale's app does: SetInterfaces. Windows (the PC's tailnet-helper) reads its own list.
	netmon.RegisterInterfaceGetter(interfaces)
}

// SetDefaultRoute names the interface of the phone's default network (LinkProperties.getInterfaceName). Android does
// not let Go read the routing table, so the node learns it from the app, as Tailscale's own app tells it.
func SetDefaultRoute(ifName string) {
	netmon.UpdateLastKnownDefaultRouteInterface(ifName)
}
