package main

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	waTypes "go.mau.fi/whatsmeow/types"
)

func TestThreadReadRoutesEncryptedThreadsThroughTheirSyncGroupAfterRestart(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/messages.db"
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	s := newStore()
	s.get(7).kind = table.ONE_TO_ONE
	s.get(8).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	s.get(9).kind = table.ENCRYPTED_OVER_WA_GROUP
	kept.saveIdentity(ctx, s)
	kept.db.Close()
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	restored := newStore()
	kept.restore(ctx, restored)
	for _, current := range []*store{s, restored} {
		for key, group := range map[int64]int64{7: 1, 8: 95, 9: 95} {
			task := current.threadReadTask(key, 123456)
			if task.ThreadId != key || task.SyncGroup != group || task.LastReadWatermarkTs != 123456 {
				t.Fatal("thread read lost routing or visible boundary")
			}
		}
	}
}

// Facebook's thread row can put a conversation's activity a few milliseconds after its newest message. Reading the
// newest message has to count as reading the conversation, or it stays marked unread.
func TestReadingTheNewestMessageClearsUnreadDespiteLaterActivity(t *testing.T) {
	s := newStore()
	s.self = 1
	s.messages[7] = map[string]*message{"a": {id: "a", encrypted: true, sender: 2, timestamp: 1000}}
	current := s.get(7)
	current.lastActivity = 1004
	current.readAt = 500
	// What Facebook is told must reach the activity time too, or its own apps keep the conversation unread.
	if got := s.readThrough(7, 1000); got != 1004 {
		t.Fatalf("thread watermark for Facebook = %d, want the activity time 1004", got)
	}
	if !s.markReadUpTo(7, 1000) || current.readAt < current.lastActivity {
		t.Fatalf("read up to %d with activity at %d", current.readAt, current.lastActivity)
	}
	// Reading an older message while a newer one exists must not claim the newer one was read.
	s.messages[7]["b"] = &message{id: "b", encrypted: true, sender: 2, timestamp: 2000}
	current.lastActivity = 2003
	if got := s.readThrough(7, 1500); got != 1500 {
		t.Fatalf("thread watermark for Facebook passed an unread newer message: %d", got)
	}
	s.markReadUpTo(7, 1500)
	if current.readAt >= 2000 {
		t.Fatalf("an unread newer message was marked read: readAt=%d", current.readAt)
	}
	if got := s.unreadUpTo(7, 2000); len(got) != 2 || got[1].id != "b" {
		t.Fatalf("receipt boundary omitted messages after local read advancement: %+v", got)
	}
}

// An encrypted arrival brings no thread time, so a read has to clear it before this copy knows it: the thread row that
// finally says it, at the next connect, must find the conversation read here and on Facebook.
func TestReadClearsAThreadTimeNotYetKnown(t *testing.T) {
	const at, long = 100_000, 900_000
	s := newStore()
	s.self = 1
	s.get(7).readAt = 500
	s.addEncrypted(7, "a", 2, "hello", "", at)
	// Only as far as now: a watermark is never in the future, and a fast clock on Facebook's side never moves it back.
	if through, _ := s.readToTell(7, at, at+300); through != at+300 {
		t.Fatalf("a read just after arrival told through %d, want now", through)
	}
	if through, _ := s.readToTell(7, at, at-5000); through != at {
		t.Fatalf("a clock behind the message told through %d, want the message", through)
	}
	through, activity := s.readToTell(7, at, long)
	if through != at+readMargin || activity != 0 {
		t.Fatalf("read told through %d with activity %d, want %d and 0", through, activity, at+readMargin)
	}
	if !s.markReadUpTo(7, through) || s.get(7).readAt != through {
		t.Fatalf("our copy read through %d, Facebook told %d", s.get(7).readAt, through)
	}
	s.get(7).lastActivity = at + 809
	if chat, _ := s.chatByKey(7); chat.Unread {
		t.Fatal("the thread row's later activity time brought the read conversation back as unread")
	}
	// A known activity time past the margin is still reached, and said.
	s.get(7).lastActivity = at + 9000
	if through, activity := s.readToTell(7, at, long); through != at+9000 || activity != 9000 {
		t.Fatalf("read told through %d with activity %d, want the activity time", through, activity)
	}
	// A newer message known here is not read by reading this one, however close behind it is.
	s.addEncrypted(7, "b", 2, "again", "", at+500)
	if through, activity := s.readToTell(7, at, long); through != at || activity != 0 {
		t.Fatalf("the margin passed a newer message: %d", through)
	}
	if through, _ := s.readToTell(404, at, long); through != at {
		t.Fatalf("a thread this copy does not hold was told through %d", through)
	}
}

