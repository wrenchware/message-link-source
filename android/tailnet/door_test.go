package tailnet

import (
	"bytes"
	"context"
	"io"
	"net"
	"net/netip"
	"strconv"
	"testing"
	"time"

	"tailscale.com/ipn/ipnstate"
	"tailscale.com/tailcfg"
	"tailscale.com/types/key"
)

var testSecret = bytes.Repeat([]byte{7}, secretLength)

// serverID is the test server's stable node id, in Tailscale's shape.
const serverID = "nS3rver1CNTRL"

// echo stands in for the server: it sends back what it reads, then closes its side once the other side has.
func echo(t *testing.T) (addr string) {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { l.Close() })
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			go func() {
				io.Copy(c, c)
				c.Close()
			}()
		}
	}()
	return l.Addr().String()
}

// testDoor opens a door whose node is a plain dialer: "messages-server" (stable id serverID) is the echo, anything else is
// not on the tailnet.
func testDoor(t *testing.T, server string) (*door, *[]string) {
	t.Helper()
	var asked []string
	d, err := newDoor(testSecret, func(ctx context.Context, name, id string, port uint16) (net.Conn, byte) {
		target := name
		if id != "" {
			target += "/" + id
		}
		asked = append(asked, net.JoinHostPort(target, itoa(port)))
		switch {
		case name == "messages-server" && (id == "" || id == serverID):
			c, err := (&net.Dialer{}).DialContext(ctx, "tcp", server)
			if err != nil {
				return nil, answerNoConnection
			}
			return c, answerOpen
		case name == "messages-server":
			return nil, answerOtherDevice
		case name == "signed-out":
			return nil, answerSignedOut
		}
		return nil, answerNoDevice
	})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { d.close() })
	return d, &asked
}

func itoa(p uint16) string { return strconv.Itoa(int(p)) }

func dialDoor(t *testing.T, d *door, preamble []byte) net.Conn {
	t.Helper()
	c, err := net.Dial("tcp", net.JoinHostPort("127.0.0.1", itoa(uint16(d.port()))))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	c.SetDeadline(time.Now().Add(10 * time.Second))
	if _, err := c.Write(preamble); err != nil {
		t.Fatal(err)
	}
	return c
}

func preambleFor(secret []byte, target string) []byte {
	return append(append(append([]byte(nil), secret...), byte(len(target))), target...)
}

func TestDoorPassesTheConnectionOn(t *testing.T) {
	d, asked := testDoor(t, echo(t))
	c := dialDoor(t, d, preambleFor(testSecret, "messages-server:8787"))
	answer := make([]byte, 1)
	if _, err := io.ReadFull(c, answer); err != nil || answer[0] != answerOpen {
		t.Fatalf("answer = %v, %v", answer, err)
	}
	if _, err := c.Write([]byte("hello through the tunnel")); err != nil {
		t.Fatal(err)
	}
	// A half-close passes on: the server sees the end, finishes, and the rest comes back.
	c.(*net.TCPConn).CloseWrite()
	back, err := io.ReadAll(c)
	if err != nil || string(back) != "hello through the tunnel" {
		t.Fatalf("back = %q, %v", back, err)
	}
	if len(*asked) != 1 || (*asked)[0] != "messages-server:8787" {
		t.Errorf("asked = %v", *asked)
	}
}

// Once the app knows the server's stable node id it names it after the name, and the door asks the node for that device.
func TestDoorPassesTheServersIdOn(t *testing.T) {
	d, asked := testDoor(t, echo(t))
	c := dialDoor(t, d, preambleFor(testSecret, "messages-server/"+serverID+":8787"))
	answer := make([]byte, 1)
	if _, err := io.ReadFull(c, answer); err != nil || answer[0] != answerOpen {
		t.Fatalf("answer = %v, %v", answer, err)
	}
	if len(*asked) != 1 || (*asked)[0] != "messages-server/"+serverID+":8787" {
		t.Errorf("asked = %v", *asked)
	}
	// An address with the id, as the app writes an IPv6 one.
	c = dialDoor(t, d, preambleFor(testSecret, "[fd7a:115c:a1e0::1/"+serverID+"]:8787"))
	if back, _ := io.ReadAll(c); !bytes.Equal(back, []byte{answerNoDevice}) {
		t.Errorf("answer = %v", back)
	}
	if len(*asked) != 2 || (*asked)[1] != "[fd7a:115c:a1e0::1/"+serverID+"]:8787" {
		t.Errorf("asked = %v", *asked)
	}
}

