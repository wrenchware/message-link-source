package tailnet

import (
	"context"
	"crypto/subtle"
	"errors"
	"io"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"time"

	"tailscale.com/ipn/ipnstate"
)

// The local door is how the app's own connections reach a device on the tailnet. OkHttp speaks TLS to the server itself,
// pinned to the server's certificate as on the PC, over a plain socket to this door on 127.0.0.1, which passes the bytes
// on through the node. Any app on the phone can connect to 127.0.0.1, so a connection must open with the secret the app
// made for this run of it; without that the door says nothing and closes, and no other app reaches the owner's tailnet
// through Messages.
//
// A connection opens with the secret (secretLength bytes), one byte giving the length of the target, and the target
// ("name:port", a device on the tailnet; or "name/id:port", only the device whose stable node id is id, which the app
// sends once it knows the server's). The door answers one byte: answerOpen, and from then on it passes bytes both ways,
// or one of the other answers, and closes.
const (
	secretLength = 32
	// maxDoorConnections bounds the connections passed on at once. The app holds one, and a few for pictures.
	maxDoorConnections = 16
	// maxWaiting bounds the connections still to say who they are. Any app on the phone can open one, so they never take
	// one of the places above: only a connection that gave the secret does.
	maxWaiting = 32
)

const (
	answerOpen = iota
	// answerOff: the node is not started.
	answerOff
	// answerSignedOut: the node needs signing in to Tailscale (first use, or its key expired).
	answerSignedOut
	// answerNoDevice: no device of that name on the tailnet.
	answerNoDevice
	// answerNoConnection: the device is there, but the connection to its port did not open.
	answerNoConnection
	// answerBusy: every place is taken.
	answerBusy
	// answerOtherDevice: the device with the id asked for is not on the tailnet, and another answers to its name.
	answerOtherDevice
)

var (
	// preambleWait is how long a connection may take to say who it is. A test shortens it.
	preambleWait = 2 * time.Second
	// connectWait bounds finding the device and opening the connection through the node. The app waits for the node's list
	// of devices before it connects (PhoneTailnet.open), so this is the connection itself, and it ends inside the 10 s the
	// app gives a connection to open: a device that does not answer is then said as such rather than as a timeout.
	connectWait = 8 * time.Second
)

type door struct {
	secret   []byte
	listener net.Listener
	connect  func(ctx context.Context, name, id string, port uint16) (net.Conn, byte)
	slots   chan struct{}
	waiting  chan struct{}
}

var (
	doorMu sync.Mutex
	open   *door
)

// OpenDoor opens the local door, once per run of the app, and returns its port on 127.0.0.1. Connections must open with
// secret, which the app makes for each run and never stores.
func OpenDoor(secret []byte) (int, error) {
	doorMu.Lock()
	defer doorMu.Unlock()
	if open != nil {
		if subtle.ConstantTimeCompare(open.secret, secret) != 1 {
			return 0, errors.New("the door is already open with another secret")
		}
		return open.port(), nil
	}
	d, err := newDoor(secret, throughNode)
	if err != nil {
		return 0, err
	}
	open = d
	return d.port(), nil
}

func newDoor(secret []byte, connect func(ctx context.Context, name, id string, port uint16) (net.Conn, byte)) (*door, error) {
	if len(secret) != secretLength {
		return nil, errors.New("the secret must be 32 bytes")
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	d := &door{secret: append([]byte(nil), secret...), listener: listener, connect: connect, slots: make(chan struct{}, maxDoorConnections),
		waiting: make(chan struct{}, maxWaiting)}
	go d.serve()
	return d, nil
}

func (d *door) port() int { return d.listener.Addr().(*net.TCPAddr).Port }

func (d *door) close() error { return d.listener.Close() }

func (d *door) serve() {
	for {
		c, err := d.listener.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
			time.Sleep(50 * time.Millisecond)
			continue
		}
		select {
		case d.waiting <- struct{}{}:
			go d.pass(c)
		default:
			c.Close()
		}
	}
}

