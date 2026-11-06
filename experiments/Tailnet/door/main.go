// Command messages-tailnet is Messages Server's own door on the owner's tailnet (SWITCHBOARD.md 4.7, chosen
// 2026-10-15). It is a Tailscale node of its own inside the server's container (tsnet: userspace, no capability, no
// tun device), so the server is on the tailnet without Unraid publishing its port anywhere but the home network, and
// whatever Unraid's own Tailscale does to its firewall. It ends TLS nowhere: each connection a device makes to it is
// passed byte for byte to the server's relay socket, after one PROXY protocol line naming the device's tailnet address,
// so the server's pinned certificate and device tokens are the same end to end, and its per-address pacing and its
// home-network rule see each device as itself (NetworkHost's relay listener).
//
// Unraid's Tailscale plugin runs tailscaled on the host with its firewall rules on: after it restarts, every packet it
// forwards to a container is masqueraded to the Docker bridge's address, so a server reached through the host's port
// sees all tailnet devices as one (4.7, read from Tailscale's linuxfw source). This door is not subject to that.
//
// The server starts it (TailnetDoor.cs) with a clean environment and reads its standard output: one line per event, fixed
// words, counts and states. The sign-in link and the Tailnet Lock key go to the container's own log, and with the rest of what the
// server's own page shows, in one "tailnet page" line the server keeps in memory for the page and never logs (page.go). Tailscale's own
// log is off (init): nothing is uploaded, and its files in the state folder stay empty. The door ends when its standard
// input ends (the server stopping),
// and on Linux with the server however it ends (parentwatch_linux.go); on Windows the server keeps it in its helpers' job,
// which ends it with the server, and its node's keys are sealed to the user (store_windows.go). With --authkey-stdin the first line of that input
// is a Tailscale auth key for a headless sign-in (MESSAGES_TAILNET_AUTHKEY, readAuthKey), so the key is in no argument
// and no environment. A later "logout" line signs the node out and ends the door with forgetCode (commands). A node that
// Tailscale signs out while it runs asks for a new sign-in link by itself (needsLink).
package main

import (
	"bufio"
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"tailscale.com/client/local"
	"tailscale.com/ipn"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/logtail"
	"tailscale.com/tsnet"
)

func init() {
	// tsnet uploads its diagnostic log to Tailscale unless told not to. It names the tailnet's machines and addresses.
	logtail.Disable()
}

// maxConnections bounds the connections passed on at once. The server's own limits (MaxUpgradedConnections, 64, and the
// door's pending places) are the real ones; this only keeps a flood from holding goroutines and sockets here first.
const maxConnections = 96

var labelPattern = regexp.MustCompile(`^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`)

func main() {
	state := flag.String("state", "", "the node's state folder (in the server's keys volume)")
	socket := flag.String("socket", "", "the server's relay socket")
	hostname := flag.String("hostname", "messagelink-server", "the node's name on the tailnet")
	port := flag.Int("port", 8787, "the port devices connect to on the tailnet")
	keyOnInput := flag.Bool("authkey-stdin", false, "read a Tailscale auth key from the first line of standard input")
	flag.Parse()
	explicitName := false
	flag.Visit(func(f *flag.Flag) {
		if f.Name == "hostname" {
			explicitName = true
		}
	})
	if !filepath.IsAbs(*state) || *socket == "" || !labelPattern.MatchString(*hostname) || *port < 1 || *port > 65535 || flag.NArg() != 0 {
		say("tailnet start failed reason=arguments")
		os.Exit(2)
	}
	if !explicitName {
		chosen, err := defaultNodeName(*state)
		if err != nil {
			say("tailnet start failed reason=node_name")
			os.Exit(1)
		}
		*hostname = chosen
	}
	input := bufio.NewReaderSize(os.Stdin, maxKeyLine)
	authKey := ""
	if *keyOnInput {
		key, ended := readAuthKey(input)
		if ended {
			// The server stopped before it said anything: nothing was started.
			say("tailnet stopped")
			os.Exit(0)
		}
		authKey = key
	}
	os.Exit(run(*state, *socket, *hostname, *port, authKey, input))
}

