package main

import (
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestApplyReportsWhatChanged(t *testing.T) {
	s := newStore()
	touched := s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 4, LastActivityTimestampMs: 10, ThreadType: table.ONE_TO_ONE}},
		LSUpsertMessage: []*table.LSUpsertMessage{
			{ThreadKey: 4, MessageId: "m2", TimestampMs: 20, SenderId: 5, Text: "hi"},
			{ThreadKey: 4, MessageId: "m1", TimestampMs: 15, SenderId: 5},
		},
		// An attachment names the message it belongs to, which may have arrived in an earlier batch.
		LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{
			{ThreadKey: 9, MessageId: "m3", AttachmentFbid: "a1", PreviewUrl: "https://example.test/a.jpg", AttachmentType: table.AttachmentTypeImage},
		},
	})
	if len(touched.Chats) != 2 || touched.Chats[0] != "4" || touched.Chats[1] != "9" {
		t.Fatalf("unexpected chats: %+v", touched.Chats)
	}
	if got := touched.Messages["4"]; len(got) != 2 || got[0] != "m1" || got[1] != "m2" {
		t.Fatalf("unexpected messages: %+v", touched.Messages)
	}
	if got := touched.Messages["9"]; len(got) != 1 || got[0] != "m3" {
		t.Fatalf("unexpected attachment message: %+v", touched.Messages)
	}
	if (&changes{}).empty() != true || touched.empty() {
		t.Fatal("emptiness is wrong")
	}
}

func TestApplyReportsNothingForRowsThatChangeNoConversation(t *testing.T) {
	s := newStore()
	touched := s.apply(&table.LSTable{
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{Id: 12, Name: "Alex"}},
	})
	if !touched.empty() {
		t.Fatalf("expected no changes, got %+v", touched)
	}
	if !s.apply(nil).empty() {
		t.Fatal("an empty batch changes nothing")
	}
}

func TestStoreReadsInsertedMessagesAsWellAsUpserts(t *testing.T) {
	s := newStore()
	s.setSelf(5)
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 4, LastActivityTimestampMs: 10, ThreadType: table.ONE_TO_ONE}},
		LSInsertMessage:          []*table.LSInsertMessage{{ThreadKey: 4, MessageId: "m1", TimestampMs: 30, SenderId: 6, Text: "arrived"}},
	})
	history := s.history(4, 0)
	if len(history) != 1 || history[0].ID != "m1" || history[0].Text != "arrived" || history[0].Outgoing {
		t.Fatalf("unexpected history: %+v", history)
	}
	// A new message moves its conversation up the list even without a thread row.
	if chats := s.chats(0); len(chats) != 1 || chats[0].Preview != "arrived" {
		t.Fatalf("unexpected chats: %+v", chats)
	}
}

func TestMessageLookupFindsSentMessagesByEitherID(t *testing.T) {
	s := newStore()
	s.setSelf(5)
	// Facebook answers a send with the pairing first, and the message row lands afterwards.
	s.apply(&table.LSTable{
		LSReplaceOptimsiticMessage: []*table.LSReplaceOptimsiticMessage{{OfflineThreadingId: "777", MessageId: "mid:1"}},
	})
	if _, ok := s.messageByID(4, "777"); ok {
		t.Fatal("no message is known yet")
	}
	if id, ok := s.sentAs("777"); !ok || id != "mid:1" {
		t.Fatalf("unexpected pairing: %q %v", id, ok)
	}
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "mid:1", OfflineThreadingId: "777", TimestampMs: 40, SenderId: 5, Text: "sent"}},
	})
	byReal, ok := s.messageByID(4, "mid:1")
	if !ok || !byReal.Outgoing || byReal.Text != "sent" || byReal.ChatID != "4" {
		t.Fatalf("unexpected message: %+v %v", byReal, ok)
	}
	// The same message resolves from the id the app was given while the send was still pending.
	byPending, ok := s.messageByID(4, "777")
	if !ok || byPending.ID != "mid:1" {
		t.Fatalf("unexpected pending lookup: %+v %v", byPending, ok)
	}
	if _, ok := s.messageByID(4, "nope"); ok {
		t.Fatal("an unknown id resolves to nothing")
	}
}

