module nativebeeper.local/googlemessages-helper

go 1.27.3

require (
	github.com/google/uuid v1.6.0
	github.com/rs/zerolog v1.35.1
	github.com/skip2/go-qrcode v0.0.0-20200617195104-da1b6568686e
	go.mau.fi/mautrix-gmessages v0.2609.1-0.20261010212621-186284eea622
	go.mau.fi/util v0.10.2-0.20260918225449-a4c0d5b86aa8
	golang.org/x/image v0.47.0
	golang.org/x/net v0.62.0
	google.golang.org/protobuf v1.36.12
)

require (
	github.com/mattn/go-colorable v0.1.14 // indirect
	github.com/mattn/go-isatty v0.0.20 // indirect
	golang.org/x/crypto v0.58.0 // indirect
	golang.org/x/exp v0.0.0-20260908205506-85c1c2202aba // indirect
	golang.org/x/sys v0.49.0 // indirect
	golang.org/x/text v0.43.0 // indirect
)

replace go.mau.fi/mautrix-gmessages => ../../../third_party/mautrix-gmessages
