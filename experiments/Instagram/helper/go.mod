module nativebeeper.local/instagram-helper

go 1.27.3

require (
	github.com/coder/websocket v1.8.15
	github.com/rs/zerolog v1.35.1
	go.mau.fi/mautrix-meta v0.2609.1-0.20261012222411-4b3889e607c0
	go.mau.fi/util v0.11.0
)

require (
	filippo.io/edwards25519 v1.2.0 // indirect
	github.com/andybalholm/brotli v1.2.4 // indirect
	github.com/apache/thrift v0.24.0 // indirect
	github.com/beeper/poly1305 v0.0.0-20250815183548-d4eede7bbf3c // indirect
	github.com/coreos/go-systemd/v22 v22.7.0 // indirect
	github.com/google/flatbuffers v25.12.19+incompatible // indirect
	github.com/google/go-querystring v1.2.0 // indirect
	github.com/google/uuid v1.6.0 // indirect
	github.com/icholy/digest v1.2.0 // indirect
	github.com/imroc/req/v3 v3.56.0 // indirect
	github.com/klauspost/compress v1.20.0 // indirect
	github.com/mattn/go-colorable v0.1.15 // indirect
	github.com/mattn/go-isatty v0.0.24 // indirect
	github.com/petermattis/goid v0.0.0-20260918085751-abfca077860b // indirect
	github.com/quic-go/qpack v0.6.0 // indirect
	github.com/quic-go/quic-go v0.61.0 // indirect
	github.com/refraction-networking/utls v1.8.2 // indirect
	github.com/rs/xid v1.6.0 // indirect
	github.com/tidwall/gjson v1.19.0 // indirect
	github.com/tidwall/match v1.1.1 // indirect
	github.com/tidwall/pretty v1.2.1 // indirect
	github.com/tidwall/sjson v1.2.5 // indirect
	github.com/yuin/goldmark v1.8.6 // indirect
	go.mau.fi/zeroconfig v0.2.0 // indirect
	golang.org/x/crypto v0.58.0 // indirect
	golang.org/x/exp v0.0.0-20261014143641-f0251eb4aa3e // indirect
	golang.org/x/net v0.61.0 // indirect
	golang.org/x/sys v0.49.0 // indirect
	golang.org/x/text v0.43.0 // indirect
	google.golang.org/protobuf v1.36.12 // indirect
	gopkg.in/natefinch/lumberjack.v2 v2.2.1 // indirect
	gopkg.in/yaml.v3 v3.0.1 // indirect
	maunium.net/go/mautrix v0.31.1-0.20261005141226-f14e6b5fc009 // indirect
)

replace github.com/imroc/req/v3 => github.com/beeper/req/v3 v3.0.0-20260925135712-b49593750e56

// Same pinned source as Messenger; Instagram uses pkg/instameow.
replace go.mau.fi/mautrix-meta => ../../../third_party/mautrix-meta
