// Package tailnet is the Go half of Message Link for Android's own Tailscale node (SWITCHBOARD.md 7.4, docs/switchboard-detail.md
// stage 7), bound for Android with gomobile: the phone joins the owner's tailnet itself, without the Tailscale app or a VPN,
// and reaches the server's door (messages-tailnet, 4.7) through it. The app's connections go through a local door of this
// package's (door.go), so the server's TLS, its pinned certificate and the device token stay the app's, end to end.
//
// What the stage 7.0 test app (experiments/Android) learned is kept here: Android denies Go the netlink socket, so the app
// passes its interfaces and default route from Java; tsnet needs a log folder of the app's or it panics; and its log stays on
// the phone.
package tailnet

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	gvlog "gvisor.dev/gvisor/pkg/log"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/logtail"
	"tailscale.com/net/netmon"
	"tailscale.com/tailcfg"
	"tailscale.com/tsnet"
)

func init() {
	// tsnet uploads its diagnostic log to Tailscale by default. It names networks and peers, so it stays on the phone.
	logtail.Disable()
	// gomobile sends Go's standard log and stderr to logcat, where anything with the right to read logs could see it, and
	// some of Tailscale's packages write there directly (netmon named the default network's interface on the emulator), as
	// gVisor's own logger does. Tailscale's log carries the sign-in link, the owner's login and the tailnet's devices and
	// addresses, so none of it is kept: tsnet's lines, the standard log and gVisor's all go nowhere.
	log.SetFlags(0)
	log.SetOutput(io.Discard)
	gvlog.SetTarget(quiet{})
}

// quiet is gVisor's logger, saying nothing.
type quiet struct{}

func (quiet) Emit(int, gvlog.Level, time.Time, string, ...any) {}

// discard is tsnet's logger, saying nothing (see init).
func discard(string, ...any) {}

var (
	ifaceMu   sync.Mutex
	ifaceList string
)

// SetInterfaces takes the phone's network interfaces as the app reads them from java.net.NetworkInterface, one per
// line: name, index, MTU, flags (u up, l loopback, p point-to-point, m multicast, b broadcast) and comma-separated
// address/prefix pairs, tab-separated. The app sends a fresh list before each start and on every network change.
func SetInterfaces(list string) {
	ifaceMu.Lock()
	defer ifaceMu.Unlock()
	ifaceList = list
}

func interfaces() ([]netmon.Interface, error) {
	ifaceMu.Lock()
	list := ifaceList
	ifaceMu.Unlock()
	out := parseInterfaces(list)
	if len(out) == 0 {
		return nil, errors.New("no network interfaces from the app yet")
	}
	return out, nil
}

func parseInterfaces(list string) []netmon.Interface {
	var out []netmon.Interface
	for _, line := range strings.Split(list, "\n") {
		f := strings.Split(line, "\t")
		if len(f) < 5 {
			continue
		}
		index, _ := strconv.Atoi(f[1])
		mtu, _ := strconv.Atoi(f[2])
		var flags net.Flags
		for _, c := range f[3] {
			switch c {
			case 'u':
				flags |= net.FlagUp | net.FlagRunning
			case 'l':
				flags |= net.FlagLoopback
			case 'p':
				flags |= net.FlagPointToPoint
			case 'm':
				flags |= net.FlagMulticast
			case 'b':
				flags |= net.FlagBroadcast
			}
		}
		// Never nil: a nil AltAddrs sends Addrs back to the netlink call Android refuses.
		addrs := []net.Addr{}
		for _, a := range strings.Split(f[4], ",") {
			if p, err := netip.ParsePrefix(a); err == nil {
				addrs = append(addrs, &net.IPNet{IP: p.Addr().AsSlice(), Mask: net.CIDRMask(p.Bits(), p.Addr().BitLen())})
			}
		}
		out = append(out, netmon.Interface{
			Interface: &net.Interface{Index: index, MTU: mtu, Name: f[0], Flags: flags},
			AltAddrs:  addrs,
		})
	}
	return out
}