func (d *door) pass(c net.Conn) {
	name, id, port, ok := d.preamble(c)
	<-d.waiting
	if !ok {
		c.Close()
		return
	}
	select {
	case d.slots <- struct{}{}:
		defer func() { <-d.slots }()
	default:
		c.Write([]byte{answerBusy})
		c.Close()
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), connectWait)
	far, answer := d.connect(ctx, name, id, port)
	cancel()
	if far == nil {
		if answer == answerOpen {
			answer = answerNoConnection
		}
		c.Write([]byte{answer})
		c.Close()
		return
	}
	if _, err := c.Write([]byte{answerOpen}); err != nil {
		c.Close()
		far.Close()
		return
	}
	splice(c, far)
}

// preamble reads the secret and the target, within preambleWait. A wrong secret ends it with nothing said.
func (d *door) preamble(c net.Conn) (name, id string, port uint16, ok bool) {
	c.SetDeadline(time.Now().Add(preambleWait))
	defer c.SetDeadline(time.Time{})
	head := make([]byte, secretLength+1)
	if _, err := io.ReadFull(c, head); err != nil {
		return "", "", 0, false
	}
	if subtle.ConstantTimeCompare(head[:secretLength], d.secret) != 1 || head[secretLength] == 0 {
		return "", "", 0, false
	}
	target := make([]byte, head[secretLength])
	if _, err := io.ReadFull(c, target); err != nil {
		return "", "", 0, false
	}
	host, portText, err := net.SplitHostPort(string(target))
	if err != nil {
		return "", "", 0, false
	}
	name, id, withID := strings.Cut(host, "/")
	if name == "" || withID && !nodeID(id) {
		return "", "", 0, false
	}
	p, err := strconv.ParseUint(portText, 10, 16)
	if err != nil || p == 0 {
		return "", "", 0, false
	}
	return name, id, uint16(p), true
}

// nodeID says whether id can be a stable node id: Tailscale's are letters and digits (n...CNTRL), and nothing that could
// be read as part of an address is let through.
func nodeID(id string) bool {
	if len(id) == 0 || len(id) > 64 {
		return false
	}
	for _, c := range id {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.') {
			return false
		}
	}
	return true
}

// throughNode finds the server on the tailnet (the device whose stable node id is id when the app gives one, and
// otherwise the device called name) and connects to its port through the node.
func throughNode(ctx context.Context, name, id string, port uint16) (net.Conn, byte) {
	s := node()
	if s == nil {
		return nil, answerOff
	}
	if st := read(ctx, s); st.State == "NeedsLogin" {
		return nil, answerSignedOut
	}
	var peer *ipnstate.PeerStatus
	var err error
	if id != "" {
		var other bool
		peer, _, other, err = lookupNode(ctx, s, id, name)
		if other {
			return nil, answerOtherDevice
		}
	} else {
		peer, _, err = lookup(ctx, s, name, "")
	}
	if err != nil {
		return nil, answerNoDevice
	}
	conn, err := s.Dial(ctx, "tcp", netip.AddrPortFrom(firstIP(peer.TailscaleIPs), port).String())
	if err != nil {
		return nil, answerNoConnection
	}
	return conn, answerOpen
}

// splice copies both ways until either side is done, passing a half-close on, then gives the other direction a few
// seconds to finish (a WebSocket's closing frames) before both are closed. As the server's door does (4.7).
func splice(a, b net.Conn) {
	done := make(chan struct{}, 2)
	copyHalf := func(to, from net.Conn) {
		io.Copy(to, from)
		if half, ok := to.(interface{ CloseWrite() error }); ok {
			half.CloseWrite()
		}
		done <- struct{}{}
	}
	go copyHalf(a, b)
	go copyHalf(b, a)
	<-done
	select {
	case <-done:
	case <-time.After(5 * time.Second):
	}
	a.Close()
	b.Close()
}
