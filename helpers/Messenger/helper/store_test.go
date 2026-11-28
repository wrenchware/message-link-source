package main

import (
	"fmt"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func TestStoreBuildsChatsFromRows(t *testing.T) {
	s := newStore()
	s.setSelf(100)
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: 7, Snippet: "latest", LastActivityTimestampMs: 2000, LastReadWatermarkTimestampMs: 1000, ThreadType: table.ONE_TO_ONE, FolderName: "inbox"},
			{ThreadKey: 8, ThreadName: "Weekend", LastActivityTimestampMs: 3000, LastReadWatermarkTimestampMs: 3000, ThreadType: table.GROUP_THREAD, FolderName: "archive"},
		},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: 7, ContactId: 100}, {ThreadKey: 7, ContactId: 200},
		},
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{Id: 200, Name: "Alex"}},
	})
	chats := s.chats(0)
	if len(chats) != 2 {
		t.Fatalf("expected 2 chats, got %d", len(chats))
	}
	// Newest first.
	if chats[0].ID != "8" || !chats[0].Group || !chats[0].Archived || chats[0].Title != "Weekend" || chats[0].Unread {
		t.Fatalf("unexpected group chat: %+v", chats[0])
	}
	direct := chats[1]
	if direct.ID != "7" || direct.Group || !direct.Unread || direct.Preview != "latest" {
		t.Fatalf("unexpected direct chat: %+v", direct)
	}
	// The title falls back to the other participant, and the viewer is marked self.
	if direct.Title != "Alex" || len(direct.Participants) != 2 || !direct.Participants[0].Self || direct.Participants[1].Name != "Alex" {
		t.Fatalf("unexpected participants: %+v", direct.Participants)
	}
}

func TestStoreTakesNamesFromEveryContactRowShape(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 3, LastActivityTimestampMs: 5, ThreadType: table.ONE_TO_ONE}},
		// A one-to-one thread gets its participants from this row, not the group rows.
		LSVerifyContactParticipantExist: []*table.LSVerifyContactParticipantExist{
			{ThreadKey: 3, ContactID: 11, Name: "Jeff"}, {ThreadKey: 3, ContactID: 99},
		},
		LSVerifyContactRowExists: []*table.LSVerifyContactRowExists{
			{ContactId: 99, Name: "Me", IsSelf: true},
			{ContactId: 11, ProfilePictureUrl: "https://example.test/jeff.jpg"},
		},
	})
	chats := s.chats(0)
	if len(chats) != 1 {
		t.Fatalf("expected one chat, got %d", len(chats))
	}
	// The viewer is identified by the row, the title falls back to the other person, and a blank name never wins.
	if chats[0].Title != "Jeff" || chats[0].Group {
		t.Fatalf("unexpected chat: %+v", chats[0])
	}
	// The picture is served through the media call, so the chat carries an id rather than a Facebook url.
	if chats[0].AvatarURL != "avatar:11" {
		t.Fatalf("a one-to-one should borrow the other person's picture: %+v", chats[0])
	}
	if source, _, ok := s.mediaSource("avatar:11"); !ok || source != "https://example.test/jeff.jpg" {
		t.Fatalf("the picture url should be resolvable for download: %q", source)
	}
	var viewer, other viewParticipant
	for _, person := range chats[0].Participants {
		if person.Self {
			viewer = person
		} else {
			other = person
		}
	}
	if viewer.Name != "Me" || other.Name != "Jeff" {
		t.Fatalf("unexpected participants: %+v", chats[0].Participants)
	}
}

func TestStoreBumpsThreadOnNewerMessage(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 1, LastActivityTimestampMs: 10, Snippet: "old"}}})
	s.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 1, TimestampMs: 20, Text: "new"}}})
	chats := s.chats(0)
	if len(chats) != 1 || chats[0].Preview != "new" || chats[0].At.UnixMilli() != 20 {
		t.Fatalf("expected the thread to move up: %+v", chats)
	}
}

