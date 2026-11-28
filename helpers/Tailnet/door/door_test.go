package main

import (
	"bufio"
	"context"
	"errors"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestReadAuthKey(t *testing.T) {
	long := "tskey-auth-" + strings.Repeat("a", 2000)
	cases := []struct {
		input, key string
		ended      bool
		rest       string
	}{
		{"tskey-auth-kAbc123CNTRL-xyz789\nmore", "tskey-auth-kAbc123CNTRL-xyz789", false, "more"},
		{"tskey-auth-kAbc123CNTRL-xyz789\r\n", "tskey-auth-kAbc123CNTRL-xyz789", false, ""},
		// Not Tailscale's form: dropped, and the sign-in link is offered as before.
		{"hunter2-not-a-key\n", "", false, ""},
		{"tskey-auth-has space\n", "", false, ""},
		{"\n", "", false, ""},
		{long + "\nrest", "", false, "rest"},
		// The server stopped before it wrote a whole line.
		{"", "", true, ""},
		{"tskey-auth-kAbc123CNTRL", "", true, ""},
		{long, "", true, ""},
	}
	for _, c := range cases {
		input := bufio.NewReaderSize(strings.NewReader(c.input), maxKeyLine)
		key, ended := readAuthKey(input)
		rest, _ := io.ReadAll(input)
		if key != c.key || ended != c.ended || (!ended && string(rest) != c.rest) {
			t.Errorf("readAuthKey(%.40q) = %q %v (rest %q), want %q %v (rest %q)", c.input, key, ended, rest, c.key, c.ended, c.rest)
		}
	}
}

func TestAdmit(t *testing.T) {
	owner, other := identity{user: 7}, identity{user: 9}
	cases := []struct {
		self, peer identity
		allowed    bool
		why        string
	}{
		{owner, owner, true, ""},
		{owner, other, false, "other_user"},
		{owner, identity{user: 7, tagged: true}, false, "tagged"},
		// A door whose own user is not known lets nobody in on its own say.
		{identity{}, identity{}, false, "other_user"},
		// A tagged door leaves the choice to the tailnet's access rules, but a tagged device is still no person's.
		{identity{tagged: true}, other, true, ""},
		{identity{tagged: true}, identity{tagged: true}, false, "tagged"},
	}
	for _, c := range cases {
		if allowed, why := admit(c.self, c.peer); allowed != c.allowed || why != c.why {
			t.Errorf("admit(%+v, %+v) = %v %q, want %v %q", c.self, c.peer, allowed, why, c.allowed, c.why)
		}
	}
}

func TestProxyLine(t *testing.T) {
	v4 := proxyLine(netip.MustParseAddrPort("100.101.102.103:51234"), netip.MustParseAddrPort("100.64.0.5:8787"))
	if v4 != "PROXY TCP4 100.101.102.103 100.64.0.5 51234 8787\r\n" {
		t.Errorf("IPv4 line %q", v4)
	}
	// An IPv4 address carried in IPv6 form is written as IPv4, as the server reads it.
	mapped := proxyLine(netip.MustParseAddrPort("[::ffff:100.101.102.103]:1"), netip.MustParseAddrPort("[::ffff:100.64.0.5]:8787"))
	if mapped != "PROXY TCP4 100.101.102.103 100.64.0.5 1 8787\r\n" {
		t.Errorf("mapped line %q", mapped)
	}
	v6 := proxyLine(netip.MustParseAddrPort("[fd7a:115c:a1e0::1]:40000"), netip.MustParseAddrPort("[fd7a:115c:a1e0::2]:8787"))
	if v6 != "PROXY TCP6 fd7a:115c:a1e0::1 fd7a:115c:a1e0::2 40000 8787\r\n" {
		t.Errorf("IPv6 line %q", v6)
	}
	// The longest line the protocol allows is 107 bytes; the server refuses more.
	longest := proxyLine(netip.MustParseAddrPort("[ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffe]:65535"), netip.MustParseAddrPort("[ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffe]:65535"))
	if len(longest) > 107 {
		t.Errorf("a line of %d bytes", len(longest))
	}
}

// One connection through the door end to end: a loopback TCP listener stands in for the tailnet, a unix socket for the
// server's relay socket. The device's bytes arrive after the PROXY line, the server's come back, and a refused device
// reaches nothing.
func TestPass(t *testing.T) {
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
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	// The first device to connect belongs to another user, every later one to the owner.
	var asked atomic.Int32
	d := &door{dial: func() (net.Conn, error) { return net.Dial("unix", socket) }}
	d.self.Store(&identity{user: 7})
	d.who = func(context.Context, netip.AddrPort) (identity, error) {
		if asked.Add(1) == 1 {
			return identity{user: 9}, nil
		}
		return identity{user: 7}, nil
	}
	go d.serve(ctx, tailnet)
	defer tailnet.Close()

	got := make(chan string, 4)
	go func() {
		for {
			conn, err := server.Accept()
			if err != nil {
				return
			}
			go func() {
				defer conn.Close()
				reader := bufio.NewReader(conn)
				line, _ := reader.ReadString('\n')
				body, _ := reader.ReadString('\n')
				got <- line + body
				io.WriteString(conn, "hello from the server\n")
			}()
		}
	}()

	// Refused first: nothing reaches the server, and the device's connection is closed.
	refused, err := net.Dial("tcp", tailnet.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	refused.SetReadDeadline(time.Now().Add(5 * time.Second))
	if n, err := refused.Read(make([]byte, 1)); n != 0 || err == nil || errors.Is(err, os.ErrDeadlineExceeded) {
		t.Fatalf("a device of another user was not closed: %d %v", n, err)
	}
	refused.Close()

	device, err := net.Dial("tcp", tailnet.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer device.Close()
	io.WriteString(device, "hello from the device\n")
	select {
	case seen := <-got:
		want := "PROXY TCP4 127.0.0.1 127.0.0.1 " + strconv.Itoa(device.LocalAddr().(*net.TCPAddr).Port) + " " + strconv.Itoa(tailnet.Addr().(*net.TCPAddr).Port) + "\r\nhello from the device\n"
		if seen != want {
			t.Fatalf("the server read %q, want %q", seen, want)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("nothing reached the server")
	}
	device.SetReadDeadline(time.Now().Add(5 * time.Second))
	answer, err := bufio.NewReader(device).ReadString('\n')
	if err != nil || answer != "hello from the server\n" {
		t.Fatalf("the device read %q %v", answer, err)
	}
	if d.relayed.Load() != 1 || d.refused.total() != 1 {
		t.Fatalf("relayed %d refused %d", d.relayed.Load(), d.refused.total())
	}
}
