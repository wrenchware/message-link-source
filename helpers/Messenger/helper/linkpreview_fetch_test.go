package main

import (
	"bytes"
	"context"
	"errors"
	"image"
	"image/color"
	"image/png"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strings"
	"testing"
)

type previewTestTransport func(*http.Request) (*http.Response, error)

func (f previewTestTransport) RoundTrip(req *http.Request) (*http.Response, error) { return f(req) }

func previewTestResponse(req *http.Request, status int, mime, body string) *http.Response {
	return &http.Response{
		StatusCode: status, Header: http.Header{"Content-Type": {mime}},
		Body: io.NopCloser(strings.NewReader(body)), Request: req,
	}
}

func TestLinkPreviewReadsOpenGraphAndResolvesAtFinalURL(t *testing.T) {
	var calls int
	fetcher := newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
		calls++
		for _, header := range []string{"Cookie", "Authorization", "Proxy-Authorization", "Referer"} {
			if req.Header.Get(header) != "" {
				t.Fatalf("unexpected credential or referrer header: %s", header)
			}
		}
		if calls == 1 {
			if req.URL.Fragment != "" {
				t.Fatal("fragment must not be sent")
			}
			response := previewTestResponse(req, http.StatusFound, "text/html", "")
			response.Header.Set("Location", "https://public.example/final/page")
			response.Header.Set("Set-Cookie", "synthetic_cookie=should_not_be_sent")
			return response, nil
		}
		return previewTestResponse(req, 200, "text/html; charset=UTF-8", `<html><head>
		<meta property="og:title" content="A &amp; B">
		<meta property="og:image" content="old.jpg">
		<meta property="og:image:secure_url" content="../cover.jpg?x=1&amp;y=2">
		<meta property="og:image:width" content="320">
		<meta property="og:image:height" content="480">
		<meta name="twitter:image" content="/ignored.jpg">
		</head><body><meta property="og:image" content="/body.jpg"></body></html>`), nil
	}))
	preview, err := fetcher.link(context.Background(), "https://public.example/start?synthetic_secret=1#local")
	if err != nil {
		t.Fatal(err)
	}
	if calls != 2 || preview.title != "A & B" || preview.imageURL != "https://public.example/cover.jpg?x=1&y=2" || preview.width != 320 || preview.height != 480 {
		t.Fatalf("unexpected synthetic metadata: %#v calls=%d", preview, calls)
	}
}

func TestLinkPreviewOpenGraphCandidatesAndTwitterFallback(t *testing.T) {
	base, _ := url.Parse("https://public.example/directory/page")
	tests := []struct {
		name, html, want string
		width, height    int64
	}{
		{"twitter", `<meta name="twitter:image" content="//images.example/card.png">`, "https://images.example/card.png", 0, 0},
		{"public fallback", `<meta property="og:image" content="http://127.0.0.1/a"><meta property="og:image:width" content="99"><meta property="og:image" content="/cover.jpg"><meta property="og:image:height" content="200">`, "https://public.example/cover.jpg", 0, 200},
		{"first image wins", `<meta property="og:image" content="/first.jpg"><meta property="og:image:width" content="300"><meta property="og:image" content="/second.jpg"><meta property="og:image:width" content="600">`, "https://public.example/first.jpg", 300, 0},
		{"invalid secure fallback", `<meta property="og:image" content="/cover.jpg"><meta property="og:image:secure_url" content="https://user:pass@images.example/private">`, "https://public.example/cover.jpg", 0, 0},
		{"invalid dimensions", `<meta PROPERTY="OG:IMAGE" CONTENT="/cover.jpg"><meta property="og:image:width" content="99999999"><meta property="og:image:height" content="-1">`, "https://public.example/cover.jpg", 0, 0},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			result, err := parseLinkPreview([]byte(test.html), base)
			if err != nil || result.imageURL != test.want || result.width != test.width || result.height != test.height {
				t.Fatalf("unexpected synthetic result: %#v, %v", result, err)
			}
		})
	}
	for _, body := range []string{
		`<title>Ordinary text</title><link rel="icon" href="/avatar.png">`,
		`<meta property="og:image" content="file:///private.png">`,
		`<meta property="og:image" content="http://169.254.169.254/private">`,
		`<body><meta property="og:image" content="https://public.example/body.jpg">`,
	} {
		if _, err := parseLinkPreview([]byte(body), base); !errors.Is(err, errPreviewMissing) {
			t.Fatalf("no usable metadata should produce missing preview: %v", err)
		}
	}
}