// Without the secret the door says nothing at all and never asks the node for anything.
func TestDoorRefusesAWrongSecret(t *testing.T) {
	d, asked := testDoor(t, echo(t))
	wrong := bytes.Repeat([]byte{8}, secretLength)
	c := dialDoor(t, d, preambleFor(wrong, "messages-server:8787"))
	if n, err := c.Read(make([]byte, 1)); n != 0 || err == nil {
		t.Fatalf("read %d bytes, %v", n, err)
	}
	if len(*asked) != 0 {
		t.Errorf("asked = %v", *asked)
	}
}

func TestDoorAnswersWhyItCouldNotConnect(t *testing.T) {
	d, _ := testDoor(t, echo(t))
	for target, want := range map[string]byte{"elsewhere:8787": answerNoDevice, "signed-out:8787": answerSignedOut,
		"messages-server/nAn0therCNTRL:8787": answerOtherDevice} {
		c := dialDoor(t, d, preambleFor(testSecret, target))
		back, _ := io.ReadAll(c)
		if !bytes.Equal(back, []byte{want}) {
			t.Errorf("%s: answer = %v, want %d", target, back, want)
		}
	}
}

func TestDoorRefusesABadTarget(t *testing.T) {
	d, asked := testDoor(t, echo(t))
	for _, target := range []string{"messages-server", "messages-server:0", "messages-server:70000", ":8787", "/" + serverID + ":8787",
		"messages-server/:8787", "messages-server/n:1:8787", "messages-server/n 1:8787", "messages-server/n/1:8787", "messages-server/n@1:8787"} {
		c := dialDoor(t, d, preambleFor(testSecret, target))
		if back, _ := io.ReadAll(c); len(back) != 0 {
			t.Errorf("%q: answer = %v", target, back)
		}
	}
	if len(*asked) != 0 {
		t.Errorf("asked = %v", *asked)
	}
}

// A connection that never says who it is is closed after preambleWait, and does not hold a place.
func TestDoorClosesASilentConnection(t *testing.T) {
	saved := preambleWait
	preambleWait = 200 * time.Millisecond
	t.Cleanup(func() { preambleWait = saved })
	d, _ := testDoor(t, echo(t))
	c := dialDoor(t, d, testSecret[:10])
	start := time.Now()
	if n, err := c.Read(make([]byte, 1)); n != 0 || err == nil {
		t.Fatalf("read %d bytes, %v", n, err)
	}
	if waited := time.Since(start); waited > 3*time.Second {
		t.Errorf("closed after %v", waited)
	}
}

