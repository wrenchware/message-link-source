package main

import (
	"bytes"
	"context"
	"errors"
	"image"
	_ "image/gif"
	"image/jpeg"
	"image/png"
	"io"
	"mime"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"golang.org/x/image/webp"
	"golang.org/x/net/html"
)

const (
	previewHTMLLimit  = 1 << 20
	previewImageLimit = 5 << 20
	previewURLLimit   = 8192
	previewTimeout    = 8 * time.Second
	// A WebP picture is decoded in full to be drawn again as JPEG or PNG, so it gets a tighter bound than a picture that is
	// passed on as it came: 16 million pixels is 64 MiB decoded. Pictures published for sharing are around 1200 by 630.
	previewWebPPixelLimit = 16_000_000
)

// All errors are deliberately content-free. Callers must not log net/http's errors,
// which can include the message's URL, or any metadata returned by the website.
var (
	errPreviewDestination = errors.New("preview destination is not public HTTP(S)")
	errPreviewRequest     = errors.New("preview request failed")
	errPreviewResponse    = errors.New("preview response is unsupported or too large")
	errPreviewMissing     = errors.New("preview picture is unavailable")
)

type linkPreviewResult struct {
	title string
	// What the page says about itself, for the line under the title in the card.
	description string
	imageURL    string
	width       int64
	height      int64
}

type previewFetcher struct{ client *http.Client }

var publicPreviewFetcher = newPreviewFetcher(newPreviewTransport())

func fetchLinkPreview(ctx context.Context, rawURL string) (*linkPreviewResult, error) {
	return publicPreviewFetcher.link(ctx, rawURL)
}

func fetchPreviewImage(ctx context.Context, rawURL string) ([]byte, string, error) {
	return publicPreviewFetcher.picture(ctx, rawURL)
}

// The injectable RoundTripper is an offline test seam. Production always uses the
// isolated transport below, never the authenticated Messenger client or a cookie jar.
func newPreviewFetcher(transport http.RoundTripper) *previewFetcher {
	return &previewFetcher{client: &http.Client{
		Transport: transport,
		Timeout:   previewTimeout,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) > 5 {
				return errPreviewRequest
			}
			if _, err := publicPreviewURL(req.URL.String()); err != nil {
				return err
			}
			// Do not disclose the previous URL to a redirect target, even when it
			// contains parameters. No credentials or cookies are ever supplied.
			req.Header.Del("Referer")
			req.Header.Del("Authorization")
			req.Header.Del("Proxy-Authorization")
			req.Header.Del("Cookie")
			return nil
		},
	}}
}

func newPreviewTransport() *http.Transport {
	dialer := &net.Dialer{Timeout: 4 * time.Second}
	return &http.Transport{
		// Proxy is intentionally nil, so environment proxy settings cannot
		// route a public request to a local service or attach proxy credentials.
		DialContext:            previewPublicDial(net.DefaultResolver.LookupNetIP, hostNetworks(readHostNetworks, time.Now), dialer.DialContext),
		TLSHandshakeTimeout:    4 * time.Second,
		ResponseHeaderTimeout:  5 * time.Second,
		MaxResponseHeaderBytes: 64 << 10,
		MaxIdleConns:           4,
		MaxIdleConnsPerHost:    2,
		IdleConnTimeout:        30 * time.Second,
	}
}

type previewLookup func(context.Context, string, string) ([]netip.Addr, error)
type previewDial func(context.Context, string, string) (net.Conn, error)

// The networks this computer's own interfaces are on (hostNetworks).
type previewLocalNetworks func() []netip.Prefix

func previewPublicDial(lookup previewLookup, local previewLocalNetworks, dial previewDial) previewDial {
	return func(ctx context.Context, network, address string) (net.Conn, error) {
		ctx, cancel := context.WithTimeout(ctx, 4*time.Second)
		defer cancel()
		if ctx.Err() != nil {
			return nil, errPreviewRequest
		}
		host, port, err := net.SplitHostPort(address)
		if err != nil || !publicPreviewHost(host) {
			return nil, errPreviewDestination
		}
		var addresses []netip.Addr
		if ip, err := netip.ParseAddr(host); err == nil {
			addresses = []netip.Addr{ip}
		} else {
			addresses, err = lookup(ctx, "ip", host)
			if err != nil || len(addresses) == 0 {
				return nil, errPreviewRequest
			}
		}
		// Reject mixed public/private DNS answers too, then dial a checked IP
		// directly. The transport retains the URL hostname for Host and TLS.
		// A global IPv6 address is public by its range and can still be a
		// neighbour on this computer's own network (the router, a printer), so
		// anything on a network of this computer's is refused as well.
		networks := local()
		for _, ip := range addresses {
			if !publicPreviewIP(ip) || onHostNetwork(ip, networks) {
				return nil, errPreviewDestination
			}
		}
		for _, ip := range addresses {
			conn, err := dial(ctx, network, net.JoinHostPort(ip.String(), port))
			if err == nil {
				return conn, nil
			}
			if ctx.Err() != nil {
				break
			}
		}
		return nil, errPreviewRequest
	}
}