// maxKeyLine bounds the auth key's line. Tailscale's keys are well under it.
const maxKeyLine = 1024

var keyPattern = regexp.MustCompile(`^tskey-[A-Za-z0-9_-]{8,500}$`)

// readAuthKey takes the auth key for a headless sign-in (MESSAGES_TAILNET_AUTHKEY) from the first line of the input,
// so it is in no argument or environment another process can read. The rest of the input is the server's stop signal
// as before. A key that is not of Tailscale's form is dropped with a fixed word (the sign-in link is still offered);
// ended is true when the input ended before a whole line came.
func readAuthKey(input *bufio.Reader) (key string, ended bool) {
	line, err := input.ReadSlice('\n')
	if err == bufio.ErrBufferFull {
		// Too long to be a key: drop the rest of the line, keep the input for the stop.
		for err == bufio.ErrBufferFull {
			_, err = input.ReadSlice('\n')
		}
		if err != nil {
			return "", true
		}
		say("tailnet authkey refused reason=form")
		return "", false
	}
	if err != nil {
		return "", true
	}
	text := strings.TrimRight(string(line), "\r\n")
	if !keyPattern.MatchString(text) {
		say("tailnet authkey refused reason=form")
		return "", false
	}
	return text, false
}

func run(state, socket, hostname string, port int, authKey string, input io.Reader) int {
	if err := os.MkdirAll(state, 0o700); err != nil {
		say("tailnet start failed reason=state_folder")
		return 1
	}
	// Tailscale looks for a writable folder for its local log state and panics without one; the server gives this
	// process no HOME, so it is named. Nothing is uploaded (init).
	for name, sub := range map[string]string{"TS_LOGS_DIR": "logs", "TMPDIR": "tmp"} {
		path := filepath.Join(state, sub)
		if err := os.MkdirAll(path, 0o700); err != nil {
			say("tailnet start failed reason=state_folder")
			return 1
		}
		os.Setenv(name, path)
	}
	discard := func(string, ...any) {}
	// The auth key, when the server was given one, is used only while the node has no identity of its own yet; a node
	// already signed in ignores it. A tagged key makes a tagged node, which turns the same-user check off (relay.go).
	// On Windows the node's keys are sealed to the user (store_windows.go); elsewhere tsnet's own file in the state folder.
	store, err := newStateStore(state)
	if err != nil {
		say("tailnet start failed reason=state")
		return 1
	}
	server := &tsnet.Server{Dir: state, Store: store, Hostname: hostname, AuthKey: authKey, Logf: discard, UserLogf: discard}
	if authKey != "" {
		say("tailnet authkey=given")
	}
	say("tailnet starting")
	if err := server.Start(); err != nil {
		say("tailnet start failed reason=start")
		server.Close()
		return 1
	}
	client, err := server.LocalClient()
	if err != nil {
		say("tailnet start failed reason=local_client")
		server.Close()
		return 1
	}
	listener, err := server.Listen("tcp", fmt.Sprintf(":%d", port))
	if err != nil {
		say("tailnet start failed reason=listen")
		server.Close()
		return 1
	}
	say(fmt.Sprintf("tailnet listening port=%d", port))

	ctx, cancel := context.WithCancel(context.Background())
	door := &door{client: client, port: port, keyed: authKey != "", dial: func() (net.Conn, error) { return net.DialTimeout("unix", socket, 5*time.Second) }}
	door.who = door.whois
	var watching sync.WaitGroup
	watching.Add(1)
	go func() {
		defer watching.Done()
		door.watch(ctx)
	}()
	// The server stops this process by ending its input (or, on Linux, by ending: parentwatch_linux.go). A "logout" line on it is "Use another
	// Tailscale account" on the server's page: the node signs out of Tailscale, and this process ends with forgetCode so the server forgets its state
	// and starts it afresh, which asks for a new sign-in link.
	var forgetting atomic.Bool
	go func() {
		commands(input, func() {
			forgetting.Store(true)
			logoutCtx, done := context.WithTimeout(ctx, 15*time.Second)
			defer done()
			if err := client.Logout(logoutCtx); err != nil {
				say("tailnet logout failed reason=logout")
			} else {
				say("tailnet logged_out")
			}
		})
		cancel()
		listener.Close()
	}()
	door.serve(ctx, listener)
	cancel()
	watching.Wait()
	server.Close()
	if forgetting.Load() {
		say("tailnet stopped reason=forget")
		return forgetCode
	}
	say("tailnet stopped")
	return 0
}

