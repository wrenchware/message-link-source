package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"testing"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
	"go.mau.fi/util/pblite"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
)

func withPageToken(resp *gvproto.RespListThreads, token string) *gvproto.RespListThreads {
	resp.PaginationToken = token
	return resp
}

// The token is read from the answer's field 2 and sent back in the request's field 4, where Voice keeps it.
func TestPageTokenFieldsAreWhereVoiceKeepsThem(t *testing.T) {
	var raw []byte
	raw = protowire.AppendTag(raw, 2, protowire.BytesType)
	raw = protowire.AppendString(raw, "synthetic-token")
	var resp gvproto.RespListThreads
	if err := proto.Unmarshal(raw, &resp); err != nil || resp.GetPaginationToken() != "synthetic-token" {
		t.Fatalf("the answer's field 2 is its page token: %q %v", resp.GetPaginationToken(), err)
	}
	body, err := pblite.Marshal(&gvproto.ReqListThreads{Folder: gvproto.ThreadFolder_ALL_THREADS, PaginationToken: "tok"})
	if err != nil {
		t.Fatal(err)
	}
	var fields []any
	if json.Unmarshal(body, &fields) != nil || len(fields) < 4 || fields[3] != "tok" {
		t.Fatalf("the request's field 4 carries the token: %s", body)
	}
}

func TestConversationPagesFollowTheTokenAndStop(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	a.contacts.autocomplete = func(context.Context, *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
		return &gvproto.RespAutocompleteContacts{}, nil
	}
	a.contacts.lookup = func(context.Context, *libgv.Client, []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
		return map[string]*gvproto.RespLookupContacts_Match{}, nil
	}
	thread := func(id string, at int64) *gvproto.Thread {
		return &gvproto.Thread{ID: id, Messages: []*gvproto.Message{{ID: id + "m", Timestamp: at}}}
	}
	a.listFolders = func(_ context.Context, folder gvproto.ThreadFolder) (*gvproto.RespListThreads, error) {
		if folder == gvproto.ThreadFolder_ALL_ARCHIVED_THREADS {
			return &gvproto.RespListThreads{Threads: []*gvproto.Thread{thread("archived", 1)}}, nil
		}
		if folder == gvproto.ThreadFolder_ALL_SPAM_THREADS {
			return &gvproto.RespListThreads{}, nil
		}
		return withPageToken(&gvproto.RespListThreads{Threads: []*gvproto.Thread{thread("new", 9)}}, "p2"), nil
	}
	a.listThreads = func(ctx context.Context) (*gvproto.RespListThreads, error) {
		return a.listFolders(ctx, gvproto.ThreadFolder_ALL_THREADS)
	}
	var asked []string
	a.listPageAfter = func(_ context.Context, token string) (*gvproto.RespListThreads, error) {
		asked = append(asked, token)
		if token == "p2" {
			return withPageToken(&gvproto.RespListThreads{Threads: []*gvproto.Thread{thread("older", 5)}}, "p3"), nil
		}
		return withPageToken(&gvproto.RespListThreads{Threads: []*gvproto.Thread{thread("oldest", 2)}}, "p3"), nil
	}
	page := func(cursor string) *chatPage {
		result, code := a.callContext(context.Background(), request{Method: "ui.chatsPage", Params: json.RawMessage(`{"cursor":"` + cursor + `"}`)})
		if code != "" {
			t.Fatalf("page %q failed: %q", cursor, code)
		}
		return result.(*chatPage)
	}
	first := page("")
	if len(first.Chats) != 2 || first.Next != "p2" {
		t.Fatalf("the first page is the main list with the archived folder and the main list's token: %+v", first)
	}
	second := page("p2")
	if len(second.Chats) != 1 || second.Chats[0].ID != "older" || second.Next != "p3" || asked[0] != "p2" {
		t.Fatalf("a later page follows the token: %+v %v", second, asked)
	}
	third := page("p3")
	if third.Next != "" {
		t.Fatalf("a page that points back at itself ends the list: %+v", third)
	}
	if _, watched := a.lastSeen["older"]; watched || !a.watermarked || a.seenThrough != 9 {
		t.Fatalf("a list only sets the starting point for change detection: %v %v %d", a.lastSeen, a.watermarked, a.seenThrough)
	}
}