func TestDoorBoundsItsConnections(t *testing.T) {
	saved := preambleWait
	preambleWait = 2 * time.Second
	t.Cleanup(func() { preambleWait = saved })
	d, _ := testDoor(t, echo(t))
	// Silent connections, as any app on the phone could open, fill only the places for those still to say who they are:
	// the app's own still gets through, and the next silent one is closed at once.
	for i := 0; i < maxWaiting; i++ {
		dialDoor(t, d, nil)
	}
	time.Sleep(100 * time.Millisecond)
	c := dialDoor(t, d, nil)
	start := time.Now()
	if n, err := c.Read(make([]byte, 1)); n != 0 || err == nil {
		t.Fatalf("read %d bytes, %v", n, err)
	}
	if waited := time.Since(start); waited > time.Second {
		t.Errorf("closed after %v", waited)
	}
	// Once they are gone, the app's connections fill the places passed on, and one more is told the door is busy.
	time.Sleep(preambleWait + 500*time.Millisecond)
	for i := 0; i < maxDoorConnections; i++ {
		c := dialDoor(t, d, preambleFor(testSecret, "messages-server:8787"))
		if answer := make([]byte, 1); func() bool { _, err := io.ReadFull(c, answer); return err != nil || answer[0] != answerOpen }() {
			t.Fatalf("connection %d was not let through", i)
		}
	}
	busy := dialDoor(t, d, preambleFor(testSecret, "messages-server:8787"))
	if back, _ := io.ReadAll(busy); !bytes.Equal(back, []byte{answerBusy}) {
		t.Errorf("answer = %v, want busy", back)
	}
}

func TestOpenDoorKeepsItsSecret(t *testing.T) {
	if _, err := newDoor(testSecret[:16], throughNode); err == nil {
		t.Error("a short secret was taken")
	}
}

func TestChoose(t *testing.T) {
	peer := func(dns, host string, ip string, user int64, online bool) *ipnstate.PeerStatus {
		return &ipnstate.PeerStatus{DNSName: dns, HostName: host, TailscaleIPs: []netip.Addr{netip.MustParseAddr(ip)},
			UserID: tailcfg.UserID(user), Online: online, PublicKey: key.NewNode().Public()}
	}
	old := peer("messages-server.tail1234.ts.net.", "messages-server", "100.64.0.9", 1, false)
	current := peer("messages-server-1.tail1234.ts.net.", "messages-server", "100.64.0.10", 1, true)
	pc := peer("desk.tail1234.ts.net.", "DESK", "100.64.0.21", 1, true)
	stranger := peer("messages-server-2.tail1234.ts.net.", "messages-server", "100.64.0.30", 2, true)
	peers := map[string]*ipnstate.PeerStatus{"a": old, "b": current, "c": pc, "d": stranger}
	cases := []struct {
		target string
		want   *ipnstate.PeerStatus
	}{
		// The device holding the name, even offline (its nightly update): never another that only asked for it.
		{"messages-server", old},
		{"messages-server-1", current},
		{"MESSAGES-SERVER-1.tail1234.ts.net.", current},
		{"desk", pc},
		{"100.64.0.21", pc},
		{"100.64.0.99", nil},
		{"laptop", nil},
		{"", nil},
	}
	for _, c := range cases {
		if got := choose(peers, c.target, 1); got != c.want {
			t.Errorf("%q: got %v", c.target, got)
		}
	}
	// Among devices that only asked for the name, the owner's comes before a stranger's shared in, every time.
	delete(peers, "a")
	for i := 0; i < 20; i++ {
		if got := choose(peers, "messages-server", 1); got != current {
			t.Fatalf("after the old one left: got %v", got)
		}
	}
	// Between equals, the lower address, whatever order the map gives.
	twin := peer("messages-server-4.tail1234.ts.net.", "messages-server", "100.64.0.5", 1, true)
	peers["e"] = twin
	for i := 0; i < 20; i++ {
		if got := choose(peers, "messages-server", 1); got != twin {
			t.Fatalf("between equals: got %v", got)
		}
	}
}

func testPeer(id, dns, host, ip string, user int64, online bool) *ipnstate.PeerStatus {
	return &ipnstate.PeerStatus{ID: tailcfg.StableNodeID(id), DNSName: dns, HostName: host, TailscaleIPs: []netip.Addr{netip.MustParseAddr(ip)},
		UserID: tailcfg.UserID(user), Online: online, PublicKey: key.NewNode().Public()}
}