// forgetCode is how the door ends after "logout": the server then removes its state folder and starts it again at once (TailnetDoor.cs).
const forgetCode = 3

// commands reads the server's lines after the auth key until the input ends. "logout" signs the node out (then the input is no longer read: the door is
// ending); any other line is ignored, so a later server can say more without an older door misreading it.
func commands(input io.Reader, logout func()) {
	lines := bufio.NewReaderSize(input, maxKeyLine)
	for {
		line, err := lines.ReadSlice('\n')
		if err == bufio.ErrBufferFull {
			for err == bufio.ErrBufferFull {
				_, err = lines.ReadSlice('\n')
			}
			if err != nil {
				return
			}
			continue
		}
		if err != nil {
			return
		}
		if strings.TrimRight(string(line), "\r\n") == "logout" {
			logout()
			return
		}
	}
}

// loginRetry is how often a node signed out with no sign-in link asks Tailscale for one again.
const loginRetry = time.Minute

// needsLink says whether to ask Tailscale for a sign-in link now: the node is signed out (its key expired, it was logged out, or a key Tailscale refused
// was given up on) and has no link to show, and it has not asked within loginRetry. tsnet asks only when it starts, so without this a node signed out
// while running would show nothing to open until a restart. A node still trying its auth key is left to it (keyGrace).
func needsLink(state, authURL string, tryingKey bool, lastAsked, now time.Time) bool {
	return state == "NeedsLogin" && authURL == "" && !tryingKey && now.Sub(lastAsked) >= loginRetry
}

// keyGrace is how long a node given an auth key may sit signed out with no sign-in link before the key counts as
// refused (expired, revoked, used up) and the link is asked for, as without a key.
const keyGrace = 30 * time.Second

type door struct {
	client *local.Client
	port   int
	keyed  bool
	dial   func() (net.Conn, error)
	who    func(context.Context, netip.AddrPort) (identity, error)

	self     atomic.Pointer[identity]
	open     atomic.Int64
	relayed  atomic.Int64
	refused  counter
	dropped  counter
	failures counter
}

// serve accepts until the listener closes, passing each connection on in a goroutine of its own.
func (d *door) serve(ctx context.Context, listener net.Listener) {
	for {
		conn, err := listener.Accept()
		if err != nil {
			if ctx.Err() != nil || errors.Is(err, net.ErrClosed) {
				return
			}
			time.Sleep(100 * time.Millisecond)
			continue
		}
		if d.open.Add(1) > maxConnections {
			d.open.Add(-1)
			d.dropped.note("tailnet connection dropped reason=full")
			conn.Close()
			continue
		}
		go func() {
			defer d.open.Add(-1)
			d.pass(ctx, conn)
		}()
	}
}

