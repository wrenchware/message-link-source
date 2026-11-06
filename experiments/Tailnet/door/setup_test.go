package main

import (
	"bufio"
	"context"
	"io"
	"net"
	"net/netip"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"tailscale.com/ipn/ipnstate"
	"tailscale.com/types/key"
	"tailscale.com/types/views"
)

// The server's lines after the key: "logout" is acted on once and ends the reading; anything else is passed over, a line too long included; the input's end
// ends it without a logout.
func TestCommands(t *testing.T) {
	cases := []struct {
		input   string
		logouts int
	}{
		{"logout\n", 1},
		{"logout\r\nlogout\n", 1},
		{"something later\nlogout\n", 1},
		{strings.Repeat("x", 3*maxKeyLine) + "\nlogout\n", 1},
		{"log out\nLOGOUT\n", 0},
		{"logout", 0},
		{"", 0},
	}
	for _, c := range cases {
		done := make(chan struct{})
		count := 0
		go func() {
			commands(strings.NewReader(c.input), func() { count++ })
			close(done)
		}()
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			t.Fatalf("commands(%.20q) did not end with its input", c.input)
		}
		if count != c.logouts {
			t.Errorf("commands(%.20q) logged out %d times, want %d", c.input, count, c.logouts)
		}
	}
}

// A node signed out while it runs asks for a new link, once a minute at most, and never while a link is showing or its auth key is still being tried.
func TestNeedsLink(t *testing.T) {
	now := time.Date(2026, 10, 30, 12, 0, 0, 0, time.UTC)
	cases := []struct {
		state, url string
		trying     bool
		asked      time.Time
		want       bool
	}{
		{"NeedsLogin", "", false, time.Time{}, true},
		{"NeedsLogin", "", false, now.Add(-2 * time.Minute), true},
		{"NeedsLogin", "", false, now.Add(-10 * time.Second), false},
		{"NeedsLogin", "https://login.tailscale.com/a/abc", false, time.Time{}, false},
		{"NeedsLogin", "", true, time.Time{}, false},
		{"Running", "", false, time.Time{}, false},
		{"NeedsMachineAuth", "", false, time.Time{}, false},
		{"Starting", "", false, time.Time{}, false},
	}
	for _, c := range cases {
		if got := needsLink(c.state, c.url, c.trying, c.asked, now); got != c.want {
			t.Errorf("needsLink(%q, %q, %v, %v) = %v, want %v", c.state, c.url, c.trying, c.asked, got, c.want)
		}
	}
}

// The page names the owner's Messages phones by their node names, with each key's expiry, and counts the people on the tailnet: a server, another
// person's phone named like ours, and tagged devices are never listed as the owner's phones.
func TestPhones(t *testing.T) {
	expires := time.Date(2027, 4, 1, 0, 0, 0, 0, time.UTC)
	tagged := views.SliceOf([]string{"tag:server"})
	status := &ipnstate.Status{
		Self: &ipnstate.PeerStatus{DNSName: "messages-server.tail1234.ts.net.", UserID: 7, TailscaleIPs: []netip.Addr{netip.MustParseAddr("100.64.0.10")}},
		Peer: map[key.NodePublic]*ipnstate.PeerStatus{
			key.NewNode().Public(): {DNSName: "messages-pixel.tail1234.ts.net.", UserID: 7, TailscaleIPs: []netip.Addr{netip.MustParseAddr("100.64.0.20")}, KeyExpiry: &expires},
			key.NewNode().Public(): {DNSName: "messages-galaxy.tail1234.ts.net.", UserID: 7, TailscaleIPs: []netip.Addr{netip.MustParseAddr("100.64.0.21")}},
			key.NewNode().Public(): {DNSName: "messages-server-1.tail1234.ts.net.", UserID: 7},
			key.NewNode().Public(): {DNSName: "laptop.tail1234.ts.net.", UserID: 7},
			key.NewNode().Public(): {DNSName: "messages-theirs.tail1234.ts.net.", UserID: 9},
			key.NewNode().Public(): {DNSName: "messages-tagged.tail1234.ts.net.", UserID: 7, Tags: &tagged},
		},
	}
	found, users := phones(status)
	if len(found) != 2 || found[0] != (phoneView{Name: "messages-galaxy", Address: "100.64.0.21", Expiry: "off"}) ||
		found[1] != (phoneView{Name: "messages-pixel", Address: "100.64.0.20", Expiry: "2027-04-01T00:00:00Z"}) {
		t.Errorf("phones = %+v", found)
	}
	if users != 2 {
		t.Errorf("users = %d, want 2", users)
	}
	// New phones are recognized beside legacy nodes, while new server names stay excluded.
	status.Peer[key.NewNode().Public()] = &ipnstate.PeerStatus{DNSName: "messagelink-phone.tail1234.ts.net.", UserID: 7}
	status.Peer[key.NewNode().Public()] = &ipnstate.PeerStatus{DNSName: "messagelink-server-1.tail1234.ts.net.", UserID: 7}
	if found, _ := phones(status); len(found) != 3 {
		t.Errorf("mixed prefixes: %+v", found)
	}
	status.Peer = nil
	if found, users := phones(status); len(found) != 0 || users != 1 {
		t.Errorf("alone: phones = %+v users = %d", found, users)
	}
}

