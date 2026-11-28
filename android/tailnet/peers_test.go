package tailnet

import (
	"encoding/json"
	"net/netip"
	"testing"

	"tailscale.com/ipn/ipnstate"
	"tailscale.com/tailcfg"
	"tailscale.com/types/key"
	"tailscale.com/types/views"
)

// Peers' answer lists every device but this one, in the shape the PC reads, and in a fixed order.
func TestPeerList(t *testing.T) {
	self := &ipnstate.PeerStatus{ID: "nSelf1CNTRL", DNSName: "messagelink-desk.tail1234.ts.net.", UserID: 1,
		TailscaleIPs: []netip.Addr{netip.MustParseAddr("100.64.0.5")}}
	tags := views.SliceOf([]string{"tag:server"})
	peers := map[string]*ipnstate.PeerStatus{
		"server": {ID: serverID, DNSName: "messagelink-server.tail1234.ts.net.", HostName: "messagelink-server", OS: "linux",
			UserID: 1, Online: true, PublicKey: key.NewNode().Public(),
			TailscaleIPs: []netip.Addr{netip.MustParseAddr("fd7a:115c:a1e0::9"), netip.MustParseAddr("100.64.0.9")}},
		"tagged": {ID: "nTagged1CNTRL", DNSName: "box.tail1234.ts.net.", OS: "linux", UserID: 99, Tags: &tags,
			TailscaleIPs: []netip.Addr{netip.MustParseAddr("100.64.0.30")}},
		"v6only": {ID: "nSix1CNTRL", DNSName: "phone.tail1234.ts.net.", OS: "android", UserID: 2, Online: true,
			TailscaleIPs: []netip.Addr{netip.MustParseAddr("fd7a:115c:a1e0::40")}},
		"nodns": {ID: "nBare1CNTRL", HostName: "Laptop", OS: "windows", UserID: 1},
		// The node itself, as a status may list it.
		"self": self,
		"gone": nil,
	}
	b, err := json.Marshal(peerList(self, peers))
	if err != nil {
		t.Fatal(err)
	}
	want := `[` +
		`{"name":"box","dns":"box.tail1234.ts.net","address":"100.64.0.30","id":"nTagged1CNTRL","online":false,"mine":false,"tagged":true,"os":"linux"},` +
		`{"name":"laptop","dns":"","address":"","id":"nBare1CNTRL","online":false,"mine":true,"tagged":false,"os":"windows"},` +
		`{"name":"messagelink-server","dns":"messagelink-server.tail1234.ts.net","address":"100.64.0.9","id":"` + serverID + `","online":true,"mine":true,"tagged":false,"os":"linux"},` +
		`{"name":"phone","dns":"phone.tail1234.ts.net","address":"fd7a:115c:a1e0::40","id":"nSix1CNTRL","online":true,"mine":false,"tagged":false,"os":"android"}` +
		`]`
	if string(b) != want {
		t.Errorf("got  %s\nwant %s", b, want)
	}
	if b, _ := json.Marshal(peerList[string](self, nil)); string(b) != "[]" {
		t.Errorf("no devices read %s", b)
	}
	if got := peerList[tailcfg.StableNodeID](nil, nil); got == nil {
		t.Error("no devices gave nil")
	}
}