func TestStoreHistoryIsOldestFirstAndMarksOutgoing(t *testing.T) {
	s := newStore()
	s.setSelf(100)
	s.apply(&table.LSTable{
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{{Id: 200, Name: "Alex"}},
		LSUpsertMessage: []*table.LSUpsertMessage{
			{ThreadKey: 7, MessageId: "b", SenderId: 100, Text: "mine", TimestampMs: 200},
			{ThreadKey: 7, MessageId: "a", SenderId: 200, Text: "theirs", TimestampMs: 100, ReplySourceId: "z"},
			{ThreadKey: 9, MessageId: "c", SenderId: 200, Text: "other chat", TimestampMs: 300},
		},
	})
	history := s.history(7, 0)
	if len(history) != 2 || history[0].ID != "a" || history[1].ID != "b" {
		t.Fatalf("expected oldest first for one conversation: %+v", history)
	}
	if history[0].Outgoing || history[0].Name != "Alex" || history[0].ReplyTo != "z" || history[0].ChatID != "7" {
		t.Fatalf("unexpected incoming message: %+v", history[0])
	}
	if !history[1].Outgoing {
		t.Fatalf("expected the viewer's own message to be outgoing: %+v", history[1])
	}
	// A repeated row updates in place rather than duplicating.
	s.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 7, MessageId: "b", SenderId: 100, Text: "edited", TimestampMs: 200}}})
	if history = s.history(7, 0); len(history) != 2 || history[1].Text != "edited" {
		t.Fatalf("expected an update, not a duplicate: %+v", history)
	}
	if got := s.history(7, 1); len(got) != 1 || got[0].ID != "b" {
		t.Fatalf("a limit should keep the newest: %+v", got)
	}
}

func TestStoreClassifiesAttachmentsByTypeNotPlayableUrl(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "m1", TimestampMs: 10}},
		LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{
			// Facebook gives photos a playable url too; that must not make them videos.
			{MessageId: "m1", AttachmentFbid: "a1", AttachmentType: table.AttachmentTypeImage, AttachmentMimeType: "image/jpeg",
				PreviewUrl: "https://example.test/photo.jpg", PlayableUrl: "https://example.test/photo-playable.jpg",
				PreviewWidth: 100, PreviewHeight: 50},
			{MessageId: "m1", AttachmentFbid: "a2", AttachmentType: table.AttachmentTypeVideo, AttachmentMimeType: "video/mp4",
				PreviewUrl: "https://example.test/thumb.jpg", PlayableUrl: "https://example.test/clip.mp4"},
		},
	})
	media := s.history(4, 0)[0].Media
	if len(media) != 2 {
		t.Fatalf("expected both attachments: %+v", media)
	}
	photo, video := media[0], media[1]
	if photo.Video || photo.Mime != "image/jpeg" || photo.Width != 100 || photo.Height != 50 {
		t.Fatalf("photo misclassified: %+v", photo)
	}
	if !video.Video || video.Mime != "video/mp4" {
		t.Fatalf("video misclassified: %+v", video)
	}
	// Both fetch the real thing and keep the smaller copy separately: Facebook's preview url is a thumbnail, and
	// serving it as the photograph is what made an opened picture look soft.
	if source, _, _ := s.mediaSource("a1"); source != "https://example.test/photo-playable.jpg" {
		t.Fatalf("unexpected photo source: %q", source)
	}
	if source, _, _ := s.mediaSource("a2"); source != "https://example.test/clip.mp4" {
		t.Fatalf("unexpected video source: %q", source)
	}
	if video.Preview != "a2:preview" {
		t.Fatalf("a video should carry its still: %+v", video)
	}
	if source, _, _ := s.mediaSource("a2:preview"); source != "https://example.test/thumb.jpg" {
		t.Fatalf("unexpected video still: %q", source)
	}
	if photo.Preview != "a1:preview" {
		t.Fatalf("a photo should keep its smaller copy for drawing in the conversation: %+v", photo)
	}
	if source, _, _ := s.mediaSource("a1:preview"); source != "https://example.test/photo.jpg" {
		t.Fatalf("unexpected photo thumbnail: %q", source)
	}
}

func TestStoreMarksGifsFromTheAutoplayFlag(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "m1", TimestampMs: 10}},
		LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{
			{MessageId: "m1", AttachmentFbid: "a1", AttachmentType: table.AttachmentTypeVideo, AttachmentMimeType: "video/mp4",
				PreviewUrl: "https://example.test/still.jpg", PlayableUrl: "https://example.test/cat.mp4"},
		},
		// Facebook flags the gif separately, and the row can arrive in the same table or a later one.
		LSUpdateExtraAttachmentColumns: []*table.LSUpdateExtraAttachmentColumns{
			{MessageID: "m1", AttachmentFBID: "a1", ShouldAutoplayVideo: true},
		},
	})
	media := s.history(4, 0)[0].Media
	if len(media) != 1 || !media[0].Animated || !media[0].Video {
		t.Fatalf("expected an animated attachment: %+v", media)
	}
	// Looping plays the clip, not the still.
	if source, _, _ := s.mediaSource("a1"); source != "https://example.test/cat.mp4" {
		t.Fatalf("a gif should play its clip: %q", source)
	}
}