// An adapter whose main list is the given threads, with a signed-in client and an empty contact book, and the events
// it writes.
func watchedAdapter(t *testing.T, threads *[]*gvproto.Thread) (*adapter, *capture) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	t.Cleanup(a.stop)
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	a.contacts.autocomplete = func(context.Context, *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
		return &gvproto.RespAutocompleteContacts{}, nil
	}
	a.contacts.lookup = func(context.Context, *libgv.Client, []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
		return map[string]*gvproto.RespLookupContacts_Match{}, nil
	}
	a.listFolders = func(_ context.Context, folder gvproto.ThreadFolder) (*gvproto.RespListThreads, error) {
		if folder != gvproto.ThreadFolder_ALL_THREADS {
			return &gvproto.RespListThreads{}, nil
		}
		return &gvproto.RespListThreads{Threads: *threads}, nil
	}
	a.listThreads = func(ctx context.Context) (*gvproto.RespListThreads, error) {
		return a.listFolders(ctx, gvproto.ThreadFolder_ALL_THREADS)
	}
	return a, sink
}

func listChats(t *testing.T, a *adapter) {
	if _, code := a.callContext(context.Background(), request{Method: "ui.chats"}); code != "" {
		t.Fatalf("listing failed: %q", code)
	}
}

func voiceThread(id string, read bool, stamps ...int64) *gvproto.Thread {
	thread := &gvproto.Thread{ID: id, Read: read}
	for i := len(stamps) - 1; i >= 0; i-- {
		thread.Messages = append(thread.Messages, &gvproto.Message{ID: fmt.Sprintf("%s-%d", id, stamps[i]), Timestamp: stamps[i]})
	}
	return thread
}

// The change events written, each as its chats and arrived message ids.
func changes(t *testing.T, sink *capture) []map[string]any {
	var found []map[string]any
	for _, line := range sink.lines {
		var event struct {
			Event  string         `json:"event"`
			Result map[string]any `json:"result"`
		}
		if json.Unmarshal([]byte(line), &event) != nil {
			t.Fatalf("unreadable event %q", line)
		}
		if event.Event == "changed" {
			found = append(found, event.Result)
		}
	}
	return found
}

func TestStartupListThenQuietDetectionAnnouncesNothing(t *testing.T) {
	threads := []*gvproto.Thread{voiceThread("a", true, 100, 200), voiceThread("b", false, 150)}
	a, sink := watchedAdapter(t, &threads)
	listChats(t, a)
	a.detectChanges(context.Background())
	if got := changes(t, sink); len(got) != 0 {
		t.Fatalf("history that was already listed must not be announced: %v", got)
	}
}

func TestDetectionBeforeAnyListStartsQuietly(t *testing.T) {
	threads := []*gvproto.Thread{voiceThread("a", true, 100, 200)}
	a, sink := watchedAdapter(t, &threads)
	a.detectChanges(context.Background())
	if got := changes(t, sink); len(got) != 0 || a.seenThrough != 200 || !a.watermarked {
		t.Fatalf("the first detection only sets the starting point: %v %d", got, a.seenThrough)
	}
	threads[0] = voiceThread("a", false, 100, 200, 300)
	a.detectChanges(context.Background())
	if got := changes(t, sink); len(got) != 1 || fmt.Sprint(got[0]["messages"]) != "map[a:[a-300]]" {
		t.Fatalf("after it, a newer message is announced by id: %v", got)
	}
}

func TestNewConversationAfterStartupIsAnnouncedWithItsMessages(t *testing.T) {
	threads := []*gvproto.Thread{voiceThread("a", true, 100, 200)}
	a, sink := watchedAdapter(t, &threads)
	listChats(t, a)
	a.detectChanges(context.Background())
	// A new number, and a conversation from beyond the watched list that moved to the top, each with a newer text.
	threads = []*gvproto.Thread{voiceThread("new", false, 250), voiceThread("older", false, 50, 260), threads[0]}
	a.detectChanges(context.Background())
	got := changes(t, sink)
	if len(got) != 1 || fmt.Sprint(got[0]["chats"]) != "[new older]" || fmt.Sprint(got[0]["messages"]) != "map[new:[new-250] older:[older-260]]" {
		t.Fatalf("both conversations and only their new messages are announced: %v", got)
	}
	if !strings.Contains(strings.Join(sink.lines, "\n"), `"arrived":2`) || !strings.Contains(strings.Join(sink.lines, "\n"), `"newThreads":2`) {
		t.Fatalf("the refresh diagnostic counts what it announced: %v", sink.lines)
	}
}