var (
	// life is held across a node's Start and its Close, which tsnet forbids running together; the app calls in from
	// more than one thread.
	life sync.Mutex
	mu   sync.Mutex
	// ready is the node once its Start has succeeded, and nil before and after. Only a started node is touched, since
	// a tsnet server's system is not set up until then.
	ready *tsnet.Server
)

// Start brings the node up from dir (where its identity is kept, so the sign-in happens once) under hostname, its name
// on the tailnet, and returns once it has started; Status then says whether it is on the tailnet or needs signing in.
// Starting a started node does nothing.
func Start(dir, hostname string) error {
	life.Lock()
	defer life.Unlock()
	if node() != nil {
		return nil
	}
	// Tailscale looks for a folder for its local logs (TS_LOGS_DIR, a system state folder, a cache folder, the working
	// folder, then the temporary one) and panics if none is writable, which on Android none is: "no safe place found to
	// store log state" crashed the 7.0 test app on the owner's phone. Nothing is uploaded.
	folders := map[string]string{"TS_LOGS_DIR": "logs", "TMPDIR": "tmp"}
	if runtime.GOOS == "windows" {
		// Go reads TMP and TEMP on Windows, never TMPDIR.
		folders["TMP"], folders["TEMP"] = "tmp", "tmp"
	}
	for name, sub := range folders {
		path := filepath.Join(dir, sub)
		if err := os.MkdirAll(path, 0o700); err != nil {
			return err
		}
		os.Setenv(name, path)
	}
	// On Windows the node's keys are sealed to the user (store_windows.go); on Android tsnet keeps them in its own file in
	// dir, which only the app can read.
	store, err := stateStore(dir)
	if err != nil {
		return err
	}
	// A closed tsnet server cannot start again, so each start is a new one from the same state folder.
	s := &tsnet.Server{Dir: dir, Hostname: hostname, Logf: discard, UserLogf: discard}
	if store != nil {
		s.Store = store
	}
	if err := s.Start(); err != nil {
		// tsnet's own cleanup of a failed start leaves its network stack and event bus; Close is written for this case.
		s.Close()
		return err
	}
	mu.Lock()
	ready = s
	mu.Unlock()
	return nil
}

// Stop takes the node off the tailnet. The app stops it a minute after it leaves the screen: nothing holds a tunnel in
// the background (SWITCHBOARD.md Decisions: pushes, no persistent notification).
func Stop() error {
	life.Lock()
	defer life.Unlock()
	mu.Lock()
	s := ready
	ready = nil
	mu.Unlock()
	if s == nil {
		return nil
	}
	return s.Close()
}

// NetworkChanged tells the node the phone changed networks. On Android it otherwise looks only every 10 minutes.
func NetworkChanged() {
	if s := node(); s != nil {
		if nm, ok := s.Sys().NetMon.GetOK(); ok && nm != nil {
			nm.InjectEvent()
		}
	}
}

// LinkChanged asks the node to look again after the phone's addresses or routes changed on the same network. Unlike
// NetworkChanged it reacts only to a real difference: a network that re-announces itself overnight would otherwise cost
// a radio-using check each time.
func LinkChanged() {
	if s := node(); s != nil {
		if nm, ok := s.Sys().NetMon.GetOK(); ok && nm != nil {
			nm.Poll()
		}
	}
}

