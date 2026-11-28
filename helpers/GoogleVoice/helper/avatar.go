package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
)

// Contact photos returned by Google Voice's contacts endpoint are hosted on Google's profile-photo CDN. Keep this
// narrow even though the URL never leaves the helper: a malformed contact record must not turn avatar rendering into
// a general-purpose downloader. The 4 MiB ceiling matches the app's avatar decoder/cache boundary.
const avatarLimit = 4 * 1024 * 1024

func allowedAvatarURL(address string) (*url.URL, bool) {
	parsed, err := url.Parse(address)
	if err != nil || parsed.Scheme != "https" || parsed.User != nil || parsed.Port() != "" || parsed.Path == "" {
		return nil, false
	}
	host := strings.ToLower(parsed.Hostname())
	if host != "googleusercontent.com" && !strings.HasSuffix(host, ".googleusercontent.com") {
		return nil, false
	}
	return parsed, true
}

func downloadContactAvatar(ctx context.Context, client *libgv.Client, address string) ([]byte, string, error) {
	parsed, ok := allowedAvatarURL(address)
	if !ok || client == nil || client.HTTP == nil {
		return nil, "", errors.New("unavailable avatar")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, parsed.String(), nil)
	if err != nil {
		return nil, "", err
	}
	req.Header.Set("User-Agent", libgv.UserAgent)
	req.Header.Set("Referer", libgv.Origin+"/")
	req.Header.Set("Accept", "image/webp,image/png,image/*;q=0.8,*/*;q=0.5")
	// Do not follow a photo URL to another origin. A same-CDN redirect is permitted, matching the browser while
	// keeping the host boundary above effective for every request.
	httpClient := *client.HTTP
	httpClient.CheckRedirect = func(next *http.Request, _ []*http.Request) error {
		if _, permitted := allowedAvatarURL(next.URL.String()); !permitted {
			return http.ErrUseLastResponse
		}
		return nil
	}
	resp, err := httpClient.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer resp.Body.Close()
	// Google normally supplies a length, but chunked photo responses are valid too; LimitReader remains the authority.
	if resp.StatusCode != http.StatusOK || resp.ContentLength > avatarLimit {
		return nil, "", fmt.Errorf("avatar status or size")
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, avatarLimit+1))
	if err != nil || len(data) == 0 || len(data) > avatarLimit {
		return nil, "", errors.New("invalid avatar")
	}
	mime := strings.ToLower(strings.TrimSpace(strings.Split(resp.Header.Get("Content-Type"), ";")[0]))
	if mime == "" || mime == "application/octet-stream" {
		mime = http.DetectContentType(data)
	}
	if !strings.HasPrefix(mime, "image/") {
		return nil, "", errors.New("non-image avatar")
	}
	return data, mime, nil
}
