package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/useragent"
)

// A download through the session carries every cookie and x-fb-lsd beside them, so it is made only to Meta's own
// hosts, and a redirect keeps the session only as far as the same hosts: Go drops the cookie on a redirect to another
// domain but forwards every other header. Anything else Facebook names — a GIF on Giphy, say, or whatever a backup
// being restored says — is fetched anonymously instead, as upstream fetches all media: no cookie, no session header,
// and only to a public address.
var mediaHosts = []string{"fbcdn.net", "fbsbx.com", "cdninstagram.com", "facebook.com", "instagram.com", "messenger.com", "whatsapp.net"}

// Content-free on purpose: nothing about the address or the response is in them.
var (
	errMediaRefused  = errors.New("media address refused")
	errMediaTooLarge = errors.New("media is larger than the limit")
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

// An address fit for the anonymous fetch: https, and a host the link-preview fetcher would call public.
func anonymousMediaURL(address *url.URL) bool {
	if address == nil || !strings.EqualFold(address.Scheme, "https") {
		return false
	}
	_, err := publicPreviewURL(address.String())
	return err == nil
}

// The anonymous fetch: no jar, no proxy, the link-preview fetcher's dial guard on the address actually dialled
// (previewPublicDial), and at most five redirects, each held to the same rules.
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

var anonymousMedia = newAnonymousMedia(newPreviewTransport())

// Why a download was refused rather than failed, as a fixed word for the log, or "" when it was not.
func mediaRefusal(err error) string {
	switch {
	case errors.Is(err, errMediaRefused), errors.Is(err, errPreviewDestination):
		return "address"
	case errors.Is(err, errMediaTooLarge):
		return "size"
	}
	return ""
}

// One download, held to mediaLimit before anything is buffered: a declared length above it is refused unread, and a
// body is read no further than one byte past it. Upstream's request loop read the whole body first and compared
// afterwards. A Meta address goes through the session, with upstream's retries kept as they were: a connection that
// failed or a 5xx is asked again after the same backoff. Any other https address, or a Meta address redirecting off
// Meta, is fetched once through anonymous; anonymous says which happened. Plain http is refused.
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
		if err == nil || attempt > httpclient.MaxHTTPRetries || ctx.Err() != nil || mediaRefusal(err) != "" ||
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

// What the encrypted connection may take in one response: the largest file kept here, AES-CBC's padding of up to 16
// bytes, and the 10-byte MAC whatsmeow strips after reading.
const secureDownloadLimit = mediaLimit + 16 + 10

// whatsmeow reads an encrypted download whole before it checks anything, so the bound is put on the wire beneath it: a
// declared length above the limit fails before the body is read, and a body that runs past it fails as it does. Only
// GETs are touched; uploads share the client and pass as they were.
type boundedDownloads struct{ next http.RoundTripper }

func (b boundedDownloads) RoundTrip(r *http.Request) (*http.Response, error) {
	resp, err := b.next.RoundTrip(r)
	if err != nil || r.Method != http.MethodGet {
		return resp, err
	}
	if resp.ContentLength > secureDownloadLimit {
		_ = resp.Body.Close()
		// Failed through the body, not as a transport error: whatsmeow takes those for a network fault and asks again.
		resp.Body, resp.ContentLength = refusedBody{}, -1
		return resp, nil
	}
	resp.Body = &cappedBody{body: resp.Body, left: secureDownloadLimit}
	return resp, nil
}

// The media client whatsmeow is given: what it makes by default, with the bound above.
func secureMediaHTTP() *http.Client {
	return &http.Client{Transport: boundedDownloads{next: http.DefaultTransport.(*http.Transport).Clone()}}
}

type refusedBody struct{}

func (refusedBody) Read([]byte) (int, error) { return 0, errMediaTooLarge }
func (refusedBody) Close() error             { return nil }

type cappedBody struct {
	body io.ReadCloser
	left int64
}

func (c *cappedBody) Read(p []byte) (int, error) {
	if c.left < 0 {
		return 0, errMediaTooLarge
	}
	// One byte past the limit is read, so a body that ends exactly at it is told from one that runs on.
	if int64(len(p)) > c.left+1 {
		p = p[:c.left+1]
	}
	n, err := c.body.Read(p)
	c.left -= int64(n)
	if c.left < 0 {
		return n, errMediaTooLarge
	}
	return n, err
}

func (c *cappedBody) Close() error { return c.body.Close() }