// A node that was signed in and is now asked to sign in again says so, as does one whose key has expired; one never signed in does not. One waiting for
// the tailnet's admin to approve it carries its name and address so the page can link to it.
func TestViewSignedOut(t *testing.T) {
	d := &door{}
	past := time.Now().Add(-time.Hour)
	waiting := &ipnstate.Status{BackendState: "NeedsLogin", AuthURL: "https://login.tailscale.com/a/abc"}
	if v := d.view(context.Background(), waiting, 8787, false); v.SignedOut || v.Login == "" {
		t.Errorf("a first sign-in: %+v", v)
	}
	if v := d.view(context.Background(), waiting, 8787, true); !v.SignedOut {
		t.Errorf("signed out while running: %+v", v)
	}
	waiting.Self = &ipnstate.PeerStatus{DNSName: "messages-server.tail1234.ts.net.", KeyExpiry: &past}
	if v := d.view(context.Background(), waiting, 8787, false); !v.SignedOut {
		t.Errorf("expired key: %+v", v)
	}
	approval := &ipnstate.Status{BackendState: "NeedsMachineAuth",
		Self: &ipnstate.PeerStatus{DNSName: "messages-server.tail1234.ts.net.", TailscaleIPs: []netip.Addr{netip.MustParseAddr("100.64.0.10")}}}
	if v := d.view(context.Background(), approval, 8787, false); v.Name != "messages-server" || v.Address != "100.64.0.10" || v.State != "NeedsMachineAuth" {
		t.Errorf("approval: %+v", v)
	}
}

// What a phone sends to pair through the door (an upgrade carrying the pairing code, then pair.claim) reaches the server's relay socket byte for byte after
// the PROXY line, and the server's answer comes back the same: the door reads nothing of it.
func TestPassPairing(t *testing.T) {
	socket := filepath.Join(t.TempDir(), "relay.sock")
	server, err := net.Listen("unix", socket)
	if err != nil {
		t.Skipf("no unix sockets here: %v", err)
	}
	defer server.Close()
	tailnet, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer tailnet.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	d := &door{dial: func() (net.Conn, error) { return net.Dial("unix", socket) }}
	d.self.Store(&identity{user: 7})
	d.who = func(context.Context, netip.AddrPort) (identity, error) { return identity{user: 7}, nil }
	go d.serve(ctx, tailnet)

	request := "GET /v1/socket HTTP/1.1\r\nHost: messages-server:8787\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nX-Messages-Pair: ABCDE-FGHJK\r\n" +
		"Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"
	answer := "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n"
	got := make(chan string, 1)
	go func() {
		conn, err := server.Accept()
		if err != nil {
			return
		}
		defer conn.Close()
		reader := bufio.NewReader(conn)
		line, _ := reader.ReadString('\n')
		body := make([]byte, len(request))
		io.ReadFull(reader, body)
		got <- line + string(body)
		io.WriteString(conn, answer)
		time.Sleep(time.Second)
	}()
	device, err := net.Dial("tcp", tailnet.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer device.Close()
	io.WriteString(device, request)
	select {
	case seen := <-got:
		if !strings.HasPrefix(seen, "PROXY TCP4 127.0.0.1 127.0.0.1 ") || !strings.HasSuffix(seen, "\r\n"+request) {
			t.Fatalf("the server read %q", seen)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("nothing reached the server")
	}
	device.SetReadDeadline(time.Now().Add(5 * time.Second))
	back := make([]byte, len(answer))
	if _, err := io.ReadFull(device, back); err != nil || string(back) != answer {
		t.Fatalf("the device read %q %v", back, err)
	}
}