func TestOutgoingPreviewCannotRemainUnread(t *testing.T) {
	s := newStore()
	s.self = 1
	s.get(7).readAt = 500
	s.addEncrypted(7, "incoming", 2, "hello", "", 1000)
	if chat, _ := s.chatByKey(7); !chat.Unread || chat.PreviewOutgoing {
		t.Fatal("incoming preview was not unread")
	}
	s.markSent(7, "reply", 1500)
	// Facebook's thread activity can be a few milliseconds later than the message.
	s.get(7).lastActivity = 1504
	if chat, _ := s.chatByKey(7); chat.Unread || !chat.PreviewOutgoing {
		t.Fatal("outgoing preview retained a stale unread mark")
	}
	s.addEncrypted(7, "newer", 2, "new reply", "", 2000)
	if chat, _ := s.chatByKey(7); !chat.Unread || chat.PreviewOutgoing {
		t.Fatal("new incoming activity did not restore unread")
	}
}

func TestGroupPreviewNamesItsWriter(t *testing.T) {
	s := newStore()
	s.self = 1
	s.contacts[2] = "Synthetic Writer"
	s.get(8).kind = table.GROUP_THREAD
	s.addEncrypted(8, "incoming", 2, "hello", "", 1000)
	// Facebook's thread activity can be a few milliseconds later than the message.
	s.get(8).lastActivity = 1004
	if chat, _ := s.chatByKey(8); chat.PreviewSender != "2" || chat.PreviewName != "Synthetic Writer" {
		t.Fatalf("group preview names %q %q, want the writer", chat.PreviewSender, chat.PreviewName)
	}
	// Activity well after the newest message is something else (a reaction): the line's words are not that message's.
	s.get(8).lastActivity = 9000
	if chat, _ := s.chatByKey(8); chat.PreviewSender != "" {
		t.Fatal("a later activity was named as the newest message's writer")
	}
	s.markSent(8, "reply", 10000)
	if chat, _ := s.chatByKey(8); chat.PreviewSender != "" || !chat.PreviewOutgoing {
		t.Fatal("this account's own line named a writer")
	}
	s.get(9).kind = table.ONE_TO_ONE
	s.addEncrypted(9, "direct", 2, "hi", "", 1000)
	if chat, _ := s.chatByKey(9); chat.PreviewSender != "" {
		t.Fatal("a one-to-one's line named its writer")
	}
}

