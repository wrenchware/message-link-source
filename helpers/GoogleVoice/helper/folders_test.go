package main

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

func TestConversationListIncludesArchivedOnceAndSurvivesAFailedArchive(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	shared := &gvproto.Thread{ID: "both", Messages: []*gvproto.Message{{ID: "b", Timestamp: 2}}}
	failArchive := false
	a.listFolders = func(_ context.Context, folder gvproto.ThreadFolder) (*gvproto.RespListThreads, error) {
		switch folder {
		case gvproto.ThreadFolder_ALL_THREADS:
			return &gvproto.RespListThreads{Threads: []*gvproto.Thread{{ID: "main", Messages: []*gvproto.Message{{ID: "m", Timestamp: 3}}}, shared}}, nil
		case gvproto.ThreadFolder_ALL_SPAM_THREADS:
			return &gvproto.RespListThreads{Threads: []*gvproto.Thread{shared, {ID: "spam", Messages: []*gvproto.Message{{ID: "s", Timestamp: 4}}}}}, nil
		case gvproto.ThreadFolder_ALL_ARCHIVED_THREADS:
			if failArchive {
				return nil, errors.New("unavailable")
			}
			return &gvproto.RespListThreads{Threads: []*gvproto.Thread{shared, {ID: "old", Messages: []*gvproto.Message{{ID: "o", Timestamp: 1}}}}}, nil
		}
		t.Fatalf("unexpected folder %v", folder)
		return nil, nil
	}
	a.listThreads = func(ctx context.Context) (*gvproto.RespListThreads, error) {
		return a.listFolders(ctx, gvproto.ThreadFolder_ALL_THREADS)
	}
	merged, err := a.listWithArchived(context.Background())
	if err != nil || len(merged.GetThreads()) != 4 {
		t.Fatalf("expected each conversation once: %v %d", err, len(merged.GetThreads()))
	}
	chats := map[string]voiceChat{}
	for _, thread := range merged.GetThreads() {
		chats[thread.GetID()] = convertThread(thread)
	}
	if !chats["old"].Archived || chats["main"].Archived || chats["both"].Archived {
		t.Fatalf("only a conversation found in the archived folder alone is archived: %+v", chats)
	}
	if !chats["spam"].Spam || chats["spam"].Archived || chats["both"].Spam {
		t.Fatalf("a conversation found in the spam folder alone is spam: %+v", chats)
	}
	failArchive = true
	if merged, err := a.listWithArchived(context.Background()); err != nil || len(merged.GetThreads()) != 3 {
		t.Fatalf("a failed archived list must still answer with the main list: %v", err)
	}
}

func TestArchiveChangesOnlyTheArchivedAttributeOfOneConversation(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	update := func(params string) (any, string) {
		return a.callContext(context.Background(), request{Method: "ui.updateChat", Params: json.RawMessage(params)})
	}
	if _, code := update(`{"chatId":"t1","archived":true}`); code != "login_required" {
		t.Fatalf("archiving needs a signed-in client, got %q", code)
	}
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	var sent []*gvproto.ReqUpdateAttributes
	a.updateAttributes = func(_ context.Context, _ *libgv.Client, req *gvproto.ReqUpdateAttributes) error {
		sent = append(sent, req)
		return nil
	}
	if _, code := update(`{"chatId":"t1"}`); code != "invalid_input" || len(sent) != 0 {
		t.Fatalf("a request that doesn't say which way must be refused before anything is sent: %q", code)
	}
	for _, archived := range []bool{true, false} {
		sent = nil
		params, _ := json.Marshal(map[string]any{"chatId": "t1", "archived": archived})
		result, code := update(string(params))
		if code != "" || result.(map[string]any)["archived"] != archived || len(sent) != 1 {
			t.Fatalf("expected one update: %v %q", result, code)
		}
		got := sent[0]
		if got.GetAttributes().GetThreadID() != "t1" || got.GetAttributes().GetArchived() != archived || !got.GetOtherAttributes().GetArchived() ||
			got.GetAttributes().GetRead() || got.GetOtherAttributes().GetRead() || got.GetAttributes().GetIsBlocked() || got.GetAttributes().GetIsSpammer() {
			t.Fatalf("only the archived attribute of that conversation may change: %+v", got)
		}
	}
	if !mutationMethod("ui.updateChat") {
		t.Fatal("archiving runs in order with other account changes")
	}
}

