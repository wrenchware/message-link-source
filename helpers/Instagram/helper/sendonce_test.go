package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

// Every request ends here and nothing leaves the process. A graph mutation's answer is lost the way a reset connection
// loses it; the upload a media send makes first is answered, and so is the route lookup a first message makes, with
// "no conversation yet".
type lostAnswers struct {
	mu        sync.Mutex
	mutations map[string]int
	other     int
}

func (l *lostAnswers) RoundTrip(r *http.Request) (*http.Response, error) {
	if r.Body != nil {
		_ = r.Body.Close()
	}
	answer := func(status int, body string) (*http.Response, error) {
		return &http.Response{StatusCode: status, Header: http.Header{}, Body: io.NopCloser(strings.NewReader(body)), Request: r}, nil
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	if name := r.Header.Get("x-fb-friendly-name"); name != "" {
		l.mutations[name]++
		return nil, errors.New("synthetic connection reset")
	}
	l.other++
	switch {
	case strings.HasPrefix(r.URL.Path, "/ajax/mercury/upload.php"):
		return answer(http.StatusOK, `{"payload":{"metadata":[{"image_id":42}]}}`)
	case strings.HasPrefix(r.URL.Path, "/ajax/navigation/"):
		return answer(http.StatusNotFound, "")
	}
	return nil, errors.New("unexpected synthetic request")
}

func (l *lostAnswers) attempts(name string) int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.mutations[name]
}

// Every mutation posted, whatever its name.
func (l *lostAnswers) total() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	count := 0
	for _, n := range l.mutations {
		count += n
	}
	return count
}

// The write fixture's conversations, with upstream's own client in place of the fake: the request loop under test is
// upstream's, so a fake client would prove nothing about it.
func lostAnswerFixture(t *testing.T) (*readOnlyAdapter, *lostAnswers) {
	t.Helper()
	a, read := chatFixture()
	// Marking read and unsending address the long id (writeFixture).
	for _, thread := range read.details {
		thread.ThreadID = "long-" + thread.ID
	}
	chatsOf(t, a, `{}`)
	values := fakeCookies()
	values["ds_user_id"] = "123"
	jar, code := readCookies(values)
	if code != "" {
		t.Fatal(code)
	}
	client := newAppClient(jar, nil, func() {}).(*instameow.Client)
	configs := httpclient.NewConfigs(nil)
	// The route lookup a first message makes refuses to run without one.
	configs.RoutingNamespace = "synthetic"
	state, _ := json.Marshal(struct {
		Configs   *httpclient.Configs
		Timestamp time.Time
	}{configs, time.Now()})
	if err := client.LoadState(state); err != nil {
		t.Fatal(err)
	}
	transport := &lostAnswers{mutations: map[string]int{}}
	client.GetHTTP().SetTransportOverride(transport)
	a.client, a.jar, a.canMutate = client, jar, true
	return a, transport
}

// A send or a group whose answer was lost may already exist, and upstream's request loop would post it again up to
// five more times. Each is marked to make one attempt (httpclient.SendOnce) and reports the failure for the app to
// call uncertain. The budget outlasts upstream's first three-second backoff, so an unmarked call would have been
// posted a second time within it.
func TestLostAnswersAreNeverPostedAgain(t *testing.T) {
	a, transport := lostAnswerFixture(t)
	for _, test := range []struct {
		method, mutation string
		call             func(context.Context) (any, string)
	}{
		{"ui.sendText", "IGDirectTextSendMutation", func(ctx context.Context) (any, string) {
			return a.uiSendText(ctx, json.RawMessage(`{"chatId":"direct","text":"synthetic"}`))
		}},
		{"ui.sendMedia", "IGDirectMediaSendMutation", func(ctx context.Context) (any, string) {
			if _, code := a.uiMediaStage(json.RawMessage(`{"offset":0,"data":"AAEC"}`)); code != "" {
				t.Fatalf("staging failed: %s", code)
			}
			return a.uiSendMedia(ctx, json.RawMessage(`{"chatId":"direct","name":"a.png","mime":"image/png"}`))
		}},
		{"ui.startDirectWithText", "IGDirectTextSendMutation", func(ctx context.Context) (any, string) {
			return a.uiStartDirectWithText(ctx, json.RawMessage(`{"personId":"789","text":"synthetic"}`))
		}},
		{"ui.createGroup", "useCreateOpenGroupThreadOffMsysMutation", func(ctx context.Context) (any, string) {
			return a.uiCreateGroup(ctx, json.RawMessage(`{"personIds":["456","789"]}`))
		}},
		// The changes besides: a repeated reaction, edit or unsend is mostly harmless, but a group renamed twice tells
		// every member twice, and no uncertain change is ever attempted twice.
		{"ui.react", "", func(ctx context.Context) (any, string) {
			return a.uiReact(ctx, json.RawMessage(`{"chatId":"direct","messageId":"m1","emoji":"x"}`))
		}},
		{"ui.editMessage", "", func(ctx context.Context) (any, string) {
			return a.uiEditMessage(ctx, json.RawMessage(`{"chatId":"direct","messageId":"m1","text":"synthetic"}`))
		}},
		{"ui.deleteMessage", "", func(ctx context.Context) (any, string) {
			return a.uiDeleteMessage(ctx, json.RawMessage(`{"chatId":"direct","messageId":"m1"}`))
		}},
		{"ui.markRead", "", func(ctx context.Context) (any, string) {
			return a.uiMarkRead(ctx, json.RawMessage(`{"chatId":"direct","messageId":"m1","at":1}`))
		}},
	} {
		before := transport.total()
		ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
		started := time.Now()
		_, code := test.call(ctx)
		cancel()
		if code != "connection_failed" {
			t.Fatalf("%s: a lost answer was reported as %q", test.method, code)
		}
		if got := transport.total() - before; got != 1 || (test.mutation != "" && transport.attempts(test.mutation) == 0) {
			t.Fatalf("%s: posted %d times after a lost answer", test.method, got)
		}
		if waited := time.Since(started); waited > 2*time.Second {
			t.Fatalf("%s: waited %s, as if for a retry", test.method, waited)
		}
	}
	// The upload and the route lookup were reached, so the sends above were the real ones and not refused early.
	if transport.other != 2 {
		t.Fatalf("expected one upload and one route lookup, saw %d other requests", transport.other)
	}
}

// The control: the same transport and budget, unmarked, and upstream does post again after its first backoff. Without
// it the test above could pass only because the budget had become too short for any retry.
func TestUnmarkedRequestsKeepUpstreamRetries(t *testing.T) {
	a, transport := lostAnswerFixture(t)
	session := a.client.(*instameow.Client).GetHTTP()
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	headers := session.BuildHeaders(true, false)
	headers.Set("x-fb-friendly-name", "SyntheticRead")
	_, _, _ = session.MakeRequest(ctx, "https://www.instagram.com/api/graphql", http.MethodPost, headers, nil, types.NONE)
	if got := transport.attempts("SyntheticRead"); got != 2 {
		t.Fatalf("an unmarked request was attempted %d times within the first backoff, not twice", got)
	}
}