// pass admits one connection by who is making it, then splices it to the server's relay socket behind its PROXY line.
func (d *door) pass(ctx context.Context, conn net.Conn) {
	defer conn.Close()
	peer, ok := addrPort(conn.RemoteAddr())
	local, ok2 := addrPort(conn.LocalAddr())
	if !ok || !ok2 {
		d.refused.note("tailnet connection refused reason=address")
		return
	}
	asking, err := d.who(ctx, peer)
	if err != nil {
		d.refused.note("tailnet connection refused reason=unknown")
		return
	}
	self := d.self.Load()
	if self == nil {
		d.refused.note("tailnet connection refused reason=not_ready")
		return
	}
	if allowed, why := admit(*self, asking); !allowed {
		d.refused.note("tailnet connection refused reason=" + why)
		return
	}
	upstream, err := d.dial()
	if err != nil {
		d.failures.note("tailnet relay failed reason=socket")
		return
	}
	defer upstream.Close()
	if _, err := io.WriteString(upstream, proxyLine(peer, local)); err != nil {
		d.failures.note("tailnet relay failed reason=write")
		return
	}
	d.relayed.Add(1)
	splice(conn, upstream)
}

func (d *door) whois(ctx context.Context, peer netip.AddrPort) (identity, error) {
	ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	who, err := d.client.WhoIs(ctx, peer.String())
	if err != nil {
		return identity{}, err
	}
	if who.Node == nil {
		return identity{}, errors.New("no node")
	}
	return identity{user: int64(who.Node.User), tagged: who.Node.IsTagged()}, nil
}

// splice copies both ways until either side is done, passing a half-close on, then gives the other direction a few
// seconds to finish (a WebSocket's closing frames) before both are closed.
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

// watch follows the node's state every few seconds: what changed is said once, the sign-in link and the Tailnet Lock key
// are said again every ten minutes while they are needed, the key's expiry each day, and how the active devices reach
// the node (direct or through Tailscale's relays) whenever that changes.
func (d *door) watch(ctx context.Context) {
	var lastState, lastURL, lastPaths, lastPage string
	lastCounts := "relayed=0 refused=0"
	var urlSaid, lockSaid, expirySaid, countsSaid, linkAsked time.Time
	started, keyGivenUp, wasRunning := time.Now(), false, false
	tick := time.NewTicker(5 * time.Second)
	defer tick.Stop()
	for {
		status, err := d.status(ctx, false)
		if err == nil {
			if status.BackendState != lastState {
				say("tailnet state=" + word(status.BackendState))
				lastState = status.BackendState
			}
			// tsnet asks for a sign-in link only when it starts without a key, so a key Tailscale refused would leave the
			// node signed out with nothing to open. Its control client keeps trying the key, so the backend is started
			// again without one (the same preferences), then the link is asked for once, and the page and log show it.
			if d.keyed && !keyGivenUp && status.BackendState == "NeedsLogin" && status.AuthURL == "" && time.Since(started) > keyGrace {
				keyGivenUp = true
				say("tailnet authkey refused reason=rejected")
				if err := d.client.Start(ctx, ipn.Options{}); err != nil {
					say("tailnet login failed reason=restart")
				} else if err := d.client.StartLoginInteractive(ctx); err != nil {
					say("tailnet login failed reason=interactive")
				}
				linkAsked = time.Now()
			}
			// Signed out while running (the key expired, or the machine was logged out in Tailscale): a new link, so the page offers "Sign in again".
			if needsLink(status.BackendState, status.AuthURL, d.keyed && !keyGivenUp, linkAsked, time.Now()) {
				linkAsked = time.Now()
				if err := d.client.StartLoginInteractive(ctx); err != nil {
					say("tailnet login failed reason=interactive")
				} else {
					say("tailnet login asked reason=signed_out")
				}
			}
			if status.BackendState == "Running" {
				wasRunning = true
			}
			if status.Self != nil && status.BackendState == "Running" {
				self := identity{user: int64(status.Self.UserID), tagged: status.Self.Tags != nil && status.Self.Tags.Len() > 0}
				if previous := d.self.Swap(&self); previous == nil || previous.tagged != self.tagged {
					if self.tagged {
						say("tailnet owner_check=off reason=tagged")
					} else {
						say("tailnet owner_check=on")
					}
				}
				if time.Since(expirySaid) > 24*time.Hour {
					expirySaid = time.Now()
					if status.Self.KeyExpiry == nil {
						say("tailnet key expiry=off")
					} else {
						say(fmt.Sprintf("tailnet key expires_days=%d", int(time.Until(*status.Self.KeyExpiry).Hours()/24)))
					}
				}
			}
			if status.AuthURL != "" && (status.AuthURL != lastURL || time.Since(urlSaid) > 10*time.Minute) {
				say("tailnet login url=" + status.AuthURL)
				lastURL, urlSaid = status.AuthURL, time.Now()
			}
			if status.BackendState == "Running" && time.Since(lockSaid) > 10*time.Minute {
				if lock, err := d.client.NetworkLockStatus(ctx); err == nil && lock.Enabled && !lock.NodeKeySigned && lock.NodeKey != nil {
					say("tailnet lock sign node_key=" + lock.NodeKey.String())
					lockSaid = time.Now()
				}
			}
			seen := status
			if status.BackendState == "Running" {
				if full, err := d.status(ctx, true); err == nil {
					seen = full
					if paths := pathCounts(full); paths != lastPaths {
						say("tailnet paths " + paths)
						lastPaths = paths
					}
				}
			}
			// What the server's page shows, whenever any of it changes (page.go).
			if status.BackendState == "NeedsMachineAuth" {
				if full, err := d.status(ctx, true); err == nil {
					seen = full
				}
			}
			if page := d.view(ctx, seen, d.port, wasRunning).line(); page != "" && page != lastPage {
				say(page)
				lastPage = page
			}
		}
		if counts := fmt.Sprintf("relayed=%d refused=%d", d.relayed.Load(), d.refused.total()); counts != lastCounts && time.Since(countsSaid) > time.Hour {
			say("tailnet connections " + counts)
			lastCounts, countsSaid = counts, time.Now()
		}
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
	}
}