func TestEncryptedReadPayloadUsesReadingTimeAndCorrectAddressing(t *testing.T) {
	for _, group := range []bool{false, true} {
		t.Run(map[bool]string{false: "direct", true: "group"}[group], func(t *testing.T) {
			a := newAdapter(nil)
			defer a.stop()
			a.chats.setSelf(1)
			a.chats.get(7).readAt = 500
			a.chats.addEncrypted(7, "first", 2, "message", "", 1000)
			a.chats.addEncrypted(7, "second", 2, "message", "", 1200)
			a.chats.addEncrypted(7, "own", 1, "message", "", 1300)
			server := waTypes.MessengerServer
			if group {
				server = waTypes.GroupServer
			}
			to := waTypes.NewJID("555", server)
			before := time.Now()
			calls := 0
			result, code := a.acknowledgeRead(7, 1300, true, func() (any, string) {
				return a.sendEncryptedRead(context.Background(), 7, 1300, to, func(ctx context.Context, ids []waTypes.MessageID, at time.Time, chat, sender waTypes.JID) error {
					calls++
					if len(ids) != 2 || ids[0] != "second" || ids[1] != "first" || chat != to {
						t.Fatal("wrong message ids or destination")
					}
					if at.Before(before) || at.After(time.Now()) {
						t.Fatal("receipt used message timestamp rather than reading time")
					}
					if group {
						if sender != waTypes.NewJID("2", waTypes.MessengerServer) {
							t.Fatal("group receipt omitted sender")
						}
					} else if !sender.IsEmpty() {
						t.Fatal("direct receipt unexpectedly named participant")
					}
					// Incoming messages during the write must remain unread.
					a.chats.addEncrypted(7, "newer", 2, "new message", "", 2000)
					return nil
				})
			}, func() string { return "" })
			if code != "" || calls != 1 || result.(map[string]any)["receiptsSent"] != 2 || a.chats.get(7).readAt != 1300 {
				t.Fatalf("wrong acknowledgement result: %v %s", result, code)
			}
			if chat, _ := a.chats.chatByKey(7); !chat.Unread {
				t.Fatal("newer message was incorrectly acknowledged")
			}
		})
	}
}

func TestPartialEncryptedReadFailureDoesNotAcknowledgeWholeConversation(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.chats.setSelf(1)
	a.chats.get(7).readAt = 500
	a.chats.addEncrypted(7, "one", 2, "message", "", 1000)
	a.chats.addEncrypted(7, "two", 3, "message", "", 1200)
	to := waTypes.NewJID("555", waTypes.GroupServer)
	calls := 0
	result, code := a.sendEncryptedRead(context.Background(), 7, 1200, to, func(ctx context.Context, ids []waTypes.MessageID, at time.Time, chat, sender waTypes.JID) error {
		calls++
		if sender.User == "3" {
			return errors.New("synthetic_write_failure")
		}
		return nil
	})
	if result != nil || code != "mark_read_failed" || calls != 2 || a.chats.get(7).readAt != 500 {
		t.Fatal("partial write was treated as completely acknowledged")
	}
	if pending := a.chats.unreadUpTo(7, 1200); len(pending) != 2 {
		t.Fatal("failed receipt was suppressed for future viewing activity")
	}
}

func TestGroupReadBatchIncludesOlderSpeakersAndCapsEachSender(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.get(7).kind = table.ENCRYPTED_OVER_WA_GROUP
	s.addEncrypted(7, "older-speaker", 3, "message", "", 100)
	for i := 0; i < readBatch+10; i++ {
		s.addEncrypted(7, fmt.Sprintf("newer-%02d", i), 2, "message", "", int64(200+i))
	}
	found := s.unreadUpTo(7, 1000)
	if len(found) != readBatch+1 || found[0].id != "older-speaker" || found[1].id != "newer-10" {
		t.Fatal("batch cap omitted older speaker or did not keep newest per sender")
	}
}

func TestNoEligibleEncryptedMessagesDoesNotWriteAReceipt(t *testing.T) {
	a := newAdapter(nil)
	defer a.stop()
	a.chats.setSelf(1)
	a.chats.get(7).readAt = 1000
	a.chats.addEncrypted(7, "own", 1, "message", "", 1100)
	result, code := a.sendEncryptedRead(context.Background(), 7, 1100, waTypes.NewJID("555", waTypes.MessengerServer), func(context.Context, []waTypes.MessageID, time.Time, waTypes.JID, waTypes.JID) error {
		t.Fatal("no eligible messages must not send")
		return nil
	})
	if code != "" || result.(map[string]any)["receiptsSent"] != 0 {
		t.Fatal("no-op should explicitly report zero sent receipts")
	}
}

