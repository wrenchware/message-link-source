module messagelink.local/googlemessages-helper

go 1.27.4

require (
	github.com/google/uuid v1.6.0
	github.com/rs/zerolog v1.35.1
	github.com/skip2/go-qrcode v0.0.0-20200617195104-da1b6568686e
	go.mau.fi/mautrix-gmessages v0.2611.1-0.20261126200815-6d8ba56a9462
	go.mau.fi/util v0.11.2-0.20261123232031-42f343a1bef8
	golang.org/x/image v0.48.0
	golang.org/x/net v0.62.0
	google.golang.org/protobuf v1.36.12
)

require (
	github.com/mattn/go-colorable v0.1.14 // indirect
	github.com/mattn/go-isatty v0.0.20 // indirect
	golang.org/x/crypto v0.59.0 // indirect
	golang.org/x/exp v0.0.0-20261113113407-e3970619a1a4 // indirect
	golang.org/x/sys v0.50.0 // indirect
	golang.org/x/text v0.44.0 // indirect
)

replace go.mau.fi/mautrix-gmessages => ../../../third_party/mautrix-gmessages
