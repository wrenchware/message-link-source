package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/useragent"
)

// A download through the session carries every cookie and x-csrftoken, x-ig-www-claim and x-fb-lsd beside them, so it
// is made only to Meta's own hosts, and a redirect keeps the session only as far as the same hosts: Go drops the
// cookie on a redirect to another domain but forwards every other header. Anything else Instagram names — a GIF on
// Giphy, say — is fetched anonymously instead, as upstream fetches all media: no cookie, no session header, and only to
// a public address. Matches the Messenger helper.
var mediaHosts = []string{"fbcdn.net", "fbsbx.com", "cdninstagram.com", "facebook.com", "instagram.com", "messenger.com", "whatsapp.net"}

// Content-free on purpose: nothing about the address or the response is in them.
var (
	errMediaRefused     = errors.New("media address refused")
	errMediaTooLarge    = errors.New("media is larger than the limit")
	errMediaDestination = errors.New("media destination is not public")
)

func metaMediaURL(address *url.URL) bool {
	if address == nil || !strings.EqualFold(address.Scheme, "https") {
		return false
	}
	host := strings.TrimSuffix(strings.ToLower(address.Hostname()), ".")
	for _, allowed := range mediaHosts {
		// On a dot boundary, so evilfbcdn.net is not fbcdn.net.
		if host == allowed || strings.HasSuffix(host, "."+allowed) {
			return true
		}
	}
	return false
}

// Whether an address is fetched through the session.
func sessionMedia(source string) bool {
	address, err := url.Parse(source)
	return err == nil && metaMediaURL(address)
}

// An address fit for the anonymous fetch: https, nobody's credentials in it, and a host that could be public.
func anonymousMediaURL(address *url.URL) bool {
	return address != nil && strings.EqualFold(address.Scheme, "https") && address.User == nil && address.Opaque == "" &&
		publicMediaHost(address.Hostname())
}

// The same networks the Messenger helper's link-preview fetcher refuses beyond Go's own private, loopback and
// link-local tests: carrier-grade NAT, documentation, benchmarking and transition ranges among them.
var excludedMediaNetworks = []netip.Prefix{
	netip.MustParsePrefix("0.0.0.0/8"),
	netip.MustParsePrefix("100.64.0.0/10"),
	netip.MustParsePrefix("192.0.0.0/24"),
	netip.MustParsePrefix("192.0.2.0/24"),
	netip.MustParsePrefix("192.88.99.0/24"),
	netip.MustParsePrefix("198.18.0.0/15"),
	netip.MustParsePrefix("198.51.100.0/24"),
	netip.MustParsePrefix("203.0.113.0/24"),
	netip.MustParsePrefix("240.0.0.0/4"),
	netip.MustParsePrefix("2001::/23"),
	netip.MustParsePrefix("2001:db8::/32"),
	netip.MustParsePrefix("2002::/16"),
	netip.MustParsePrefix("3fff::/20"),
}

var globalIPv6 = netip.MustParsePrefix("2000::/3")

// Not unspecified, multicast, private, loopback or link-local — the cloud metadata address is link-local — nor any
// of the ranges above.
func publicMediaIP(ip netip.Addr) bool {
	ip = ip.Unmap()
	if !ip.IsValid() || ip.Zone() != "" || !ip.IsGlobalUnicast() || ip.IsPrivate() || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
		return false
	}
	if ip.Is6() && !globalIPv6.Contains(ip) {
		return false
	}
	for _, prefix := range excludedMediaNetworks {
		if prefix.Contains(ip) {
			return false
		}
	}
	return true
}

func publicMediaHost(host string) bool {
	host = strings.ToLower(strings.TrimSuffix(host, "."))
	if ip, err := netip.ParseAddr(host); err == nil {
		return publicMediaIP(ip)
	}
	// Single-label names and local suffixes must not reach the resolver, where search domains and mDNS give them
	// a private meaning.
	if !strings.Contains(host, ".") || strings.ContainsAny(host, "%\\") {
		return false
	}
	for _, suffix := range []string{".localhost", ".local", ".internal", ".home", ".lan"} {
		if strings.HasSuffix(host, suffix) {
			return false
		}
	}
	return true
}

type mediaLookup func(context.Context, string, string) ([]netip.Addr, error)
type mediaDial func(context.Context, string, string) (net.Conn, error)

// Resolves the name itself, refuses the lot if any answer is not public or is on one of this computer's own networks
// (hostnetworks.go), and dials a checked address, so what is checked is what is dialled. TLS and Host still use the name.
func publicMediaDial(lookup mediaLookup, local mediaLocalNetworks, dial mediaDial) mediaDial {
	return func(ctx context.Context, network, address string) (net.Conn, error) {
		host, port, err := net.SplitHostPort(address)
		if err != nil || !publicMediaHost(host) {
			return nil, errMediaDestination
		}
		var addresses []netip.Addr
		if ip, err := netip.ParseAddr(host); err == nil {
			addresses = []netip.Addr{ip}
		} else if addresses, err = lookup(ctx, "ip", host); err != nil || len(addresses) == 0 {
			return nil, errors.New("media lookup failed")
		}
		networks := local()
		for _, ip := range addresses {
			if !publicMediaIP(ip) || onHostNetwork(ip, networks) {
				return nil, errMediaDestination
			}
		}
		var failed error
		for _, ip := range addresses {
			conn, err := dial(ctx, network, net.JoinHostPort(ip.String(), port))
			if err == nil {
				return conn, nil
			}
			failed = err
			if ctx.Err() != nil {
				break
			}
		}
		return nil, failed
	}
}