func TestReadRequiresBothChannelsBeforeClearingUnread(t *testing.T) {
	for _, scenario := range []struct {
		name                      string
		encrypted                 bool
		encryptedCode, threadCode string
		wantEncrypted, wantThread int
		accepted                  bool
	}{
		{"ordinary", false, "", "", 0, 1, true},
		{"encrypted", true, "", "", 1, 1, true},
		{"encrypted failed", true, "mark_read_failed", "", 1, 0, false},
		{"thread failed", true, "", "mark_read_failed", 1, 1, false},
		{"encrypted offline", true, "e2ee_offline", "", 1, 1, true},
		{"encrypted off", true, "e2ee_off", "", 1, 1, true},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			a := newAdapter(nil)
			defer a.stop()
			a.chats.get(7).readAt = 500
			a.chats.get(7).lastActivity = 1000
			encryptedCalls, threadCalls := 0, 0
			result, code := a.acknowledgeRead(7, 1000, scenario.encrypted, func() (any, string) {
				encryptedCalls++
				return map[string]any{"outcome": "accepted", "receiptsSent": 1}, scenario.encryptedCode
			}, func() string {
				threadCalls++
				if a.chats.get(7).readAt != 500 {
					t.Fatal("local read advanced before both writes")
				}
				return scenario.threadCode
			})
			if encryptedCalls != scenario.wantEncrypted || threadCalls != scenario.wantThread || (code == "") != scenario.accepted {
				t.Fatal("wrong channel calls or outcome")
			}
			if scenario.accepted {
				if result == nil || a.chats.get(7).readAt != 1000 {
					t.Fatal("successful read not finalized")
				}
			} else if result != nil || a.chats.get(7).readAt != 500 {
				t.Fatal("incomplete read was finalized")
			}
		})
	}
}

func TestReplyAndThreadSyncDoNotSuppressEncryptedReceipt(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.addEncrypted(7, "incoming", 2, "message", "", 1000)
	s.markSent(7, "reply", 1500)
	s.apply(&table.LSTable{LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 7, LastReadWatermarkTimestampMs: 2000}}})
	if got := s.unreadUpTo(7, 1000); len(got) != 1 || got[0].id != "incoming" {
		t.Fatal("local reply/thread read state suppressed receipt")
	}
}

func TestReceiptSelectionKeepsTransportAcrossRestart(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/messages.db"
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	s := newStore()
	s.setSelf(1)
	s.get(7).readAt = 2000
	kept.remember(ctx, 7, "encrypted", 2, "message", "", 1000, true)
	kept.remember(ctx, 7, "ordinary", 2, "history", "", 900, false)
	kept.saveIdentity(ctx, s)
	kept.db.Close()
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	restored := newStore()
	restored.setSelf(1)
	if kept.restore(ctx, restored) != 2 {
		t.Fatal("history lost across restart")
	}
	if got := restored.unreadUpTo(7, 1000); len(got) != 1 || got[0].id != "encrypted" {
		t.Fatal("restart suppressed encrypted receipt or included an ordinary message ID")
	}
	restored.get(7).deletedAt = 1100
	if got := restored.unreadUpTo(7, 2000); len(got) != 0 {
		t.Fatal("pre-deletion messages were acknowledged")
	}
}

