package main

import (
	"context"
	"encoding/json"
	"net/netip"
	"sort"
	"strings"
	"time"

	"tailscale.com/ipn/ipnstate"
	"tailscale.com/tailcfg"
)

// pageView is what the server's own page shows of the door (its "Your tailnet" card, ServerPage.cs): enough to finish setting the
// door up there instead of in the container's log. It goes to the server as one "tailnet page {json}" line whenever it changes,
// which the server holds in memory for the page alone and never writes to a log: it carries the sign-in link (whoever opens it
// picks the tailnet the server joins) and the owner's login.
type pageView struct {
	// Tailscale's backend state (NeedsLogin, Starting, Running, ...).
	State string `json:"state"`
	// The sign-in link while the node needs one.
	Login string `json:"login,omitempty"`
	// The node's name on the tailnet, its full MagicDNS name and its first IPv4 address.
	Name    string `json:"name,omitempty"`
	DNSName string `json:"dnsName,omitempty"`
	Address string `json:"address,omitempty"`
	// The login of the user the node belongs to (whom the door lets in), and whether it is tagged (then it can tell nobody apart).
	Owner  string `json:"owner,omitempty"`
	Tagged bool   `json:"tagged,omitempty"`
	// "off", or when the node's key expires (RFC 3339).
	Expiry string `json:"expiry,omitempty"`
	// Tailnet Lock: "off", "signed", "unsigned" (with the key to sign), or "unknown".
	Lock    string `json:"lock,omitempty"`
	NodeKey string `json:"nodeKey,omitempty"`
	// What the tailnet's access rules let reach the door's port: "yours" (only the owner's own devices), "open" (others too: Everyone
	// when a rule lets every device in (a wildcard or a range, Tailscale's default policy), otherwise Others devices that are not the owner's),
	// "none" (no device at all), or "unknown".
	Rules    string `json:"rules,omitempty"`
	Others   int    `json:"others,omitempty"`
	Everyone bool   `json:"everyone,omitempty"`
	// Whether Tailscale signed this node out after it had been signed in (its key expired, or it was logged out), so the page says "Sign in again".
	SignedOut bool `json:"signedOut,omitempty"`
	// The owner's Messages phones on the tailnet (nodes named messages-…, other than a server), each with its key expiry, for the page's checklist.
	Phones []phoneView `json:"phones,omitempty"`
	// How many people's devices the tailnet holds (tagged devices belong to nobody): 1 when only the owner's, where Tailscale's default rule reaches only theirs.
	Users int `json:"users,omitempty"`
}

// phoneView is one Messages phone as the page lists it.
type phoneView struct {
	Name    string `json:"name"`
	Address string `json:"address,omitempty"`
	Expiry  string `json:"expiry,omitempty"`
}

// view reads what the page shows. A part that cannot be read now is left as it is unknown, never guessed. signedIn says the node was signed in earlier
// in this run, so a sign-in it needs now is a sign-in again.
func (d *door) view(ctx context.Context, status *ipnstate.Status, port int, signedIn bool) pageView {
	v := pageView{State: word(status.BackendState), Login: status.AuthURL, Lock: "unknown", Rules: "unknown"}
	self := status.Self
	if status.BackendState == "NeedsLogin" {
		v.SignedOut = signedIn || self != nil && self.KeyExpiry != nil && self.KeyExpiry.Before(time.Now())
	}
	if self == nil || status.BackendState != "Running" && status.BackendState != "NeedsMachineAuth" {
		return v
	}
	describe(&v, self)
	if status.BackendState != "Running" {
		// Waiting for the tailnet's admin to approve it: its name and address are enough to find it in the console.
		return v
	}
	if profile, ok := status.User[self.UserID]; ok {
		v.Owner = profile.LoginName
	}
	v.Phones, v.Users = phones(status)
	if lock, err := d.client.NetworkLockStatus(ctx); err == nil {
		switch {
		case !lock.Enabled:
			v.Lock = "off"
		case lock.NodeKeySigned:
			v.Lock = "signed"
		default:
			v.Lock = "unsigned"
			if lock.NodeKey != nil {
				v.NodeKey = lock.NodeKey.String()
			}
		}
	}
	// The owner's own devices: this node and every peer of the same user (a tagged device belongs to no user).
	owned := map[netip.Addr]bool{}
	for _, ip := range self.TailscaleIPs {
		owned[ip] = true
	}
	for _, peer := range status.Peer {
		if peer != nil && peer.UserID == self.UserID && (peer.Tags == nil || peer.Tags.Len() == 0) {
			for _, ip := range peer.TailscaleIPs {
				owned[ip] = true
			}
		}
	}
	if rules, err := d.client.DebugPacketFilterRules(ctx); err == nil {
		mine := self.TailscaleIPs
		v.Rules, v.Others, v.Everyone = reach(rules, mine, port, owned)
	}
	return v
}