func TestDeletedMessagesLeaveTheConversation(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 4, LastActivityTimestampMs: 10, ThreadType: table.ONE_TO_ONE}},
		LSUpsertMessage: []*table.LSUpsertMessage{
			{ThreadKey: 4, MessageId: "m1", TimestampMs: 20, SenderId: 5, Text: "here"},
			{ThreadKey: 4, MessageId: "m2", TimestampMs: 30, SenderId: 5, Text: "gone"},
			{ThreadKey: 4, MessageId: "m3", TimestampMs: 40, SenderId: 5, Text: "unsent"},
		},
	})
	// Removed on another device, and unsent for everyone.
	touched := s.apply(&table.LSTable{
		LSDeleteMessage:           []*table.LSDeleteMessage{{ThreadKey: 4, MessageId: "m2"}},
		LSDeleteThenInsertMessage: []*table.LSDeleteThenInsertMessage{{ThreadKey: 4, MessageId: "m3", IsUnsent: true}},
	})
	if got := touched.Deleted["4"]; len(got) != 2 || got[0] != "m2" || got[1] != "m3" {
		t.Fatalf("unexpected deletions: %+v", touched)
	}
	if len(touched.Messages) != 0 {
		t.Fatalf("a deleted message is not also a change: %+v", touched.Messages)
	}
	history := s.history(4, 0)
	if len(history) != 1 || history[0].ID != "m1" {
		t.Fatalf("unexpected history: %+v", history)
	}
	if _, ok := s.messageByID(4, "m2"); ok {
		t.Fatal("a deleted message is no longer known")
	}
	// A rewritten message that was not unsent is an edit, not a deletion.
	kept := s.apply(&table.LSTable{
		LSDeleteThenInsertMessage: []*table.LSDeleteThenInsertMessage{{ThreadKey: 4, MessageId: "m1"}},
	})
	if len(kept.Deleted) != 0 {
		t.Fatalf("unexpected deletion: %+v", kept)
	}
}

func TestMissingPicturesAsksForTheNewestConversationsFirst(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 7, LastActivityTimestampMs: 100, ThreadType: table.GROUP_THREAD},
			{ThreadKey: 8, LastActivityTimestampMs: 200, ThreadType: table.GROUP_THREAD},
		},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: 7, ContactId: 1}, {ThreadKey: 7, ContactId: 30},
			{ThreadKey: 8, ContactId: 1}, {ThreadKey: 8, ContactId: 20}, {ThreadKey: 8, ContactId: 21},
		},
		// One of them already has a picture, and the viewer is never asked about.
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{Id: 21, Name: "Alex", ProfilePictureUrl: "https://example.test/a.jpg"}},
	})
	missing := s.missingPictures(10)
	if len(missing) != 2 || missing[0] != 20 || missing[1] != 30 {
		t.Fatalf("unexpected people: %+v", missing)
	}
	// Asked once, whether or not Facebook had a picture.
	s.markTried(missing)
	if got := s.missingPictures(10); len(got) != 0 {
		t.Fatalf("expected nobody left to ask: %+v", got)
	}
	// A picture arriving late belongs to the conversations that person is in.
	touched := s.apply(&table.LSTable{
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{Id: 20, Name: "Sam", ProfilePictureUrl: "https://example.test/s.jpg"}},
	})
	if len(touched.Chats) != 1 || touched.Chats[0] != "8" {
		t.Fatalf("unexpected conversations: %+v", touched.Chats)
	}
	// The same row again changes nothing, so the list is not redrawn for it.
	if again := s.apply(&table.LSTable{
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{Id: 20, Name: "Sam", ProfilePictureUrl: "https://example.test/s.jpg"}},
	}); !again.empty() {
		t.Fatalf("unexpected repeat: %+v", again)
	}
}