var previewExcludedNetworks = []netip.Prefix{
	netip.MustParsePrefix("0.0.0.0/8"),
	netip.MustParsePrefix("100.64.0.0/10"),
	netip.MustParsePrefix("192.0.0.0/24"),
	netip.MustParsePrefix("192.0.2.0/24"),
	netip.MustParsePrefix("192.88.99.0/24"),
	netip.MustParsePrefix("198.18.0.0/15"),
	netip.MustParsePrefix("198.51.100.0/24"),
	netip.MustParsePrefix("203.0.113.0/24"),
	netip.MustParsePrefix("240.0.0.0/4"),
	netip.MustParsePrefix("2001::/23"), // Special-purpose/transition addresses.
	netip.MustParsePrefix("2001:db8::/32"),
	netip.MustParsePrefix("2002::/16"), // 6to4 can embed an otherwise blocked IPv4 address.
	netip.MustParsePrefix("3fff::/20"),
}

func publicPreviewIP(ip netip.Addr) bool {
	ip = ip.Unmap()
	if !ip.IsValid() || ip.Zone() != "" || !ip.IsGlobalUnicast() || ip.IsPrivate() || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
		return false
	}
	if ip.Is6() && !netip.MustParsePrefix("2000::/3").Contains(ip) {
		return false
	}
	for _, prefix := range previewExcludedNetworks {
		if prefix.Contains(ip) {
			return false
		}
	}
	return true
}

func onHostNetwork(ip netip.Addr, networks []netip.Prefix) bool {
	ip = ip.Unmap()
	for _, network := range networks {
		if network.Contains(ip) {
			return true
		}
	}
	return false
}

// The networks of this computer's interface addresses, and for a global IPv6 address also the /64 it sits in: an
// address given as /128 (DHCPv6, a temporary address) still has its neighbours in that /64.
func hostNetworkPrefixes(addresses []net.Addr) []netip.Prefix {
	prefixes := []netip.Prefix{}
	for _, address := range addresses {
		network, ok := address.(*net.IPNet)
		if !ok {
			continue
		}
		ip, ok := netip.AddrFromSlice(network.IP)
		if !ok {
			continue
		}
		ones, bits := network.Mask.Size()
		if ip.Is4In6() {
			ip = ip.Unmap()
			if bits == 128 {
				ones -= 96
			}
		}
		// A length of zero (or a mask that is not one) says nothing about which addresses are neighbours, and taken as a
		// network it would refuse every address there is.
		if bits == 0 || ones <= 0 {
			continue
		}
		prefix, err := ip.Prefix(ones)
		if err != nil {
			continue
		}
		prefixes = append(prefixes, prefix)
		if ip.Is6() && ip.IsGlobalUnicast() && ones > 64 {
			if wide, err := ip.Prefix(64); err == nil {
				prefixes = append(prefixes, wide)
			}
		}
	}
	return prefixes
}

// What the interfaces say now. If they cannot be read, the other checks still stand.
func readHostNetworks() []netip.Prefix {
	addresses, err := net.InterfaceAddrs()
	if err != nil {
		return nil
	}
	return hostNetworkPrefixes(addresses)
}

// This computer's networks, read at most once a minute: a preview is fetched for each link shown, and the addresses
// change rarely (a new network, a renewed temporary IPv6 address).
func hostNetworks(read func() []netip.Prefix, now func() time.Time) previewLocalNetworks {
	var mu sync.Mutex
	var held []netip.Prefix
	var readAt time.Time
	return func() []netip.Prefix {
		mu.Lock()
		defer mu.Unlock()
		if at := now(); readAt.IsZero() || at.Sub(readAt) >= time.Minute || at.Before(readAt) {
			held, readAt = read(), at
		}
		return held
	}
}

