//go:build tools

package tailnet

// gomobile bind needs golang.org/x/mobile/bind in the module; imported from the library itself it would link the
// binding generator (go/types and friends) into the app.
import _ "golang.org/x/mobile/bind"