// state is what Status reports, for the app's screens. Login and User are shown on the phone only, never logged: the link
// decides whose tailnet the phone joins, and the login is a name.
type state struct {
	// State is Tailscale's own word (NeedsLogin, Starting, Running, ...), or Stopped when the node is not started.
	State string `json:"state"`
	// Login is the sign-in link while the node needs one.
	Login string `json:"login,omitempty"`
	// User is the Tailscale login the node is signed in as.
	User string `json:"user,omitempty"`
	// Name is this phone's name on the tailnet.
	Name string `json:"name,omitempty"`
	// Address is this phone's tailnet IPv4 address.
	Address string `json:"address,omitempty"`
	// Lock is off, signed, unsigned (then NodeKey is the key a signing device signs) or unknown.
	Lock    string `json:"lock"`
	NodeKey string `json:"nodeKey,omitempty"`
	// KeyExpiry is when this phone's key expires (RFC 3339, UTC), off when the tailnet's owner turned expiry off for it,
	// or empty while it is not known. Settings shows it beside a link to the machine in Tailscale's admin console.
	KeyExpiry string `json:"keyExpiry,omitempty"`
}

// Status is the node's state as JSON (state above). It reads only the node's own memory.
func Status() string {
	out := state{State: "Stopped", Lock: "unknown"}
	if s := node(); s != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		out = read(ctx, s)
	}
	b, _ := json.Marshal(out)
	return string(b)
}

func read(ctx context.Context, s *tsnet.Server) state {
	out := state{State: "Starting", Lock: "unknown"}
	lc, err := s.LocalClient()
	if err != nil {
		return out
	}
	st, err := lc.StatusWithoutPeers(ctx)
	if err != nil {
		return out
	}
	out.State = st.BackendState
	if st.BackendState == "NeedsLogin" {
		out.Login = st.AuthURL
	}
	if st.Self != nil {
		out.Name, _, _ = strings.Cut(strings.TrimSuffix(st.Self.DNSName, "."), ".")
		if u, ok := st.User[st.Self.UserID]; ok {
			out.User = u.LoginName
		}
		if st.Self.KeyExpiry != nil {
			out.KeyExpiry = st.Self.KeyExpiry.UTC().Format(time.RFC3339)
		} else if st.BackendState == "Running" {
			out.KeyExpiry = "off"
		}
	}
	if ip := firstIP(st.TailscaleIPs); ip.IsValid() {
		out.Address = ip.String()
	}
	if lock, err := lc.NetworkLockStatus(ctx); err == nil && lock != nil {
		switch {
		case !lock.Enabled:
			out.Lock = "off"
		case lock.NodeKeySigned:
			out.Lock = "signed"
		default:
			out.Lock = "unsigned"
			if lock.NodeKey != nil {
				out.NodeKey = lock.NodeKey.String()
			}
		}
	}
	return out
}

// found is what Find and FindNode report about the server's device.
type found struct {
	Name    string `json:"name,omitempty"`
	Address string `json:"address,omitempty"`
	Online  bool   `json:"online"`
	// ID is the device's stable node id, which the app keeps at join and connects to from then on, so that no other device
	// answering to the server's name can take its place.
	ID string `json:"id,omitempty"`
	// Other is FindNode's answer when that device is not on the tailnet and another answers to its name. Nothing else
	// about that device is said.
	Other bool `json:"other,omitempty"`
	// Owner is the Tailscale login of the device's owner, so the app can say so when it is not the account the phone
	// signed in with. Empty for a tagged device (an auth key's), which belongs to no person. Shown, never logged.
	Owner string `json:"owner,omitempty"`
}

func describe(peer *ipnstate.PeerStatus, users map[tailcfg.UserID]tailcfg.UserProfile) string {
	short, _, _ := strings.Cut(strings.TrimSuffix(peer.DNSName, "."), ".")
	out := found{Name: short, Address: firstIP(peer.TailscaleIPs).String(), Online: peer.Online, ID: string(peer.ID)}
	if peer.Tags == nil || peer.Tags.Len() == 0 {
		if u, ok := users[peer.UserID]; ok {
			out.Owner = u.LoginName
		}
	}
	b, _ := json.Marshal(out)
	return string(b)
}