func publicPreviewHost(host string) bool {
	host = strings.ToLower(strings.TrimSuffix(host, "."))
	if ip, err := netip.ParseAddr(host); err == nil {
		return publicPreviewIP(ip)
	}
	// Single-label names and these local suffixes must not enter the platform
	// resolver, where search domains and mDNS can give them private meaning.
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

func publicPreviewURL(rawURL string) (*url.URL, error) {
	if len(rawURL) > previewURLLimit {
		return nil, errPreviewDestination
	}
	u, err := url.Parse(strings.TrimSpace(rawURL))
	if err != nil || u.User != nil || u.Opaque != "" || (u.Scheme != "http" && u.Scheme != "https") || !publicPreviewHost(u.Hostname()) {
		return nil, errPreviewDestination
	}
	// Only the scheme's own port: a link someone sent can't make the server knock on another port of a public host (a
	// mail server, an admin page), and a redirect is held to the same rule.
	if port := u.Port(); port != "" && !(u.Scheme == "https" && port == "443") && !(u.Scheme == "http" && port == "80") {
		return nil, errPreviewDestination
	}
	u.Fragment = ""
	return u, nil
}

// A page longer than limit is cut at limit when prefix is set, and refused otherwise. The tags a preview reads sit in the
// head, and some pages are far longer than any head: a YouTube watch page is about 1.4 MB, with its og:image near 770 KB
// (measured 2026-10-08), so refusing it whole lost every YouTube card. A picture is never cut.
func (f *previewFetcher) get(ctx context.Context, rawURL, accept string, limit int64, prefix bool) ([]byte, *http.Response, error) {
	u, err := publicPreviewURL(rawURL)
	if err != nil {
		return nil, nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return nil, nil, errPreviewRequest
	}
	req.Header.Set("User-Agent", "Messages-LinkPreview/1.0")
	req.Header.Set("Accept", accept)
	resp, err := f.client.Do(req)
	if err != nil {
		return nil, nil, errPreviewRequest
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 || (!prefix && resp.ContentLength > limit) {
		return nil, nil, errPreviewResponse
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, limit+1))
	if err != nil {
		return nil, nil, errPreviewResponse
	}
	if int64(len(body)) > limit {
		if !prefix {
			return nil, nil, errPreviewResponse
		}
		body = body[:limit]
	}
	return body, resp, nil
}

func (f *previewFetcher) link(ctx context.Context, rawURL string) (*linkPreviewResult, error) {
	body, resp, err := f.get(ctx, rawURL, "text/html, application/xhtml+xml", previewHTMLLimit, true)
	if err != nil {
		return nil, err
	}
	contentType := resp.Header.Get("Content-Type")
	if contentType == "" {
		contentType = http.DetectContentType(body)
	}
	mediaType, _, err := mime.ParseMediaType(contentType)
	if err != nil || (mediaType != "text/html" && mediaType != "application/xhtml+xml") {
		return nil, errPreviewResponse
	}
	return parseLinkPreview(body, resp.Request.URL)
}

func (f *previewFetcher) picture(ctx context.Context, rawURL string) ([]byte, string, error) {
	// WebP last: a site that has the same picture as JPEG sends that, and only a WebP-only one needs converting.
	body, _, err := f.get(ctx, rawURL, "image/jpeg, image/png, image/gif, image/webp;q=0.8", previewImageLimit, false)
	if err != nil {
		return nil, "", err
	}
	config, format, err := image.DecodeConfig(bytes.NewReader(body))
	if err != nil || config.Width < 1 || config.Height < 1 || config.Width > 16384 || config.Height > 16384 || int64(config.Width)*int64(config.Height) > 40_000_000 {
		return nil, "", errPreviewResponse
	}
	switch format {
	case "jpeg", "png", "gif":
		return body, "image/" + format, nil
	case "webp":
		return webpAsJPEGOrPNG(body, config)
	default:
		return nil, "", errPreviewResponse
	}
}

type previewImageCandidate struct {
	url, secureURL string
	width, height  int64
}

// Open Graph's structured properties belong to the preceding og:image. Keep
// candidates distinct so dimensions and secure URLs cannot leak between images.
func parseLinkPreview(body []byte, base *url.URL) (*linkPreviewResult, error) {
	tokenizer := html.NewTokenizer(bytes.NewReader(body))
	var pictures []previewImageCandidate
	currentImage := -1
	var title, twitterTitle, twitterImage string
	var description, twitterDescription, metaDescription string
	for {
		kind := tokenizer.Next()
		if kind == html.ErrorToken {
			if tokenizer.Err() != io.EOF {
				return nil, errPreviewResponse
			}
			break
		}
		if kind != html.StartTagToken && kind != html.SelfClosingTagToken && kind != html.EndTagToken {
			continue
		}
		token := tokenizer.Token()
		if (kind == html.EndTagToken && token.Data == "head") || token.Data == "body" {
			break
		}
		if kind == html.EndTagToken || token.Data != "meta" {
			continue
		}
		var property, name, content string
		for _, attr := range token.Attr {
			switch attr.Key {
			case "property":
				property = strings.ToLower(strings.TrimSpace(attr.Val))
			case "name":
				name = strings.ToLower(strings.TrimSpace(attr.Val))
			case "content":
				content = strings.TrimSpace(attr.Val)
			}
		}
		if property == "" {
			property = name
		}
		switch property {
		case "og:title":
			if title == "" {
				title = previewTitle(content)
			}
		case "twitter:title":
			if twitterTitle == "" {
				twitterTitle = previewTitle(content)
			}
		case "og:description":
			if description == "" {
				description = previewTitle(content)
			}
		case "twitter:description":
			if twitterDescription == "" {
				twitterDescription = previewTitle(content)
			}
		case "description":
			if metaDescription == "" {
				metaDescription = previewTitle(content)
			}
		case "twitter:image", "twitter:image:src":
			if twitterImage == "" {
				twitterImage = content
			}
		case "og:image", "og:image:url":
			currentImage = -1
			if len(pictures) < 32 {
				pictures = append(pictures, previewImageCandidate{url: content})
				currentImage = len(pictures) - 1
			}
		case "og:image:secure_url", "og:image:width", "og:image:height":
			if currentImage < 0 {
				continue
			}
			last := &pictures[currentImage]
			switch property {
			case "og:image:secure_url":
				last.secureURL = content
			case "og:image:width":
				last.width = previewDimension(content)
			case "og:image:height":
				last.height = previewDimension(content)
			}
		}
	}
	if title == "" {
		title = twitterTitle
	}
	for _, fallback := range []string{twitterDescription, metaDescription} {
		if description == "" {
			description = fallback
		}
	}
	pictures = append(pictures, previewImageCandidate{url: twitterImage})
	for _, picture := range pictures {
		for _, candidate := range []string{picture.secureURL, picture.url} {
			if candidate == "" {
				continue
			}
			u, err := url.Parse(candidate)
			if err != nil {
				continue
			}
			u, err = publicPreviewURL(base.ResolveReference(u).String())
			if err == nil {
				return &linkPreviewResult{title: title, description: description, imageURL: u.String(), width: picture.width, height: picture.height}, nil
			}
		}
	}
	return nil, errPreviewMissing
}

func previewTitle(value string) string {
	runes := []rune(strings.Join(strings.Fields(value), " "))
	if len(runes) > 500 {
		runes = runes[:500]
	}
	return string(runes)
}

func previewDimension(value string) int64 {
	n, err := strconv.ParseInt(value, 10, 64)
	if err != nil || n < 1 || n > 16384 {
		return 0
	}
	return n
}

// Many sites now publish their sharing picture only as WebP (a kayak-tour page shared in a Google conversation, 2026-09-25; this fetcher is the same file in both helpers),
// which this fetcher refused, so its card lost its picture and the app said Google no longer stored it. The app is handed
// JPEG, PNG or GIF whatever codecs Windows has, so a WebP is decoded here and drawn again: JPEG when it is opaque, PNG
// when it has transparency, which JPEG would turn black. An animated WebP does not decode and stays refused, as before.
func webpAsJPEGOrPNG(body []byte, config image.Config) ([]byte, string, error) {
	if int64(config.Width)*int64(config.Height) > previewWebPPixelLimit {
		return nil, "", errPreviewResponse
	}
	picture, err := webp.Decode(bytes.NewReader(body))
	if err != nil {
		return nil, "", errPreviewResponse
	}
	var out bytes.Buffer
	if opaque, ok := picture.(interface{ Opaque() bool }); ok && !opaque.Opaque() {
		if png.Encode(&out, picture) != nil || out.Len() > previewImageLimit {
			return nil, "", errPreviewResponse
		}
		return out.Bytes(), "image/png", nil
	}
	if jpeg.Encode(&out, picture, &jpeg.Options{Quality: 88}) != nil || out.Len() > previewImageLimit {
		return nil, "", errPreviewResponse
	}
	return out.Bytes(), "image/jpeg", nil
}