func TestLinkPreviewRejectsNonpublicURLsBeforeTransport(t *testing.T) {
	fetcher := newPreviewFetcher(previewTestTransport(func(*http.Request) (*http.Response, error) {
		t.Fatal("blocked destination reached transport")
		return nil, nil
	}))
	for _, value := range []string{
		"file:///etc/hosts", "data:image/png;base64,eA==", "javascript:alert(1)",
		"https://user:password@public.example/a", "https://localhost/a", "https://localhost./a",
		"https://machine/a", "http://thing.local/a", "http://thing.localhost/a", "https://thing.internal/a",
		"http://127.0.0.1/a", "http://10.0.0.1/a", "http://169.254.169.254/a", "http://100.64.0.1/a",
		"http://192.168.1.2/a", "http://172.16.0.1/a", "http://0.0.0.0/a", "http://224.0.0.1/a",
		"http://192.0.2.1/a", "http://198.18.0.1/a", "http://240.0.0.1/a", "http://[::1]/a",
		"http://[::ffff:127.0.0.1]/a", "http://[fc00::1]/a", "http://[fe80::1%25zone]/a",
		"http://[2001:db8::1]/a", "http://[2002:7f00:1::]/a", "https://public.example:0/a",
		"https://public.example:65536/a", "https://public.example:bad/a",
		"https://public.example:8443/a", "http://public.example:22/a", "https://public.example:80/a", "http://public.example:443/a",
		"https://public.example/" + strings.Repeat("x", previewURLLimit),
	} {
		if _, err := fetcher.link(context.Background(), value); !errors.Is(err, errPreviewDestination) {
			t.Errorf("expected destination rejection for synthetic URL %q, got %v", value, err)
		}
	}
}

func TestLinkPreviewRedirectsCannotReachPrivateOrCredentialledDestinations(t *testing.T) {
	for _, destination := range []string{"http://127.0.0.1/", "http://[::1]/", "https://user:pass@public.example/", "file:///private"} {
		calls := 0
		fetcher := newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
			calls++
			response := previewTestResponse(req, http.StatusFound, "text/html", "")
			response.Header.Set("Location", destination)
			return response, nil
		}))
		if _, err := fetcher.link(context.Background(), "https://public.example/"); err == nil || calls != 1 {
			t.Fatalf("redirect was followed: calls=%d, err=%v", calls, err)
		}
	}
	calls := 0
	fetcher := newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
		calls++
		response := previewTestResponse(req, http.StatusFound, "text/html", "")
		response.Header.Set("Location", "/again")
		return response, nil
	}))
	if _, err := fetcher.link(context.Background(), "https://public.example/"); err == nil || calls != 6 {
		t.Fatalf("redirect cap should allow five hops: calls=%d err=%v", calls, err)
	}
}

func TestLinkPreviewDialPinsCheckedDNSResult(t *testing.T) {
	lookupCalls, dialCalls := 0, 0
	lookup := func(ctx context.Context, network, host string) ([]netip.Addr, error) {
		lookupCalls++
		if host != "public.example" || network != "ip" {
			t.Fatal("unexpected resolver input")
		}
		return []netip.Addr{netip.MustParseAddr("8.8.8.8")}, nil
	}
	dial := func(ctx context.Context, network, address string) (net.Conn, error) {
		dialCalls++
		if address != "8.8.8.8:443" {
			t.Fatal("transport must dial checked IP, never resolve hostname again")
		}
		return nil, errors.New("offline synthetic connection failure")
	}
	_, err := previewPublicDial(lookup, noHostNetworks, dial)(context.Background(), "tcp", "public.example:443")
	if !errors.Is(err, errPreviewRequest) || lookupCalls != 1 || dialCalls != 1 {
		t.Fatalf("DNS check/dial mismatch: %v %d %d", err, lookupCalls, dialCalls)
	}
	for _, ips := range [][]netip.Addr{
		{netip.MustParseAddr("127.0.0.1")},
		{netip.MustParseAddr("8.8.8.8"), netip.MustParseAddr("10.1.1.1")},
		{netip.MustParseAddr("::ffff:192.168.1.1")},
	} {
		lookup := func(context.Context, string, string) ([]netip.Addr, error) { return ips, nil }
		blockedDial := func(context.Context, string, string) (net.Conn, error) {
			t.Fatal("nonpublic DNS result reached dial")
			return nil, nil
		}
		if _, err := previewPublicDial(lookup, noHostNetworks, blockedDial)(context.Background(), "tcp", "public.example:443"); !errors.Is(err, errPreviewDestination) {
			t.Fatal(err)
		}
	}
	if !publicPreviewIP(netip.MustParseAddr("2606:4700:4700::1111")) || !publicPreviewIP(netip.MustParseAddr("8.8.8.8")) {
		t.Fatal("public IPv4 and IPv6 must be accepted")
	}
	transport := newPreviewTransport()
	defer transport.CloseIdleConnections()
	if transport.Proxy != nil || newPreviewFetcher(transport).client.Jar != nil {
		t.Fatal("preview transport must not use ambient proxy credentials or cookies")
	}
	canceled, cancel := context.WithCancel(context.Background())
	cancel()
	beforeLookups, beforeDials := lookupCalls, dialCalls
	if _, err := previewPublicDial(lookup, noHostNetworks, dial)(canceled, "tcp", "public.example:443"); !errors.Is(err, errPreviewRequest) || lookupCalls != beforeLookups || dialCalls != beforeDials {
		t.Fatal("canceled work must not start DNS or dial")
	}
}