// Find waits up to timeoutMs for a device called name on the tailnet and returns it as JSON (found above). A node
// started from its saved state says Running before its list of peers has come (7.0), so the list is waited for. avoid,
// when set, is the device the app had kept, passed over while it is offline and another answers to the name: "Find the
// server again", after the server's Tailscale state was reset and its old node, gone for good, still holds the name.
func Find(name, avoid string, timeoutMs int) (string, error) {
	s := node()
	if s == nil {
		return "", errors.New("tailnet off")
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(timeoutMs)*time.Millisecond)
	defer cancel()
	peer, users, err := lookup(ctx, s, name, avoid)
	if err != nil {
		return "", err
	}
	return describe(peer, users), nil
}

// FindNode waits up to timeoutMs for the device whose stable node id is id (the server the app joined) and returns it as
// JSON. Once the list of devices has come without it and another device answers to name, it says so (other) at once:
// the app refuses that device rather than connecting.
func FindNode(id, name string, timeoutMs int) (string, error) {
	s := node()
	if s == nil {
		return "", errors.New("tailnet off")
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(timeoutMs)*time.Millisecond)
	defer cancel()
	peer, users, other, err := lookupNode(ctx, s, id, name)
	switch {
	case other:
		b, _ := json.Marshal(found{Other: true})
		return string(b), nil
	case err != nil:
		return "", err
	}
	return describe(peer, users), nil
}