// describe fills in what is the node's own: its names, its first IPv4 address, whether it is tagged and its key expiry.
func describe(v *pageView, self *ipnstate.PeerStatus) {
	v.Name = strings.SplitN(strings.TrimSuffix(self.DNSName, "."), ".", 2)[0]
	v.DNSName = strings.TrimSuffix(self.DNSName, ".")
	v.Address = firstV4(self.TailscaleIPs)
	v.Tagged = self.Tags != nil && self.Tags.Len() > 0
	if self.KeyExpiry == nil {
		v.Expiry = "off"
	} else {
		v.Expiry = self.KeyExpiry.UTC().Format(time.RFC3339)
	}
}

func firstV4(ips []netip.Addr) string {
	for _, ip := range ips {
		if ip.Is4() {
			return ip.String()
		}
	}
	return ""
}

// phonePrefix is how a Messages phone names its node on the tailnet (the app's Tailscale node, 7.4); a server is messages-server, or another name the
// owner gave it, and is never listed as a phone.
const phonePrefix = "messagelink-"

// phones lists the owner's Messages phones among the peers, by name, and counts the people whose devices the tailnet holds.
func phones(status *ipnstate.Status) ([]phoneView, int) {
	self := status.Self
	users := map[tailcfg.UserID]bool{}
	if self.Tags == nil || self.Tags.Len() == 0 {
		users[self.UserID] = true
	}
	var found []phoneView
	for _, peer := range status.Peer {
		if peer == nil {
			continue
		}
		tagged := peer.Tags != nil && peer.Tags.Len() > 0
		if !tagged {
			users[peer.UserID] = true
		}
		name := strings.SplitN(strings.TrimSuffix(peer.DNSName, "."), ".", 2)[0]
		if name == "" {
			name = strings.ToLower(peer.HostName)
		}
		if tagged || peer.UserID != self.UserID || !(strings.HasPrefix(name, phonePrefix) || strings.HasPrefix(name, "messages-")) || strings.HasPrefix(name, "messages-server") || strings.HasPrefix(name, "messagelink-server") {
			continue
		}
		phone := phoneView{Name: name, Address: firstV4(peer.TailscaleIPs), Expiry: "off"}
		if peer.KeyExpiry != nil {
			phone.Expiry = peer.KeyExpiry.UTC().Format(time.RFC3339)
		}
		found = append(found, phone)
	}
	sort.Slice(found, func(i, j int) bool { return found[i].Name < found[j].Name })
	return found, len(users)
}

func (v pageView) line() string {
	data, err := json.Marshal(v)
	if err != nil {
		return ""
	}
	return "tailnet page " + string(data)
}

// reach says what this node's packet filter (the tailnet's access rules as they apply to it) lets reach port on any of its addresses:
// "yours" when every source is one of the owner's devices, "open" when any is not (a wildcard, a range, another person's or a tagged
// device), with how many single devices are not the owner's and whether any rule lets every device in (a wildcard or a range), and "none"
// when no rule lets anything reach the port.
func reach(rules []tailcfg.FilterRule, mine []netip.Addr, port int, owned map[netip.Addr]bool) (string, int, bool) {
	reaching, others, everyone := false, 0, false
	for _, rule := range rules {
		if !covers(rule.DstPorts, mine, port) {
			continue
		}
		for _, source := range rule.SrcIPs {
			reaching = true
			// One device is the owner's or someone else's; a wildcard or a range lets every device in.
			if addr, ok := single(source); !ok {
				everyone = true
			} else if !owned[addr] {
				others++
			}
		}
	}
	switch {
	case !reaching:
		return "none", 0, false
	case others > 0 || everyone:
		return "open", others, everyone
	default:
		return "yours", 0, false
	}
}

// covers says whether any of the destinations includes one of mine on port.
func covers(destinations []tailcfg.NetPortRange, mine []netip.Addr, port int) bool {
	for _, d := range destinations {
		if port < int(d.Ports.First) || port > int(d.Ports.Last) {
			continue
		}
		for _, addr := range mine {
			if matches(d.IP, addr) {
				return true
			}
		}
	}
	return false
}

// matches reads a destination as the filter writes one: "*", an address, a prefix, or a range "a-b".
func matches(spec string, addr netip.Addr) bool {
	switch {
	case spec == "*":
		return true
	case strings.Contains(spec, "-"):
		parts := strings.SplitN(spec, "-", 2)
		low, err1 := netip.ParseAddr(parts[0])
		high, err2 := netip.ParseAddr(parts[1])
		return err1 == nil && err2 == nil && low.Compare(addr) <= 0 && addr.Compare(high) <= 0
	case strings.Contains(spec, "/"):
		prefix, err := netip.ParsePrefix(spec)
		return err == nil && prefix.Contains(addr)
	default:
		parsed, err := netip.ParseAddr(spec)
		return err == nil && parsed.Unmap() == addr.Unmap()
	}
}

// single reads a source as one device's address (an address, or a prefix of one address), or says it is wider than that.
func single(source string) (netip.Addr, bool) {
	if addr, err := netip.ParseAddr(source); err == nil {
		return addr.Unmap(), true
	}
	if prefix, err := netip.ParsePrefix(source); err == nil && prefix.IsSingleIP() {
		return prefix.Addr().Unmap(), true
	}
	return netip.Addr{}, false
}