// The anonymous fetch: no jar, no proxy, the dial guard above, and at most five redirects, each held to the same
// rules.
func newAnonymousMedia(transport http.RoundTripper) *http.Client {
	return &http.Client{Transport: transport, Timeout: 60 * time.Second,
		CheckRedirect: func(next *http.Request, via []*http.Request) error {
			if len(via) > 5 {
				return httpclient.ErrTooManyRedirects
			}
			if !anonymousMediaURL(next.URL) {
				return errMediaRefused
			}
			next.Header.Del("Referer")
			return nil
		}}
}

func newAnonymousTransport() *http.Transport {
	dialer := &net.Dialer{Timeout: 10 * time.Second}
	return &http.Transport{
		// Proxy is left nil, so no environment setting can route the request elsewhere or add credentials to it.
		DialContext:            publicMediaDial(net.DefaultResolver.LookupNetIP, hostNetworks(readHostNetworks, time.Now), dialer.DialContext),
		TLSHandshakeTimeout:    10 * time.Second,
		ResponseHeaderTimeout:  20 * time.Second,
		MaxResponseHeaderBytes: 64 << 10,
		MaxIdleConns:           4,
		IdleConnTimeout:        30 * time.Second,
	}
}

var anonymousMedia = newAnonymousMedia(newAnonymousTransport())

// Refused here rather than failed there: reading the conversation again would only name the same address or file.
func refusedMedia(err error) bool {
	return errors.Is(err, errMediaRefused) || errors.Is(err, errMediaDestination) || errors.Is(err, errMediaTooLarge)
}

// One download, held to mediaLimit before anything is buffered: a declared length above it is refused unread, and a
// body is read no further than one byte past it. Upstream's request loop read the whole body first and compared
// afterwards. A Meta address goes through the session, with upstream's retries kept as they were: a connection that
// failed or a 5xx is asked again after the same backoff. Any other https address, or a Meta address redirecting off
// Meta, is fetched once through anonymous; the bool says which happened. Plain http is refused.
func fetchMedia(ctx context.Context, session *httpclient.HTTPClient, anonymous *http.Client, source string) (*http.Response, []byte, bool, error) {
	address, err := url.Parse(source)
	if err != nil {
		return nil, nil, false, errMediaRefused
	}
	if !metaMediaURL(address) {
		if !anonymousMediaURL(address) {
			return nil, nil, false, errMediaRefused
		}
		resp, body, err := fetchAnonymously(ctx, anonymous, address)
		return resp, body, true, err
	}
	client := *session.HTTP
	follow := client.CheckRedirect
	client.CheckRedirect = func(next *http.Request, via []*http.Request) error {
		if !metaMediaURL(next.URL) {
			// The session stops here; the rest is asked for anonymously, below.
			return http.ErrUseLastResponse
		}
		if follow != nil {
			return follow(next, via)
		}
		if len(via) >= 10 {
			return httpclient.ErrTooManyRedirects
		}
		return nil
	}
	for attempt := 1; ; attempt++ {
		request, err := http.NewRequestWithContext(ctx, http.MethodGet, source, nil)
		if err != nil {
			return nil, nil, false, errMediaRefused
		}
		request.Header = session.BuildHeaders(true, false)
		resp, body, err := fetchBounded(&client, request)
		if err == nil && resp.StatusCode >= http.StatusMultipleChoices && resp.Header.Get("Location") != "" {
			next, invalid := resp.Location()
			if invalid != nil || !anonymousMediaURL(next) {
				return resp, nil, false, errMediaRefused
			}
			resp, body, err = fetchAnonymously(ctx, anonymous, next)
			return resp, body, true, err
		}
		if err == nil || attempt > httpclient.MaxHTTPRetries || ctx.Err() != nil || refusedMedia(err) ||
			httpclient.IsPermanentRequestError(err) || (resp != nil && resp.StatusCode < http.StatusInternalServerError) {
			return resp, body, false, err
		}
		select {
		case <-time.After(time.Duration(attempt) * 3 * time.Second):
		case <-ctx.Done():
			return resp, nil, false, ctx.Err()
		}
	}
}

// A fresh request carrying nothing but an ordinary User-Agent.
func fetchAnonymously(ctx context.Context, client *http.Client, address *url.URL) (*http.Response, []byte, error) {
	if !anonymousMediaURL(address) {
		return nil, nil, errMediaRefused
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, address.String(), nil)
	if err != nil {
		return nil, nil, errMediaRefused
	}
	request.Header.Set("User-Agent", useragent.UserAgent)
	return fetchBounded(client, request)
}

// A redirect the client was told to stop at comes back as it is, unread, for the caller to follow.
func fetchBounded(client *http.Client, request *http.Request) (*http.Response, []byte, error) {
	resp, err := client.Do(request)
	if resp != nil {
		defer resp.Body.Close()
	}
	if err != nil {
		return resp, nil, err
	}
	if resp.StatusCode >= http.StatusBadRequest {
		return resp, nil, fmt.Errorf("%w %d", httpclient.ErrUnexpectedError, resp.StatusCode)
	}
	if resp.StatusCode >= http.StatusMultipleChoices {
		return resp, nil, nil
	}
	if resp.ContentLength > mediaLimit {
		return resp, nil, errMediaTooLarge
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, mediaLimit+1))
	if err != nil {
		return resp, nil, err
	}
	if len(body) > mediaLimit {
		return resp, nil, errMediaTooLarge
	}
	return resp, body, nil
}