func TestLinkPreviewBoundsPageResponseAndSanitizesErrors(t *testing.T) {
	for _, test := range []struct {
		name, body, mime string
		status           int
		length           int64
	}{
		{"streamed large body", strings.Repeat("x", previewHTMLLimit+1), "text/html", 200, -1},
		{"declared large body", "", "text/html", 200, previewHTMLLimit + 1},
		{"unsupported content", "synthetic binary", "application/pdf", 200, -1},
		{"status failure", "", "text/html", 403, -1},
	} {
		t.Run(test.name, func(t *testing.T) {
			fetcher := newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
				response := previewTestResponse(req, test.status, test.mime, test.body)
				response.ContentLength = test.length
				return response, nil
			}))
			if _, err := fetcher.link(context.Background(), "https://public.example/"); !errors.Is(err, errPreviewResponse) {
				t.Fatal(err)
			}
		})
	}
	fetcher := newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
		return nil, errors.New("synthetic-secret: " + req.URL.String())
	}))
	_, err := fetcher.link(context.Background(), "https://public.example/private?synthetic_secret=1")
	if err == nil || strings.Contains(err.Error(), "synthetic") || strings.Contains(err.Error(), "public.example") {
		t.Fatalf("fetch errors must omit all URLs and transport details: %v", err)
	}
}

func TestLinkPreviewPictureValidatesBytesAndCapsDimensions(t *testing.T) {
	img := image.NewRGBA(image.Rect(0, 0, 2, 3))
	img.Set(0, 0, color.RGBA{R: 255, A: 255})
	var encoded bytes.Buffer
	if err := png.Encode(&encoded, img); err != nil {
		t.Fatal(err)
	}
	fetcher := newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
		// MIME comes from the image bytes, not a possibly incorrect server header.
		return previewTestResponse(req, 200, "application/octet-stream", encoded.String()), nil
	}))
	data, mime, err := fetcher.picture(context.Background(), "https://images.example/cover")
	if err != nil || mime != "image/png" || !bytes.Equal(data, encoded.Bytes()) {
		t.Fatalf("valid picture failed: mime=%s err=%v", mime, err)
	}
	for _, body := range []string{
		`<svg xmlns="http://www.w3.org/2000/svg"></svg>`,
		`<html>synthetic sign-in response</html>`,
		"\x89PNG\r\n\x1a\n", // Truncated signature is not a valid picture header.
		strings.Repeat("x", previewImageLimit+1),
		// GIF header advertises 65535 x 65535 pixels, before any decoding allocation.
		"GIF89a\xff\xff\xff\xff\x00\x00\x00",
	} {
		fetcher := newPreviewFetcher(previewTestTransport(func(req *http.Request) (*http.Response, error) {
			return previewTestResponse(req, 200, "image/png", body), nil
		}))
		if _, _, err := fetcher.picture(context.Background(), "https://images.example/cover"); !errors.Is(err, errPreviewResponse) {
			t.Fatalf("invalid image body was accepted: %v", err)
		}
	}
}

// The card shows what a page says about itself: Open Graph first, then Twitter's tag, then the plain meta description.
func TestLinkPreviewReadsTheDescription(t *testing.T) {
	base, _ := url.Parse("https://example.com/")
	for body, want := range map[string]string{
		`<meta property="og:description" content="From Open Graph"><meta name="description" content="Plain"><meta property="og:image" content="https://example.com/a.jpg">`: "From Open Graph",
		`<meta name="twitter:description" content="From Twitter"><meta name="description" content="Plain"><meta property="og:image" content="https://example.com/a.jpg">`:     "From Twitter",
		`<meta name="description" content="  Plain   words  "><meta property="og:image" content="https://example.com/a.jpg">`:                                            "Plain words",
	} {
		result, err := parseLinkPreview([]byte("<html><head>"+body+"</head></html>"), base)
		if err != nil || result.description != want {
			t.Fatalf("description came out as %+v (%v), wanted %q", result, err, want)
		}
	}
}