func TestAListReadBetweenArrivalAndDetectionDoesNotHideTheMessage(t *testing.T) {
	threads := []*gvproto.Thread{voiceThread("a", true, 100, 200)}
	a, sink := watchedAdapter(t, &threads)
	listChats(t, a)
	a.detectChanges(context.Background())
	threads[0] = voiceThread("a", false, 100, 200, 300)
	listChats(t, a)
	a.detectChanges(context.Background())
	if got := changes(t, sink); len(got) != 1 || fmt.Sprint(got[0]["messages"]) != "map[a:[a-300]]" {
		t.Fatalf("the app's own list read must not absorb an arrival: %v", got)
	}
}

func TestReadingElsewhereBeforeTheFirstDetectionIsStillReported(t *testing.T) {
	threads := []*gvproto.Thread{voiceThread("a", false, 100, 200)}
	a, sink := watchedAdapter(t, &threads)
	listChats(t, a)
	threads[0] = voiceThread("a", true, 100, 200)
	a.detectChanges(context.Background())
	if got := changes(t, sink); len(got) != 1 || fmt.Sprint(got[0]["chats"]) != "[a]" || fmt.Sprint(got[0]["messages"]) != "map[]" {
		t.Fatalf("a conversation read on the phone changes without naming messages: %v", got)
	}
}

func TestNewConversationOpensEmptyUntilItsFirstMessage(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	a.getThread = func(context.Context, *libgv.Client, string, int, string) (*gvproto.RespGetThread, error) {
		return nil, &libgv.ResponseError{Resp: &http.Response{StatusCode: http.StatusNotFound}}
	}
	result, code := a.callContext(context.Background(), request{Method: "ui.messages", Params: json.RawMessage(`{"chatId":"t.+15550007"}`)})
	if code != "" || len(result.(map[string]any)["messages"].([]voiceMessage)) != 0 {
		t.Fatalf("an unstarted conversation is empty, not an error: %v %q", result, code)
	}
	if _, code := a.callContext(context.Background(), request{Method: "ui.messages", Params: json.RawMessage(`{"chatId":"g.group"}`)}); code == "" {
		t.Fatal("only a one-to-one conversation by number can be unstarted")
	}
	a.getThread = func(context.Context, *libgv.Client, string, int, string) (*gvproto.RespGetThread, error) {
		return nil, &libgv.ResponseError{Resp: &http.Response{StatusCode: http.StatusUnauthorized}}
	}
	if _, code := a.callContext(context.Background(), request{Method: "ui.messages", Params: json.RawMessage(`{"chatId":"t.+15550007"}`)}); code != "auth_required" {
		t.Fatalf("a refused session is still reported: %q", code)
	}
}

func TestOneConversationReadsFromItsOwnThread(t *testing.T) {
	var threads []*gvproto.Thread
	a, _ := watchedAdapter(t, &threads)
	asked := ""
	a.getThread = func(_ context.Context, _ *libgv.Client, id string, count int, _ string) (*gvproto.RespGetThread, error) {
		asked = fmt.Sprintf("%s/%d", id, count)
		return &gvproto.RespGetThread{Thread: voiceThread(id, false, 100, 200)}, nil
	}
	result, code := a.callContext(context.Background(), request{Method: "ui.chat", Params: json.RawMessage(`{"chatId":"c.old"}`)})
	chat, ok := result.(voiceChat)
	if code != "" || !ok || chat.ID != "c.old" || !chat.Unread || chat.LastActivity != 200 || chat.Preview == nil || asked != "c.old/1" {
		t.Fatalf("one conversation is its thread's newest message and read state: %#v %q %s", result, code, asked)
	}
	if read, known := a.lastRead["c.old"]; !known || read {
		t.Fatal("its read state is remembered for change detection")
	}
	a.getThread = func(context.Context, *libgv.Client, string, int, string) (*gvproto.RespGetThread, error) {
		return &gvproto.RespGetThread{}, nil
	}
	if _, code := a.callContext(context.Background(), request{Method: "ui.chat", Params: json.RawMessage(`{"chatId":"c.gone"}`)}); code != "not_found" {
		t.Fatalf("a thread Voice no longer has is not found: %q", code)
	}
}
