module messagelink.local/instagram-helper

go 1.27.4

require (
	github.com/coder/websocket v1.8.15
	github.com/rs/zerolog v1.35.1
	go.mau.fi/mautrix-meta v0.2611.1-0.20261126163501-16724d697cdc
	go.mau.fi/util v0.11.2-0.20261124193158-ca7820d78067
)

require (
	filippo.io/edwards25519 v1.2.0 // indirect
	github.com/andybalholm/brotli v1.2.7 // indirect
	github.com/apache/thrift v0.25.0 // indirect
	github.com/beeper/poly1305 v0.0.0-20250815183548-d4eede7bbf3c // indirect
	github.com/coreos/go-systemd/v22 v22.7.0 // indirect
	github.com/google/flatbuffers v25.12.19+incompatible // indirect
	github.com/google/go-querystring v1.2.0 // indirect
	github.com/google/uuid v1.6.0 // indirect
	github.com/icholy/digest v1.2.0 // indirect
	github.com/imroc/req/v3 v3.56.0 // indirect
	github.com/klauspost/compress v1.20.2 // indirect
	github.com/mattn/go-colorable v0.1.15 // indirect
	github.com/mattn/go-isatty v0.0.24 // indirect
	github.com/petermattis/goid v0.0.0-20261109124353-7cfa343ad0ab // indirect
	github.com/quic-go/qpack v0.6.0 // indirect
	github.com/quic-go/quic-go v0.64.1 // indirect
	github.com/refraction-networking/utls v1.8.2 // indirect
	github.com/rs/xid v1.6.0 // indirect
	github.com/tidwall/gjson v1.20.0 // indirect
	github.com/tidwall/match v1.1.1 // indirect
	github.com/tidwall/pretty v1.3.0 // indirect
	github.com/tidwall/sjson v1.2.5 // indirect
	github.com/yuin/goldmark v1.8.7 // indirect
	go.mau.fi/zeroconfig v0.2.0 // indirect
	golang.org/x/crypto v0.59.0 // indirect
	golang.org/x/exp v0.0.0-20261113113407-e3970619a1a4 // indirect
	golang.org/x/net v0.62.0 // indirect
	golang.org/x/sys v0.50.0 // indirect
	golang.org/x/text v0.44.0 // indirect
	google.golang.org/protobuf v1.36.12 // indirect
	gopkg.in/natefinch/lumberjack.v2 v2.2.1 // indirect
	gopkg.in/yaml.v3 v3.0.1 // indirect
	maunium.net/go/mautrix v0.33.1-0.20261125114131-b1b4ee3b87d9 // indirect
)

replace github.com/imroc/req/v3 => github.com/beeper/req/v3 v3.0.0-20261112020631-42bebc180e13

// Same pinned source as Messenger; Instagram uses pkg/instameow.
replace go.mau.fi/mautrix-meta => ../../../third_party/mautrix-meta