func (d *door) status(ctx context.Context, peers bool) (*ipnstate.Status, error) {
	ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	if peers {
		return d.client.Status(ctx)
	}
	return d.client.StatusWithoutPeers(ctx)
}

// pathCounts says how the devices active in the last minutes reach this node: direct (a path of their own) or relayed
// (through Tailscale's DERP servers). A relayed phone at home means the direct path through Docker's network failed.
func pathCounts(status *ipnstate.Status) string {
	direct, relayed := 0, 0
	for _, peer := range status.Peer {
		if peer == nil || !peer.Active {
			continue
		}
		if peer.CurAddr != "" {
			direct++
		} else {
			relayed++
		}
	}
	return fmt.Sprintf("direct=%d relayed=%d", direct, relayed)
}

// word keeps a state name to the fixed words the server's log takes.
func word(s string) string {
	if s == "" {
		return "none"
	}
	for _, c := range s {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') {
			return "other"
		}
	}
	return s
}

func addrPort(a net.Addr) (netip.AddrPort, bool) {
	if tcp, ok := a.(*net.TCPAddr); ok {
		p := tcp.AddrPort()
		return netip.AddrPortFrom(p.Addr().Unmap(), p.Port()), p.IsValid()
	}
	p, err := netip.ParseAddrPort(a.String())
	if err != nil {
		return netip.AddrPort{}, false
	}
	return netip.AddrPortFrom(p.Addr().Unmap(), p.Port()), true
}

var out sync.Mutex

// say writes one line for the server to read.
func say(line string) {
	out.Lock()
	defer out.Unlock()
	fmt.Fprintln(os.Stdout, line)
}

// counter says a repeated event the first time and then once in a hundred, with the count, as the server's door does.
type counter struct{ n atomic.Int64 }

func (c *counter) note(line string) {
	if n := c.n.Add(1); n == 1 {
		say(line)
	} else if n%100 == 0 {
		say(fmt.Sprintf("%s count=%d", line, n))
	}
}

func (c *counter) total() int64 { return c.n.Load() }
