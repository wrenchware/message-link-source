//go:build !android

package tailnet

// SetDefaultRoute matters only on Android; elsewhere the package is built to be tested.
func SetDefaultRoute(ifName string) {}