func TestConversationsArriveAsEitherThreadRow(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	// Facebook sends most conversations this way; only reading the other row left them out of the list entirely.
	s.apply(&table.LSTable{
		LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{
			{ThreadKey: 5, LastActivityTimestampMs: 50, ThreadType: table.ONE_TO_ONE, FolderName: "inbox", Snippet: "hello"},
		},
		LSVerifyContactParticipantExist: []*table.LSVerifyContactParticipantExist{{ThreadKey: 5, ContactID: 9, Name: "Gabe"}},
	})
	chats := s.chats(0)
	if len(chats) != 1 || chats[0].ID != "5" || chats[0].Title != "Gabe" || chats[0].Group || chats[0].Preview != "hello" {
		t.Fatalf("unexpected chat: %+v", chats)
	}
	// An update is a change, not a replacement: what it leaves out stays as it was.
	s.apply(&table.LSTable{
		LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 5, LastActivityTimestampMs: 80}},
	})
	after := s.chats(0)
	if len(after) != 1 || after[0].Preview != "hello" || after[0].Title != "Gabe" {
		t.Fatalf("an update cleared what it did not carry: %+v", after)
	}
	if after[0].At.UnixMilli() != 80 {
		t.Fatalf("unexpected activity: %+v", after[0].At)
	}
	// Both rows carry "cannot reply" as a value that is absent when there is no reason (upstream 8f2831e moved this
	// row's column to where Facebook sends it, and gave it the other row's type).
	if after[0].ReadOnly {
		t.Fatal("a conversation with no reason given can be replied to")
	}
	s.apply(&table.LSTable{
		LSUpdateOrInsertThread: []*table.LSUpdateOrInsertThread{{ThreadKey: 5, LastActivityTimestampMs: 90, CannotReplyReason: int64(3)}},
	})
	if !s.chats(0)[0].ReadOnly {
		t.Fatal("a reason given on an update row makes the conversation read-only")
	}
}

func TestChatLookupAndCount(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 4, ThreadName: "Weekend", LastActivityTimestampMs: 10, ThreadType: table.GROUP_THREAD},
		},
	})
	found, ok := s.chatByKey(4)
	if !ok || found.Title != "Weekend" {
		t.Fatalf("unexpected chat: %+v %v", found, ok)
	}
	if _, ok := s.chatByKey(5); ok {
		t.Fatal("an unknown conversation resolves to nothing")
	}
	if s.threadCount() != 1 {
		t.Fatalf("unexpected count: %d", s.threadCount())
	}
}

func TestEncryptedMessagesMoveIntoTheConversationWhenPaired(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	// The encrypted connection delivers before the ordinary one has said which conversation this is, so the message
	// is held under the number it came with.
	s.addEncrypted(555, "e1", 9, "hello", "", 100)
	if got := s.history(555, 0); len(got) != 1 {
		t.Fatalf("expected the message to be held: %+v", got)
	}
	// The pairing arrives with the conversation itself.
	touched := s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 7, LastActivityTimestampMs: 90, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE},
		},
		LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: 7, ThreadJID: 555}},
		LSDeleteThenInsertContact:  []*table.LSDeleteThenInsertContact{{Id: 555, Name: "Test peer"}},
	})
	if got := s.history(7, 0); len(got) != 1 || got[0].Text != "hello" {
		t.Fatalf("the held message did not join its conversation: %+v", got)
	}
	if got := s.history(555, 0); len(got) != 0 {
		t.Fatalf("the message was left behind as well: %+v", got)
	}
	// The conversation it was held under is gone, and the real one is announced as changed.
	if chats := s.chats(0); len(chats) != 1 || chats[0].ID != "7" {
		t.Fatalf("unexpected conversations: %+v", chats)
	}
	if len(touched.Messages["7"]) != 1 {
		t.Fatalf("the move was not announced: %+v", touched)
	}
	// And an encrypted conversation is only replyable once that connection is up.
	if !s.chats(0)[0].Encrypted || !s.chats(0)[0].ReadOnly {
		t.Fatal("an encrypted conversation with no connection should be read-only")
	}
	s.setSecure(true)
	if s.chats(0)[0].ReadOnly {
		t.Fatal("with the connection up it should not be")
	}
}