func TestStoreTakesGifsFromThePicker(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "m2", TimestampMs: 10}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{MessageId: "m2", AttachmentFbid: "x1", PlayableUrl: "https://example.test/picked.mp4",
				PreviewUrl: "https://example.test/picked.jpg", ShouldAutoplayVideo: true},
			// A row with a picture and nothing to play is a share or a link preview, and is drawn as its picture.
			{MessageId: "m2", AttachmentFbid: "x2", PreviewUrl: "https://example.test/link.jpg"},
		},
	})
	media := s.history(4, 0)[0].Media
	if len(media) != 2 {
		t.Fatalf("the picked gif and the preview should both be there: %+v", media)
	}
	if media[0].ID != "x1" || !media[0].Animated {
		t.Errorf("the picked gif should still animate: %+v", media[0])
	}
	if media[1].ID != "x2" || media[1].Animated || media[1].Video {
		t.Errorf("a preview has nothing to play: %+v", media[1])
	}
}

func TestStoreGivesGroupsAParticipantPicture(t *testing.T) {
	s := newStore()
	s.setSelf(1)
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread:        []*table.LSDeleteThenInsertThread{{ThreadKey: 2, LastActivityTimestampMs: 5, ThreadType: table.GROUP_THREAD}},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{{ThreadKey: 2, ContactId: 1}, {ThreadKey: 2, ContactId: 2}, {ThreadKey: 2, ContactId: 3}},
		LSVerifyContactRowExists: []*table.LSVerifyContactRowExists{
			{ContactId: 1, Name: "Me", ProfilePictureUrl: "https://example.test/me.jpg", IsSelf: true},
			{ContactId: 3, Name: "Alex", ProfilePictureUrl: "https://example.test/alex.jpg"},
		},
	})
	// A group leaves its picture empty so the app can composite the members shown on the participants.
	chat := s.chats(0)[0]
	if chat.AvatarURL != "" {
		t.Fatalf("a group should not borrow one member's picture: %q", chat.AvatarURL)
	}
	var member viewParticipant
	for _, person := range chat.Participants {
		if person.ID == "3" {
			member = person
		}
	}
	if member.AvatarURL != "avatar:3" {
		t.Fatalf("members should carry their own pictures: %+v", chat.Participants)
	}
}

func TestStoreLimitsAndSkipsEmptyThreads(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{{ThreadKey: 5, ContactId: 1}}})
	if got := s.chats(0); len(got) != 0 {
		t.Fatalf("a thread with no activity should not be listed: %+v", got)
	}
	s.apply(&table.LSTable{LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
		{ThreadKey: 5, LastActivityTimestampMs: 1}, {ThreadKey: 6, LastActivityTimestampMs: 2},
	}})
	if got := s.chats(1); len(got) != 1 || got[0].ID != "6" {
		t.Fatalf("expected only the newest chat: %+v", got)
	}
}

// A shared reel or post has no playable url — there is nothing to play in the conversation — but it does have a
// picture of itself and somewhere it leads. Skipping those left the message showing nothing, and a message that was
// only a shared reel dropped out of the conversation altogether as a row with no content.
func TestSharedReelKeepsItsThumbnailAndLink(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "m3", TimestampMs: 10}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{MessageId: "m3", AttachmentFbid: "r1", PreviewUrl: "https://example.test/reel.jpg",
				ActionUrl: "https://example.test/reel", AccessibilitySummaryText: "A reel", PreviewWidth: 9, PreviewHeight: 16},
		},
	})
	page := s.history(4, 0)
	if len(page) != 1 {
		t.Fatalf("the message should be in the conversation, got %d rows", len(page))
	}
	media := page[0].Media
	if len(media) != 1 || media[0].ID != "r1" {
		t.Fatalf("the share should be drawn as one attachment: %+v", media)
	}
	if media[0].Link != "https://example.test/reel" {
		t.Errorf("the share should carry where it leads, got %q", media[0].Link)
	}
	if media[0].Video || media[0].Animated {
		t.Errorf("there is nothing to play in a share: %+v", media[0])
	}
	if media[0].Name != "A reel" {
		t.Errorf("name = %q", media[0].Name)
	}
}

// A share carries its picture and nothing else: the address is the message text, which the app draws as a link. So a
// row with a picture is drawn even with nowhere of its own to go, and a row with neither has nothing to draw.
func TestShareNeedsOnlyItsPicture(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "m4", TimestampMs: 10, Text: "look"}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{MessageId: "m4", AttachmentFbid: "n1", PreviewUrl: "https://example.test/only.jpg"},
			{MessageId: "m4", AttachmentFbid: "n2", ActionUrl: "https://example.test/only"},
		},
	})
	media := s.history(4, 0)[0].Media
	if len(media) != 1 || media[0].ID != "n1" {
		t.Fatalf("the row with a picture should be drawn and the one without should not: %+v", media)
	}
	if media[0].Link != "" {
		t.Errorf("a share with no action url of its own carries no link: %q", media[0].Link)
	}
}