func TestNotSpamChangesOnlyTheSpammerAttributeOfOneConversation(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.client = libgv.NewClient(map[string]string{"SID": "synthetic"})
	var sent []*gvproto.ReqUpdateAttributes
	a.updateAttributes = func(_ context.Context, _ *libgv.Client, req *gvproto.ReqUpdateAttributes) error {
		sent = append(sent, req)
		return nil
	}
	update := func(params string) (any, string) {
		return a.callContext(context.Background(), request{Method: "ui.updateChat", Params: json.RawMessage(params)})
	}
	for _, params := range []string{`{"chatId":"t1","spam":true}`, `{"chatId":"t1","spam":false,"archived":false}`} {
		if _, code := update(params); code != "invalid_input" || len(sent) != 0 {
			t.Fatalf("unsupported or ambiguous spam update must be refused: %q", code)
		}
	}
	result, code := update(`{"chatId":"t1","spam":false}`)
	if code != "" || result.(map[string]any)["spam"] != false || len(sent) != 1 {
		t.Fatalf("expected one Not spam update: %v %q", result, code)
	}
	got := sent[0]
	if got.GetAttributes().GetThreadID() != "t1" || got.GetAttributes().GetIsSpammer() || !got.GetOtherAttributes().GetIsSpammer() ||
		got.GetAttributes().GetArchived() || got.GetOtherAttributes().GetArchived() || got.GetAttributes().GetRead() || got.GetOtherAttributes().GetRead() ||
		got.GetAttributes().GetIsBlocked() || got.GetOtherAttributes().GetIsBlocked() {
		t.Fatalf("only the spammer attribute of that conversation may change: %+v", got)
	}
}

// The first page of conversations waited for the main list, then the archived folder, then the spam folder, one after
// another. The two folders are asked for alongside the main list, and merged in the same order as before.
func TestFoldersAreListedAlongsideTheMainList(t *testing.T) {
	sink := &capture{}
	a := newAdapter(&output{encoder: json.NewEncoder(sink)})
	defer a.stop()
	var asked atomic.Int32
	bothAsked := make(chan struct{})
	failMain := false
	folder := func(id string, delay time.Duration) (*gvproto.RespListThreads, error) {
		if asked.Add(1) == 2 {
			close(bothAsked)
		}
		time.Sleep(delay)
		return &gvproto.RespListThreads{Threads: []*gvproto.Thread{{ID: id, Messages: []*gvproto.Message{{ID: id, Timestamp: 1}}}}}, nil
	}
	a.listFolders = func(_ context.Context, which gvproto.ThreadFolder) (*gvproto.RespListThreads, error) {
		switch which {
		case gvproto.ThreadFolder_ALL_THREADS:
			// The main list answers only once both folders have been asked for.
			select {
			case <-bothAsked:
			case <-time.After(2 * time.Second):
				return nil, errors.New("the folders were not asked for alongside the main list")
			}
			if failMain {
				return nil, errors.New("unavailable")
			}
			return &gvproto.RespListThreads{Threads: []*gvproto.Thread{{ID: "main", Messages: []*gvproto.Message{{ID: "m", Timestamp: 2}}}}}, nil
		case gvproto.ThreadFolder_ALL_ARCHIVED_THREADS:
			// Answering after the spam folder must not change the order.
			return folder("archived", 50*time.Millisecond)
		case gvproto.ThreadFolder_ALL_SPAM_THREADS:
			return folder("spam", 0)
		}
		t.Errorf("unexpected folder %v", which)
		return nil, nil
	}
	a.listThreads = func(ctx context.Context) (*gvproto.RespListThreads, error) {
		return a.listFolders(ctx, gvproto.ThreadFolder_ALL_THREADS)
	}
	merged, err := a.listWithArchived(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	order := []string{}
	for _, thread := range merged.GetThreads() {
		order = append(order, thread.GetID())
	}
	if strings.Join(order, ",") != "main,archived,spam" {
		t.Fatalf("merged in the order %v", order)
	}
	// A main list that fails answers nothing, as before, and says nothing about the folders.
	asked.Store(0)
	bothAsked = make(chan struct{})
	failMain = true
	sink.lines = nil
	if _, err := a.listWithArchived(context.Background()); err == nil {
		t.Fatal("a failed main list must fail the listing")
	}
	if len(sink.lines) != 0 {
		t.Fatalf("a failed main list reported its folders: %v", sink.lines)
	}
}