// The server the app joined is its stable id: found whatever it is called now, and never stood in for by another device
// that answers to its name.
func TestIdentify(t *testing.T) {
	server := testPeer(serverID, "messages-server.tail1234.ts.net.", "messages-server", "100.64.0.9", 1, true)
	pc := testPeer("nDeskCNTRL", "desk.tail1234.ts.net.", "DESK", "100.64.0.21", 1, true)
	peers := map[string]*ipnstate.PeerStatus{"a": server, "b": pc}
	if got, other := identify(peers, serverID, "messages-server", 1); got != server || other {
		t.Errorf("by its id: %v, %v", got, other)
	}
	// Renamed in the admin console: still the server.
	server.DNSName, server.HostName = "unraid.tail1234.ts.net.", "unraid"
	if got, other := identify(peers, serverID, "messages-server", 1); got != server || other {
		t.Errorf("renamed: %v, %v", got, other)
	}
	// Its Tailscale state reset: a new node holds the name, and the one with the id is gone. Refused, with why.
	delete(peers, "a")
	peers["c"] = testPeer("nN3wNodeCNTRL", "messages-server.tail1234.ts.net.", "messages-server", "100.64.0.10", 1, true)
	if got, other := identify(peers, serverID, "messages-server", 1); got != nil || !other {
		t.Errorf("another device on the name: %v, %v", got, other)
	}
	// Someone else's device, shared in, that asked for the name: the same.
	delete(peers, "c")
	peers["d"] = testPeer("nStrangerCNTRL", "messages-server-2.tail1234.ts.net.", "messages-server", "100.64.0.30", 2, true)
	if got, other := identify(peers, serverID, "messages-server", 1); got != nil || !other {
		t.Errorf("a stranger on the name: %v, %v", got, other)
	}
	// Nothing answers to the name either (the list may not have come yet): no device, and nothing refused.
	delete(peers, "d")
	if got, other := identify(peers, serverID, "messages-server", 1); got != nil || other {
		t.Errorf("nothing on the name: %v, %v", got, other)
	}
	if got, other := identify(peers, "", "desk", 1); got != nil || other {
		t.Errorf("no id: %v, %v", got, other)
	}
}

// Find the server again passes over the device the app kept only while it is offline and another answers to the name.
func TestPick(t *testing.T) {
	old := testPeer(serverID, "messages-server.tail1234.ts.net.", "messages-server", "100.64.0.9", 1, false)
	renewed := testPeer("nN3wNodeCNTRL", "messages-server-1.tail1234.ts.net.", "messages-server", "100.64.0.10", 1, true)
	peers := map[string]*ipnstate.PeerStatus{"a": old, "b": renewed}
	// The old node keeps the exact name, offline: choose alone gives it back every time.
	if got := pick(peers, "messages-server", 1, ""); got != old {
		t.Errorf("without avoid: %v", got)
	}
	if got := pick(peers, "messages-server", 1, serverID); got != renewed {
		t.Errorf("passing over the old node: %v", got)
	}
	// The kept device online (the person asked during a moment's failure): it is chosen as before.
	old.Online = true
	if got := pick(peers, "messages-server", 1, serverID); got != old {
		t.Errorf("kept device online: %v", got)
	}
	// Offline and the only one on the name: it is the answer, not nothing.
	old.Online = false
	delete(peers, "b")
	if got := pick(peers, "messages-server", 1, serverID); got != old {
		t.Errorf("only the kept device: %v", got)
	}
}

func TestParseInterfaces(t *testing.T) {
	list := "wlan0\t30\t1500\tumb\t192.168.1.20/24,fe80::1/64\nlo\t1\t65536\tul\t127.0.0.1/8\nbad line"
	got := parseInterfaces(list)
	if len(got) != 2 || got[0].Name != "wlan0" || got[0].MTU != 1500 || len(got[0].AltAddrs) != 2 || !got[1].IsLoopback() || !got[0].IsUp() {
		t.Fatalf("got %+v", got)
	}
	if got := parseInterfaces("rmnet0\t5\t1400\tup\t"); len(got) != 1 || got[0].AltAddrs == nil {
		t.Errorf("an interface without addresses: %+v", got)
	}
}