func TestEncryptedReceiptPrimaryAdvancesPastRestoredHistory(t *testing.T) {
	for _, group := range []bool{false, true} {
		t.Run(fmt.Sprintf("group=%v", group), func(t *testing.T) {
			ctx := context.Background()
			path := t.TempDir() + "/messages.db"
			kept, err := openMessageStore(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			s := newStore()
			s.setSelf(1)
			s.get(7).readAt = 9000 // Thread sync/local replies are not encrypted acknowledgements.
			for i := 0; i < readBatch+5; i++ {
				kept.remember(ctx, 7, fmt.Sprintf("old-%02d", i), 2, "old", "", int64(1000+i), true)
			}
			if group {
				kept.remember(ctx, 7, "older-speaker", 3, "old", "", 1100, true)
			}
			kept.saveIdentity(ctx, s)
			kept.db.Close()
			kept, err = openMessageStore(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			defer kept.db.Close()
			a := newAdapter(nil)
			defer a.stop()
			a.chats.setSelf(1)
			if kept.restore(ctx, a.chats) < readBatch {
				t.Fatal("archive not restored")
			}
			a.chats.addEncrypted(7, "first-new", 2, "new", "", 2000)
			a.chats.addEncrypted(7, "ordinary", 2, "history", "", 3000, false)
			a.chats.addEncrypted(7, "own", 1, "reply", "", 4000)
			a.chats.addEncrypted(7, "second-new", 2, "newer", "", 5000)
			server := waTypes.MessengerServer
			if group {
				server = waTypes.GroupServer
			}
			to := waTypes.NewJID("555", server)
			for _, step := range []struct {
				boundary int64
				primary  string
			}{{4000, "first-new"}, {5000, "second-new"}, {5000, "second-new"}} {
				calls := 0
				_, code := a.sendEncryptedRead(ctx, 7, step.boundary, to, func(_ context.Context, ids []waTypes.MessageID, _ time.Time, chat, sender waTypes.JID) error {
					calls++
					if chat != to || len(ids) == 0 {
						t.Fatal("missing receipt destination or IDs")
					}
					if group && sender.User == "3" {
						if len(ids) != 1 || ids[0] != "older-speaker" {
							t.Fatal("older group speaker lost its own receipt")
						}
						return nil
					}
					if len(ids) != readBatch || ids[0] != step.primary {
						t.Fatalf("primary receipt pinned to archive: got %s, want %s", ids[0], step.primary)
					}
					for _, id := range ids {
						if id == "ordinary" || id == "own" || (step.boundary < 5000 && id == "second-new") {
							t.Fatal("acknowledged an ineligible message")
						}
					}
					return nil
				})
				wantCalls := 1
				if group {
					wantCalls = 2
				}
				if code != "" || calls != wantCalls {
					t.Fatal("wrong receipt outcome or sender count")
				}
			}
		})
	}
}

func TestLegacyMessageChannelMigrationPreservesHistory(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/legacy.db"
	db, err := sql.Open("sqlite", "file:"+path)
	if err != nil {
		t.Fatal(err)
	}
	_, err = db.ExecContext(ctx, `CREATE TABLE encrypted_message (id TEXT PRIMARY KEY, thread INTEGER NOT NULL,
		sender INTEGER NOT NULL, body TEXT NOT NULL, at INTEGER NOT NULL);
		INSERT INTO encrypted_message VALUES ('123456789',7,2,'encrypted',1000),
		('mid.$synthetic',7,2,'ordinary',900),('unknown-legacy',7,2,'unknown',800)`)
	if err != nil {
		t.Fatal(err)
	}
	db.Close()
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	restored := newStore()
	restored.setSelf(1)
	if kept.restore(ctx, restored) != 3 {
		t.Fatal("migration discarded history")
	}
	if got := restored.unreadUpTo(7, 1000); len(got) != 1 || got[0].id != "123456789" {
		t.Fatal("migration sent ordinary/unknown IDs as encrypted receipts")
	}
}

// Facebook saying less has been read than this copy does is counted once per watermark, never adopted (owner,
// 2026-10-22: measured before it is followed), and a row older than the copy is not counted at all.
func TestLowerFacebookWatermarkIsCountedNotAdopted(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	row := func(activity, read int64) {
		s.apply(&table.LSTable{LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{
			{ThreadKey: 5, LastActivityTimestampMs: activity, LastReadWatermarkTimestampMs: read, ThreadType: table.ONE_TO_ONE},
		}})
	}
	row(100, 100)
	row(100, 90)
	if s.get(5).readAt != 100 || s.get(5).lowerNoted != 90 {
		t.Fatalf("lower watermark adopted or not counted: read=%d noted=%d", s.get(5).readAt, s.get(5).lowerNoted)
	}
	row(50, 40)
	if s.get(5).lowerNoted != 90 {
		t.Fatal("an older row was counted as drift")
	}
}