// Item content belongs directly to its message. This fixture happens to use matching attachment IDs, but rendering
// must not depend on that relationship (shares_test.go covers different/missing IDs and separate batches).
func TestShareTakesItsPictureFromTheItemRow(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: "m5", TimestampMs: 10}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{ThreadKey: 4, MessageId: "m5", AttachmentFbid: "s1", AttachmentType: table.AttachmentTypeXMA},
		},
		LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{
			{ThreadKey: 4, MessageId: "m5", AttachmentFbid: "s1", TitleText: "A reel",
				PreviewUrl: "https://example.test/reel.jpg", DefaultActionUrl: "https://example.test/reel"},
		},
	})
	page := s.history(4, 0)
	if len(page) != 1 {
		t.Fatalf("the message should be in the conversation, got %d rows", len(page))
	}
	media := page[0].Media
	if len(media) != 1 {
		t.Fatalf("the share should be drawn from its item row: %+v", media)
	}
	if media[0].Link != "https://example.test/reel" {
		t.Errorf("the address is on the item row, got %q", media[0].Link)
	}
	if media[0].Name != "A reel" {
		t.Errorf("so is the title, got %q", media[0].Name)
	}
}

// Encrypted times are whole seconds, and the next page asks for what is strictly older than this page's oldest time.
// A message sharing that second but left just outside the page was on neither page: here the 50th and 51st newest
// arrived together, as a photo set straddling the boundary does, and Load earlier never showed the 51st.
func TestHistoryPageNeverSplitsOneMoment(t *testing.T) {
	s := newStore()
	const total = 51
	for i := 0; i < total; i++ {
		at := int64(1000+i) * 1000
		if i == 0 {
			at = 1001 * 1000
		}
		s.addEncrypted(7, fmt.Sprintf("m%03d", i), 42, fmt.Sprintf("synthetic %d", i), "", at)
	}
	first := s.historyBefore(7, 0, 50)
	if len(first) != total {
		t.Fatalf("the newest page held %d messages; it must grow over the second it would have split", len(first))
	}
	second := s.historyBefore(7, first[0].At.UnixMilli(), 50)
	seen := map[string]bool{}
	for _, page := range [][]viewMessage{first, second} {
		for _, message := range page {
			seen[message.ID] = true
		}
	}
	if len(seen) != total {
		t.Fatalf("two pages showed %d of %d messages", len(seen), total)
	}
	// A boundary between two different seconds is left exactly where it was.
	if got := s.historyBefore(7, 0, 40); len(got) != 40 {
		t.Fatalf("an unsplit boundary moved the page to %d messages", len(got))
	}
}

func TestPhotoOnlyMessageNamesItselfInTheList(t *testing.T) {
	s := newStore()
	s.apply(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: 7, Snippet: "earlier words", LastActivityTimestampMs: 1000, ThreadType: table.GROUP_THREAD}},
	})
	photo := func(id string, at int64, text string) *table.LSTable {
		return &table.LSTable{
			LSInsertMessage: []*table.LSInsertMessage{{ThreadKey: 7, MessageId: id, SenderId: 5, Text: text, TimestampMs: at}},
			LSInsertBlobAttachment: []*table.LSInsertBlobAttachment{{ThreadKey: 7, MessageId: id, AttachmentFbid: id + "-a",
				AttachmentType: table.AttachmentTypeImage, PreviewUrl: "https://example.test/" + id + ".jpg"}},
		}
	}
	s.apply(photo("m1", 2000, ""))
	if got := s.threads[7].snippet; got != "Photo" {
		t.Fatalf("a photo with no words left the list saying %q", got)
	}
	// A captioned photo keeps its caption, and an older photo arriving late is not the newest message.
	s.apply(photo("m2", 3000, "look"))
	if got := s.threads[7].snippet; got != "look" {
		t.Fatalf("a captioned photo said %q", got)
	}
	s.apply(photo("m0", 1500, ""))
	if got := s.threads[7].snippet; got != "look" {
		t.Fatalf("a late photo changed the line to %q", got)
	}
	if got := blobLabel(table.AttachmentTypeVideo, "", false, true, false); got != "Video" {
		t.Fatalf("a video said %q", got)
	}
	if got := blobLabel(table.AttachmentTypeFile, "notes.pdf", false, false, false); got != "notes.pdf" {
		t.Fatalf("a file said %q", got)
	}
}