// Path says how traffic to the server goes now: direct (a path of its own, across the home network or through the
// routers' NAT), relayed (through one of Tailscale's DERP servers), none yet, or unknown. The server is the device whose
// stable id is id when the app has one, and otherwise the device called name. For the app's log: at home a relayed path
// means the phone could not reach the server's address on the home network. One read of the node's memory: it never
// waits for the device.
func Path(name, id string) string {
	s := node()
	if s == nil {
		return "unknown"
	}
	lc, err := s.LocalClient()
	if err != nil {
		return "unknown"
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	st, err := lc.Status(ctx)
	if err != nil {
		return "unknown"
	}
	var self int64
	if st.Self != nil {
		self = int64(st.Self.UserID)
	}
	var peer *ipnstate.PeerStatus
	if id != "" {
		peer, _ = identify(st.Peer, id, name, self)
	} else {
		peer = choose(st.Peer, name, self)
	}
	switch {
	case peer == nil:
		return "unknown"
	case peer.CurAddr != "":
		return "direct"
	case peer.Relay != "":
		return "relayed"
	}
	return "none"
}

// peerInfo is one device in what Peers reports. Shown on screen or used to find the server, never logged.
type peerInfo struct {
	// Name is the first label of the device's MagicDNS name (its name as asked for when it has none).
	Name string `json:"name"`
	// DNS is the whole MagicDNS name, without its trailing dot.
	DNS string `json:"dns"`
	// Address is the device's tailnet IPv4 address, or its first tailnet address when it has no IPv4 one.
	Address string `json:"address"`
	// ID is the device's stable node id, which a door target names after a "/".
	ID     string `json:"id"`
	Online bool   `json:"online"`
	// Mine is a device of the user this node is signed in as.
	Mine bool `json:"mine"`
	// Tagged is a device of an auth key's, which belongs to no person.
	Tagged bool   `json:"tagged"`
	OS     string `json:"os"`
}

// Peers lists every other device this node can see on the tailnet as a JSON array (peerInfo above), so the PC can look for
// its server among them. One read of the node's memory: it never waits for the list to come.
func Peers() (string, error) {
	s := node()
	if s == nil {
		return "", errors.New("tailnet off")
	}
	lc, err := s.LocalClient()
	if err != nil {
		return "", err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	st, err := lc.Status(ctx)
	if err != nil {
		return "", err
	}
	b, err := json.Marshal(peerList(st.Self, st.Peer))
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// peerList is Peers' answer from a status: every device but self, in a fixed order (name, then id) so the same list
// always reads the same. Never nil, so the JSON is [] rather than null.
func peerList[K comparable](self *ipnstate.PeerStatus, peers map[K]*ipnstate.PeerStatus) []peerInfo {
	out := []peerInfo{}
	for _, p := range peers {
		if p == nil || self != nil && p.ID != "" && p.ID == self.ID {
			continue
		}
		dns := strings.TrimSuffix(p.DNSName, ".")
		name, _, _ := strings.Cut(dns, ".")
		if name == "" {
			name = strings.ToLower(p.HostName)
		}
		item := peerInfo{Name: name, DNS: dns, ID: string(p.ID), Online: p.Online, Tagged: p.Tags != nil && p.Tags.Len() > 0, OS: p.OS}
		if ip := firstIP(p.TailscaleIPs); ip.IsValid() {
			item.Address = ip.String()
		}
		item.Mine = self != nil && p.UserID == self.UserID
		out = append(out, item)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Name != out[j].Name {
			return out[i].Name < out[j].Name
		}
		return out[i].ID < out[j].ID
	})
	return out
}

// lookup waits until ctx ends for a device on the tailnet called target (its MagicDNS name, the first label of it, the
// name it asked for, or its tailnet address), passing over avoid as pick does. Nothing else is ever looked up: tsnet
// would dial anything else outside the tunnel.
func lookup(ctx context.Context, s *tsnet.Server, target, avoid string) (*ipnstate.PeerStatus, map[tailcfg.UserID]tailcfg.UserProfile, error) {
	lc, err := s.LocalClient()
	if err != nil {
		return nil, nil, err
	}
	for {
		st, err := lc.Status(ctx)
		if err != nil {
			return nil, nil, err
		}
		var self int64
		if st.Self != nil {
			self = int64(st.Self.UserID)
		}
		if peer := pick(st.Peer, target, self, avoid); peer != nil {
			return peer, st.User, nil
		}
		select {
		case <-ctx.Done():
			return nil, nil, fmt.Errorf("no device called %q on the tailnet (%d known)", target, len(st.Peer))
		case <-time.After(50 * time.Millisecond):
		}
	}
}

// lookupNode waits until ctx ends for the device whose stable node id is id. It gives up early, with other, once the
// list of devices has come without it and another device answers to name (identify).
func lookupNode(ctx context.Context, s *tsnet.Server, id, name string) (peer *ipnstate.PeerStatus, users map[tailcfg.UserID]tailcfg.UserProfile, other bool, err error) {
	lc, err := s.LocalClient()
	if err != nil {
		return nil, nil, false, err
	}
	for {
		st, err := lc.Status(ctx)
		if err != nil {
			return nil, nil, false, err
		}
		var self int64
		if st.Self != nil {
			self = int64(st.Self.UserID)
		}
		peer, other := identify(st.Peer, id, name, self)
		if peer != nil || other {
			return peer, st.User, other, nil
		}
		select {
		case <-ctx.Done():
			return nil, nil, false, fmt.Errorf("no device with the server's id on the tailnet (%d known)", len(st.Peer))
		case <-time.After(50 * time.Millisecond):
		}
	}
}

// identify finds the device whose stable node id is id: the server the app joined, whatever it is called now. Without
// it, other says whether another device answers to name. That is a server whose Tailscale state was reset (a new node,
// which "Find the server again" learns once its certificate matches the pin) or a device that asked for the name; either
// way the app does not connect to it by itself. A node's list of devices comes whole, so once another device is listed
// the one with the id is not about to follow.
func identify[K comparable](peers map[K]*ipnstate.PeerStatus, id, name string, self int64) (peer *ipnstate.PeerStatus, other bool) {
	if id == "" {
		return nil, false
	}
	for _, p := range peers {
		if p != nil && string(p.ID) == id && firstIP(p.TailscaleIPs).IsValid() {
			return p, false
		}
	}
	return nil, choose(peers, name, self) != nil
}

// pick is choose, passing over avoid (the device the app had kept) while that device is offline and another answers to
// target: the old node of a server whose Tailscale state was reset keeps its name, offline, and choose would always give
// it back. While the kept device is online it is chosen as before, and a device passed over to is still only connected
// to once its certificate matches the pin.
func pick[K comparable](peers map[K]*ipnstate.PeerStatus, target string, self int64, avoid string) *ipnstate.PeerStatus {
	if avoid != "" {
		rest := make(map[K]*ipnstate.PeerStatus, len(peers))
		passed := false
		for k, p := range peers {
			if p != nil && string(p.ID) == avoid && !p.Online {
				passed = true
				continue
			}
			rest[k] = p
		}
		if passed {
			if p := choose(rest, target, self); p != nil {
				return p
			}
		}
	}
	return choose(peers, target, self)
}

// choose picks the device called target from the peers. Several can answer to it: a second server is listed as
// messages-server-1, and a device shared in from someone else can ask for any name.
func choose[K comparable](peers map[K]*ipnstate.PeerStatus, target string, self int64) *ipnstate.PeerStatus {
	target = strings.TrimSuffix(strings.TrimSpace(target), ".")
	if target == "" {
		return nil
	}
	addr, err := netip.ParseAddr(target)
	isAddr := err == nil
	var best *ipnstate.PeerStatus
	bestScore := -1
	for _, p := range peers {
		if p == nil || !firstIP(p.TailscaleIPs).IsValid() {
			continue
		}
		if isAddr {
			if !peerHas(p, addr) {
				continue
			}
		} else {
			name := strings.TrimSuffix(p.DNSName, ".")
			short, _, _ := strings.Cut(name, ".")
			if !strings.EqualFold(name, target) && !strings.EqualFold(short, target) && !strings.EqualFold(p.HostName, target) {
				continue
			}
		}
		// The exact name first, then the owner's own device, then one that is online; between equals the lower address,
		// so the same list always gives the same device. Being online comes last: a server that is down for its nightly
		// update must not hand its connections to another device that only asked for its name (the pin would turn that
		// one away, and the phone would stop trying).
		score := 0
		if short, _, _ := strings.Cut(strings.TrimSuffix(p.DNSName, "."), "."); strings.EqualFold(short, target) {
			score += 4
		}
		if int64(p.UserID) == self {
			score += 2
		}
		if p.Online {
			score++
		}
		if score > bestScore || score == bestScore && firstIP(p.TailscaleIPs).Less(firstIP(best.TailscaleIPs)) {
			best, bestScore = p, score
		}
	}
	return best
}

func node() *tsnet.Server {
	mu.Lock()
	defer mu.Unlock()
	return ready
}

func peerHas(p *ipnstate.PeerStatus, ip netip.Addr) bool {
	for _, a := range p.TailscaleIPs {
		if a == ip {
			return true
		}
	}
	return false
}

func firstIP(ips []netip.Addr) netip.Addr {
	for _, ip := range ips {
		if ip.Is4() {
			return ip
		}
	}
	if len(ips) > 0 {
		return ips[0]
	}
	return netip.Addr{}
}

// Login asks Tailscale for a new sign-in link, for a node whose key expired while it ran: tsnet asks only when it starts,
// so the node would otherwise wait with no link to offer.
func Login() error {
	s := node()
	if s == nil {
		return errors.New("tailnet off")
	}
	lc, err := s.LocalClient()
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	return lc.StartLoginInteractive(ctx)
}

// Logout signs the node out of Tailscale and takes it off the tailnet, so the phone can sign in with another account.
// The app then removes the node's folder.
func Logout() error {
	life.Lock()
	defer life.Unlock()
	mu.Lock()
	s := ready
	ready = nil
	mu.Unlock()
	if s == nil {
		return nil
	}
	defer s.Close()
	lc, err := s.LocalClient()
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	return lc.Logout(ctx)
}
