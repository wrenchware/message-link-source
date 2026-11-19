package main

import (
	"context"
	"encoding/json"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
)

// The first methods that answer with conversation content rather than counts: stage 5 draws these in the app.
// Diagnostics do not change — history-status.log still carries only fixed codes, states and numbers — and nothing
// here writes to disk or touches the account. Reading a thread is a GraphQL read; marking it read is a different
// method that this helper still does not implement, so opening a conversation here cannot mark it read on the phone.
const chatFreshness = 30 * time.Second

// How far back the conversation list pages before the app is given what there is. Instagram's own folders are small
// enough that stage 3 exhausted all three at 86 threads, so this is a ceiling rather than an expected cost.
const chatPages = 20

type uiPerson struct {
	// The messaging FBID, because that is what a message's sender carries. The Instagram id is a different number and
	// mixing them was the identity mistake stage 3 went looking for.
	ID       string `json:"id"`
	Name     string `json:"name"`
	Self     bool   `json:"self,omitempty"`
	Avatar   string `json:"avatar,omitempty"`
	Username string `json:"username,omitempty"`
}

type uiChat struct {
	ID           string     `json:"id"`
	Title        string     `json:"title"`
	Participants []uiPerson `json:"participants"`
	Preview      string     `json:"preview,omitempty"`
	At           int64      `json:"at"`
	Unread       bool       `json:"unread,omitempty"`
	// This account's read watermark (Unix ms): the app draws "New messages" above what came after it. Zero when
	// Instagram gave no watermark for this account.
	ReadAt int64 `json:"readAt,omitempty"`
	Group  bool  `json:"group,omitempty"`
	Muted  bool  `json:"muted,omitempty"`
	Pinned bool  `json:"pinned,omitempty"`
	// "inbox", "pending" or "spam". A request is not an ordinary conversation and the app must be able to tell.
	Folder string `json:"folder"`
	// Instagram says which threads take no reply. Everything this build could send is unimplemented anyway, but the
	// app should not offer a composer the provider has already refused.
	ReadOnly bool   `json:"readOnly,omitempty"`
	Avatar   string `json:"avatar,omitempty"`
	// Instagram.s own two ids for the thread, kept here and never sent to the app. Sending addresses the short one
	// and marking read the long one; they are distinct concepts and must not be collapsed. Unexported, so they
	// cannot reach the pipe by being added to a struct that is marshalled.
	shortID string
	longID  string
	// The numeric thread, which is what Instagram's own web client puts in the referer when it uploads a file.
	threadKey int64
	// Each other person's Instagram user id, to the messaging id the app knows them by. Typing reports name the sender
	// by the Instagram id alone, and this is how upstream resolves it (igconnector saveThreadMappings, keyed by
	// User.ID). Unexported like the thread ids: the Instagram id is never sent to the app.
	igUsers map[string]string
	// How far each other person has read, from the watermarks the thread already carries. The app decides from these
	// who has read which message, so nothing extra is asked of Instagram for them.
	Reads []uiRead `json:"reads,omitempty"`
	// Other people in the thread with no messaging id. No read position can name them, so the app must not take the
	// people it was given for the whole conversation and call a group read before they have.
	Unnamed int `json:"unnamed,omitempty"`
	// The newest message, which the list answer already carries, so the app can tell that something arrived and say
	// what without reading the conversation again. Never stored by the app. See lastPreview for why it has no pictures.
	Last *uiMessage `json:"last,omitempty"`
}

// One other person's read position: the time of the newest message they have read. Instagram reports it per
// participant, and upstream reads it the same way — as covering every message at or before that moment
// (igconnector/handlemeta.go dispatchRead). It is a time rather than a message, so it says who has read a message
// and not which message somebody stopped at.
type uiRead struct {
	Person string `json:"person"`
	At     int64  `json:"at"`
}

type uiReaction struct {
	Key    string `json:"key"`
	Sender string `json:"sender"`
}

type uiMessage struct {
	ID       string `json:"id"`
	ChatID   string `json:"chatId"`
	Sender   string `json:"sender"`
	Name     string `json:"name,omitempty"`
	Text     string `json:"text,omitempty"`
	At       int64  `json:"at"`
	Outgoing bool   `json:"outgoing,omitempty"`
	ReplyTo  string `json:"replyTo,omitempty"`
	// What the message actually is. Most Instagram messages are not text, and a shared reel with an empty body is
	// not an empty message — it is a thing the app has to be able to say something true about.
	Kind string `json:"kind,omitempty"`
	// A shared reel, post or profile: what it is called and where it leads. The picture is not fetched, so the link
	// is the whole of what can be offered for it.
	Title     string       `json:"title,omitempty"`
	Link      string       `json:"link,omitempty"`
	Snippet   string       `json:"snippet,omitempty"`
	Media     []uiMedia    `json:"media,omitempty"`
	Reactions []uiReaction `json:"reactions,omitempty"`
	EditedAt  int64        `json:"editedAt,omitempty"`
	// An admin notice that only records a reaction. The reaction already shows on the message it was made to, so a
	// line of its own would say it twice.
	ReactionLog bool `json:"reactionLog,omitempty"`
}

type uiMedia struct {
	// An id this helper handed out, never an address. The app asks for the picture by id and the helper fetches it.
	ID string `json:"id"`
	// The smaller copy, drawn in the conversation. Opening the picture asks for the full one instead.
	Preview string `json:"preview,omitempty"`
	Video   bool   `json:"video,omitempty"`
	// Stands for something that plays, without being a clip this build can play: a shared reel or post.
	Play   bool `json:"play,omitempty"`
	Width  int  `json:"width,omitempty"`
	Height int  `json:"height,omitempty"`
	// A voice message: a recording, fetched only when it is played. Duration is Instagram's own, in milliseconds, so the
	// length shows before anything is downloaded.
	Audio    bool `json:"audio,omitempty"`
	Duration int  `json:"duration,omitempty"`
	// A GIF: a short silent clip that loops by itself over Preview, its still, when Mime is video/mp4, and an animated
	// picture when it is image/webp.
	Gif bool `json:"gif,omitempty"`
	// A sticker, drawn as a picture.
	Sticker bool `json:"sticker,omitempty"`
	// What the address is known to serve, where Instagram's own field names it: a GIF's clip or picture, which come
	// under separate fields. Empty where nothing says, and the app then goes by the bytes.
	Mime string `json:"mime,omitempty"`
}

// Every picture a message carries, as ids. A video offers its still rather than the clip: the app draws a frame and
// this build has no player, so fetching the video itself would download a great deal for nothing.
// messageID names the pictures a message carries that have no id of their own, so they stay the same picture
// across sessions and the app.s cache can keep them.
func mapMedia(content slidetypes.MessageContent, messageID string, media *mediaStore) []uiMedia {
	var attachments []*slidetypes.Attachment
	var video bool
	switch actual := content.(type) {
	case *slidetypes.MessageContentImage:
		attachments = actual.Attachments
	case *slidetypes.MessageContentVideo:
		attachments, video = actual.Videos, true
	case *slidetypes.MessageContentXMA:
		// A shared reel or post has a preview image rather than an attachment, and no id of its own.
		if actual.XMA == nil {
			return nil
		}
		address, width, height := sharePreview(actual.XMA)
		if address == "" {
			return nil
		}
		id := media.remember("preview:"+messageID, address)
		if id == "" {
			return nil
		}
		// A picture, not a video. A share carries only a thumbnail of something that lives on Instagram — there is no
		// clip here to play — and calling it a video sent it down the app's video path, which draws an empty play
		// button when it has no still to put behind it. Play says what it stands for without claiming to be one.
		return []uiMedia{{ID: id, Width: width, Height: height, Play: true}}
	case *slidetypes.MessageContentAudio:
		return mapVoice(actual, messageID, media)
	case *slidetypes.MessageContentAnimatedMedia:
		return mapAnimated(actual, messageID, media)
	case *slidetypes.MessageContentSticker:
		// One picture and nothing else: preview_url, preview_width, preview_height, the fields upstream reuploads a
		// sticker from (igconv stickerReuploadParams). No id of its own, so it is named after its message.
		if actual.PreviewURL == "" {
			return nil
		}
		id := media.remember("sticker:"+messageID, actual.PreviewURL)
		if id == "" {
			return nil
		}
		return []uiMedia{{ID: id, Sticker: true, Width: actual.PreviewWidth, Height: actual.PreviewHeight}}
	default:
		return nil
	}
	result := make([]uiMedia, 0, len(attachments))
	for _, attachment := range attachments {
		if attachment == nil {
			continue
		}
		full := attachment.AttachmentCDNURL
		if full == "" {
			full = attachment.AttachmentCDNFallbackURL
		}
		preview := attachment.PreviewCDNURL
		if preview == "" {
			preview = attachment.PreviewCDNFallbackURL
		}
		// A video has no still of its own to open, so its preview stands for the whole of it.
		if video && full == "" {
			full = preview
		}
		id := media.remember(attachment.AttachmentFBID, full)
		if id == "" {
			// Nothing addressable: a picture with neither copy is not reported as one that failed to load.
			continue
		}
		item := uiMedia{ID: id, Video: video, Play: video, Width: attachment.PreviewWidth, Height: attachment.PreviewHeight}
		if preview != "" && preview != full {
			item.Preview = media.remember(previewID(attachment.AttachmentFBID, messageID), preview)
		}
		result = append(result, item)
	}
	if len(result) == 0 {
		return nil
	}
	return result
}

// A share's picture: the first address present, the primary one before its fallback, the XMA preview before the
// plain one. A share whose primary address is missing still carries the fallback Instagram's own client would use,
// and without it the card was drawn with no picture at all. The size is the one given beside the address used.
func sharePreview(xma *slidetypes.XMAContent) (address string, width, height int) {
	for _, preview := range []*slidetypes.XMAPreviewImage{xma.XMAPreviewImage, xma.PreviewImage} {
		if preview == nil {
			continue
		}
		for _, candidate := range []string{preview.URL, preview.FallbackURL} {
			if candidate != "" {
				return candidate, preview.Width, preview.Height
			}
		}
	}
	return "", 0, 0
}

// A voice message's recordings. The fields are the ones upstream reuploads one from (igconv audioReuploadParams):
// audio_attachments[].attachment_fbid names it, attachment_cdn_url is where it is and playable_duration_ms how long it
// runs. The waveform Instagram sends is not used: its shape has not been seen, and the app draws its own once the
// recording has been played. A recording with no address is not reported, so the message keeps its words instead.
func mapVoice(content *slidetypes.MessageContentAudio, messageID string, media *mediaStore) []uiMedia {
	var result []uiMedia
	for index, recording := range content.AudioAttachments {
		if recording == nil || recording.AttachmentCDNURL == "" {
			continue
		}
		known := recording.AttachmentFBID
		if known == "" {
			known = partID("voice:", messageID, index)
		}
		id := media.remember(known, recording.AttachmentCDNURL)
		if id == "" {
			continue
		}
		duration := recording.PlayableDurationMS
		if duration < 0 {
			duration = 0
		}
		result = append(result, uiMedia{ID: id, Audio: true, Duration: duration})
	}
	return result
}

// GIFs and animated stickers. Each carries up to three addresses — attachment_mp4_url, attachment_webp_url and
// preview_cdn_url — and is_sticker, and the choice between them is upstream's (igconv animatedMediaReuploadParams): a
// sticker, or anything with no clip, is its WebP picture; otherwise the clip, which loops silently over its still;
// with neither, the still alone as an ordinary picture. None has an id of its own, so each is named after its message
// and its place in it.
func mapAnimated(content *slidetypes.MessageContentAnimatedMedia, messageID string, media *mediaStore) []uiMedia {
	var result []uiMedia
	for index, item := range content.AnimatedMedia {
		if item == nil {
			continue
		}
		mapped := uiMedia{Width: item.PreviewWidth, Height: item.PreviewHeight}
		switch {
		case item.AttachmentWebpURL != "" && (item.IsSticker || item.AttachmentMP4URL == ""):
			mapped.ID = media.remember(partID("animated:", messageID, index), item.AttachmentWebpURL)
			mapped.Sticker, mapped.Gif, mapped.Mime = item.IsSticker, !item.IsSticker, "image/webp"
		case item.AttachmentMP4URL != "":
			mapped.ID = media.remember(partID("animated:", messageID, index), item.AttachmentMP4URL)
			mapped.Gif, mapped.Mime = true, "video/mp4"
			if item.PreviewCDNURL != "" {
				mapped.Preview = media.remember(partID("animated-still:", messageID, index), item.PreviewCDNURL)
			}
		case item.PreviewCDNURL != "":
			mapped.ID = media.remember(partID("animated-still:", messageID, index), item.PreviewCDNURL)
		}
		if mapped.ID == "" {
			continue
		}
		result = append(result, mapped)
	}
	return result
}

// A name for the n-th picture or recording of a message that gives it no id: stable across sessions, so the app's cache
// keeps it, and different for each part of one message.
func partID(kind, messageID string, index int) string {
	if messageID == "" {
		return ""
	}
	return kind + messageID + ":" + strconv.Itoa(index)
}

// What Instagram calls a message's content, as something the app can reason about. The names come from
// slidetypes.MessageContentWrapper's own switch, so an unmapped one is a new upstream type rather than a guess.
func messageKind(typeName string) string {
	switch typeName {
	case "SlideMessageText":
		return "text"
	case "SlideMessageAdminText":
		return "admin"
	case "SlideMessageImageContent", "SlideMessageRavenImageContent":
		return "photo"
	case "SlideMessageVideosContent", "SlideMessageRavenVideoContent":
		return "video"
	case "SlideMessageAudiosContent":
		return "voice"
	case "SlideMessageAnimatedMediaContent":
		return "gif"
	case "SlideMessageMultiMediaContent":
		return "media"
	case "SlideMessageStoreStickerContent", "SlideMessageAvatarStickerXMAContent",
		"SlideMessageCutoutStickerXMAContent", "SlideMessageAIStickerXMAContent":
		return "sticker"
	case "SlideMessageMusicStickerXMAContent":
		return "music"
	case "SlideMessageXMAContent":
		return "share"
	case "SlideMessageGenAIRichResponseXMAContent", "SlideMessageGenAISearchPluginResponseXMAContent":
		return "ai"
	case "":
		return ""
	default:
		return "unknown"
	}
}

// A shared reel or post carries its own title and destination. Several fields hold the title depending on what was
// shared, so the first one present wins rather than any single one being trusted.
func shareDetail(content slidetypes.MessageContent) (title, link string) {
	xma, ok := content.(*slidetypes.MessageContentXMA)
	if !ok || xma.XMA == nil {
		return "", ""
	}
	for _, candidate := range []string{xma.XMA.XMATitle, xma.XMA.TitleText, xma.XMA.HeaderTitleText, xma.XMA.CaptionBodyText, xma.XMA.SubtitleText} {
		if candidate != "" {
			title = candidate
			break
		}
	}
	return title, xma.XMA.TargetURL
}

type uiMessagePage struct {
	Messages []uiMessage `json:"messages"`
	HasMore  bool        `json:"hasMore"`
	Cursor   string      `json:"cursor,omitempty"`
	// The read positions that came with the newest page, which is read with the whole thread. An older page comes
	// alone and carries none; the conversation's own positions cover it.
	Reads []uiRead `json:"reads,omitempty"`
}

// The conversations last read from Instagram. Held in memory only: stage 4 established that Instagram serves its own
// history, so nothing here is the only copy of anything and none of it is written down by this process.
type chatStore struct {
	mu       sync.Mutex
	chats    map[string]*uiChat
	loadedAt time.Time
	// When every page was last read, as against only the first. Zero until a whole read has succeeded in this process,
	// which is what makes the first read of a new helper — a launch, a resume, a recovery — a whole one.
	sweptAt time.Time
}

func newChatStore() *chatStore { return &chatStore{chats: map[string]*uiChat{}} }

func (s *chatStore) replace(chats []*uiChat) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.chats = make(map[string]*uiChat, len(chats))
	for _, chat := range chats {
		s.chats[chat.ID] = chat
	}
	s.loadedAt = time.Now()
	s.sweptAt = s.loadedAt
}

// A whole read in which one folder failed: the held rows of that folder that the read did not find are carried over.
func (s *chatStore) replaceKeeping(chats []*uiChat, folder string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	previous := s.chats
	s.chats = make(map[string]*uiChat, len(chats))
	for _, chat := range chats {
		s.chats[chat.ID] = chat
	}
	for id, chat := range previous {
		if _, found := s.chats[id]; !found && chat.Folder == folder {
			s.chats[id] = chat
		}
	}
	s.loadedAt = time.Now()
	s.sweptAt = s.loadedAt
}

// The first page laid over what the last whole read found. Nothing is removed: a conversation below the first page is
// not gone for being absent from it, and a conversation taken away on the phone goes at the next whole read.
func (s *chatStore) merge(chats []*uiChat) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, chat := range chats {
		s.chats[chat.ID] = chat
	}
	s.loadedAt = time.Now()
}

func (s *chatStore) swept() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return !s.sweptAt.IsZero()
}

func (s *chatStore) put(chat *uiChat) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.chats[chat.ID] = chat
}

func (s *chatStore) get(id string) (*uiChat, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	chat, ok := s.chats[id]
	return chat, ok
}

// A conversation by Instagram's long thread id, which is the only one a typing report carries. Upstream resolves it
// the same way, from the long id to the thread (igconnector handleTyping, GetFBIDForIGThread).
func (s *chatStore) byLongID(long string) (*uiChat, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, chat := range s.chats {
		if chat.longID != "" && chat.longID == long {
			return chat, true
		}
	}
	return nil, false
}

// Newest first, which is the order the list is drawn in and therefore the order worth deciding here rather than in
// three different callers.
func (s *chatStore) list(limit int) []*uiChat {
	s.mu.Lock()
	defer s.mu.Unlock()
	result := make([]*uiChat, 0, len(s.chats))
	for _, chat := range s.chats {
		result = append(result, chat)
	}
	sort.SliceStable(result, func(a, b int) bool {
		if result[a].At != result[b].At {
			return result[a].At > result[b].At
		}
		return result[a].ID < result[b].ID
	})
	if limit > 0 && len(result) > limit {
		result = result[:limit]
	}
	return result
}

func (s *chatStore) fresh() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return !s.loadedAt.IsZero() && time.Since(s.loadedAt) < chatFreshness
}

func (s *chatStore) stale() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.loadedAt = time.Time{}
}

// "inbox" is the one folder that allows sends and read marks, so it is given only on evidence: the thread was listed
// from the inbox, or Instagram named the folder itself. Anything else is "" (unknown), which every inbox check
// refuses. Defaulting to "inbox" let a message request opened from New chat be answered and marked read without
// anybody accepting it.
func folderName(thread *slidetypes.ThreadInfo, queried string) string {
	switch {
	case thread.SystemFolder == "PENDING" || queried == "PENDING":
		return "pending"
	case thread.SystemFolder == "SPAM" || queried == "SPAM":
		return "spam"
	case thread.SystemFolder == "INBOX" || queried == "INBOX":
		return "inbox"
	default:
		return ""
	}
}

// The folder a detail read leaves a conversation in. A folder the detail names is the newer fact; one it does not name
// keeps what the list established, so a request or a spam thread is not promoted by being opened.
func detailFolder(chat, known *uiChat, found bool) {
	if chat.Folder == "" && found && known.Folder != "" {
		chat.Folder = known.Folder
	}
}

// A direct conversation Instagram gives no title to is named after the person in it, the way every other provider
// here names one. Falling back to the username rather than leaving it blank matters because an account with no full
// name set is common on Instagram and an untitled row is unusable.
func threadTitle(thread *slidetypes.ThreadInfo, people []uiPerson) string {
	if thread.ThreadTitle != "" {
		return thread.ThreadTitle
	}
	for _, person := range people {
		if !person.Self && person.Name != "" {
			return person.Name
		}
	}
	return ""
}

func personName(user *slidetypes.User) string {
	if user == nil {
		return ""
	}
	if user.FullName != "" {
		return user.FullName
	}
	return user.Username
}

func mapPeople(thread *slidetypes.ThreadInfo, self int64, media *mediaStore) []uiPerson {
	people := make([]uiPerson, 0, len(thread.Users))
	for _, user := range thread.Users {
		if user == nil || user.InteropMessagingUserFBID == 0 {
			continue
		}
		people = append(people, uiPerson{
			ID:       strconv.FormatInt(user.InteropMessagingUserFBID, 10),
			Name:     personName(user),
			Username: user.Username,
			Self:     user.InteropMessagingUserFBID == self,
			// Named by the person, not by the address. Instagram.s picture urls are signed and rotate, so a digest of
			// the address is a different id on every read and the app.s cache could never hit: every launch
			// downloaded every avatar again. The id is stable while the account is; the address behind it is
			// replaced each time the conversation is read.
			Avatar: media.remember("avatar:"+strconv.FormatInt(user.InteropMessagingUserFBID, 10), user.ProfilePicURL),
		})
	}
	return people
}

// Everybody else in the thread by Instagram user id, to their messaging id. Only people with both: somebody Instagram
// gives no messaging id cannot be named in the app, so a typing report from them is not shown rather than guessed at.
// This account is left out, so its own typing on another device can never come back as somebody else's.
func instagramUsers(thread *slidetypes.ThreadInfo, self int64) map[string]string {
	users := map[string]string{}
	for _, user := range thread.Users {
		if user == nil || user.ID == "" || user.InteropMessagingUserFBID == 0 || user.InteropMessagingUserFBID == self {
			continue
		}
		users[user.ID] = strconv.FormatInt(user.InteropMessagingUserFBID, 10)
	}
	return users
}

// Whether anything has arrived since this account last read the conversation. Instagram reports a watermark per
// participant, so this is the provider's own answer rather than a guess: the viewer's watermark against the newest
// message. A conversation the user marked unread stays unread regardless.
func threadUnread(thread *slidetypes.ThreadInfo, self int64, latest int64) bool {
	if thread.MarkedAsUnread {
		return true
	}
	if latest == 0 {
		return false
	}
	if watermark, found := selfWatermark(thread, self); found {
		return watermark < latest
	}
	// No watermark for this account is not evidence of unread messages. Unknown stays unavailable.
	return false
}

// This account's read watermark in Unix ms, and whether Instagram gave one.
func selfWatermark(thread *slidetypes.ThreadInfo, self int64) (int64, bool) {
	for _, receipt := range thread.SlideReadReceipts {
		if receipt.ParticipantFBID == self {
			return receipt.WatermarkTimestampMS.UnixMilli(), true
		}
	}
	return 0, false
}

// Everybody else's watermark, one each and the furthest where Instagram repeats a person. This account's own is left
// out: it decides Unread above and is never a reader of anything. A person with no watermark has read nothing that
// can be claimed, so they are simply absent.
func threadReads(thread *slidetypes.ThreadInfo, self int64) []uiRead {
	furthest := map[int64]int64{}
	for _, receipt := range thread.SlideReadReceipts {
		at := receipt.WatermarkTimestampMS.UnixMilli()
		if receipt.ParticipantFBID <= 0 || receipt.ParticipantFBID == self || at <= 0 {
			continue
		}
		if at > furthest[receipt.ParticipantFBID] {
			furthest[receipt.ParticipantFBID] = at
		}
	}
	if len(furthest) == 0 {
		return nil
	}
	reads := make([]uiRead, 0, len(furthest))
	for person, at := range furthest {
		reads = append(reads, uiRead{Person: strconv.FormatInt(person, 10), At: at})
	}
	sort.Slice(reads, func(a, b int) bool { return reads[a].Person < reads[b].Person })
	return reads
}

// Other people mapPeople had to leave out for want of a messaging id. The viewer is not one of them however it is
// listed: Instagram does not reliably give this account's own record a messaging id, and it is nobody's reader.
func unnamedPeople(thread *slidetypes.ThreadInfo) int {
	count := 0
	for _, user := range thread.Users {
		if user == nil || user.InteropMessagingUserFBID != 0 {
			continue
		}
		if user.ID != "" && (user.ID == thread.ViewerID || thread.Viewer != nil && user.ID == thread.Viewer.ID) ||
			user.PK != "" && thread.Viewer != nil && user.PK == thread.Viewer.PK {
			continue
		}
		count++
	}
	return count
}

func lastMessage(thread *slidetypes.ThreadInfo) *slidetypes.Message {
	if thread.SlideMessages == nil {
		return nil
	}
	var newest *slidetypes.Message
	for _, edge := range thread.SlideMessages.Edges {
		if edge.Node == nil {
			continue
		}
		if newest == nil || edge.Node.TimestampMS.UnixMilli() > newest.TimestampMS.UnixMilli() {
			newest = edge.Node
		}
	}
	return newest
}

func mapThread(thread *slidetypes.ThreadInfo, self int64, queried string, media *mediaStore) *uiChat {
	people := mapPeople(thread, self, media)
	chat := &uiChat{
		ID:           thread.ID,
		Title:        threadTitle(thread, people),
		Participants: people,
		At:           thread.LastActivityTimestampMS.UnixMilli(),
		Group:        thread.IsGroup,
		Folder:       folderName(thread, queried),
		shortID:      thread.ThreadFBID,
		threadKey:    thread.ThreadKey,
		longID:       thread.ThreadID,
		Avatar:       threadPicture(thread, people, media),
		Reads:        threadReads(thread, self),
		Unnamed:      unnamedPeople(thread),
		igUsers:      instagramUsers(thread, self),
	}
	if thread.IsMuted != nil {
		chat.Muted = *thread.IsMuted
	}
	if thread.IsPin != nil {
		chat.Pinned = *thread.IsPin
	}
	if newest := lastMessage(thread); newest != nil {
		chat.Preview = newest.TextBody
		if chat.Preview == "" {
			chat.Preview = newest.IGDSnippet
		}
		if chat.At == 0 {
			chat.At = newest.TimestampMS.UnixMilli()
		}
		chat.Unread = threadUnread(thread, self, newest.TimestampMS.UnixMilli())
		if watermark, found := selfWatermark(thread, self); found && watermark > 0 {
			chat.ReadAt = watermark
		}
		chat.Last = lastPreview(newest, thread.ID, self)
		// An admin notice with no snippet has no words for the list but its own (messageBody); without them the app
		// reads the whole conversation to find a line.
		if chat.Preview == "" && chat.Last != nil && chat.Last.Kind == "admin" {
			chat.Preview = chat.Last.Text
		}
	}
	return chat
}

// The newest message as the list has it: everything but what it shows. Mapping the list's pictures would register the
// list's addresses under the same attachment ids the conversation uses — the newest address wins — and quietly replace
// the addresses a conversation read supplied with ones this read happened to carry. Nor is a share described, since
// its card is half picture. A text message is therefore complete here and any other kind is not; the kind says which,
// and the app treats it so.
func lastPreview(message *slidetypes.Message, threadID string, self int64) *uiMessage {
	if message.ID == "" {
		return nil
	}
	last := messageBody(message, threadID, self)
	return &last
}

func mapMessage(message *slidetypes.Message, threadID string, self int64, media *mediaStore) uiMessage {
	mapped := messageBody(message, threadID, self)
	mapped.Title, mapped.Link = shareDetail(message.Content.Content)
	mapped.Media = mapMedia(message.Content.Content, message.ID, media)
	return mapped
}

// A message's words, sender, time, reactions and corrections: what the list and the conversation both carry.
func messageBody(message *slidetypes.Message, threadID string, self int64) uiMessage {
	mapped := uiMessage{
		ID:       message.ID,
		ChatID:   threadID,
		Sender:   strconv.FormatInt(message.SenderFBID, 10),
		Text:     message.TextBody,
		At:       message.TimestampMS.UnixMilli(),
		Outgoing: message.SenderFBID == self,
		ReplyTo:  message.RepliedToMessageID,
		Kind:     messageKind(message.Content.TypeName),
		// Instagram's own words for what this is, which is what the conversation list already shows as a preview.
		Snippet: message.IGDSnippet,
	}
	if message.Sender != nil {
		mapped.Name = message.Sender.Name
		if mapped.Name == "" {
			mapped.Name = personName(&message.Sender.UserDict)
		}
	}
	for _, reaction := range message.Reactions {
		if reaction == nil {
			continue
		}
		mapped.Reactions = append(mapped.Reactions, uiReaction{Key: reaction.Reaction, Sender: strconv.FormatInt(reaction.SenderFBID, 10)})
	}
	// Instagram keeps the corrections beside the message; the newest one is when the words last changed.
	for _, edit := range message.SlideEditHistory {
		if at := edit.TimestampMS.UnixMilli(); at > mapped.EditedAt {
			mapped.EditedAt = at
		}
	}
	// An admin notice (a call, a theme, a nickname, a reaction) has an empty body: its words are in its fragments.
	// Without them the app had nothing to draw and showed its own placeholder, "[admin]".
	if admin, ok := message.Content.Content.(*slidetypes.MessageContentAdminText); ok {
		if mapped.Text == "" {
			mapped.Text = adminWords(admin)
		}
		mapped.ReactionLog = admin.IsReactionActionLog
	}
	return mapped
}

// The words of an admin notice, joined as upstream joins them (igconv wrapAdminText) but without the links some
// fragments carry: the app draws the notice as a line of plain words.
func adminWords(content *slidetypes.MessageContentAdminText) string {
	var words strings.Builder
	for _, fragment := range content.TextFragments {
		words.WriteString(fragment.Plaintext)
	}
	return strings.TrimSpace(words.String())
}

// Reads every folder the source models. PENDING and SPAM are queried, never accepted or moved: a message request is
// something the app should be able to show without the act of looking at it deciding anything.
func (a *readOnlyAdapter) loadChats(ctx context.Context, client historyClient) (string, bool) {
	self := client.GetOwnFBID()
	if self <= 0 {
		return "identity_mismatch", false
	}
	mailbox, err := client.GetMailbox(ctx)
	a.requests.note(ctx, &a.requests.mailbox, err != nil)
	if err != nil {
		return failureCode(err), false
	}
	if mailbox == nil || mailbox.Mailbox == nil {
		return "invalid_response", false
	}
	var chats []*uiChat
	seen := map[string]bool{}
	add := func(page slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]], folder string) {
		for _, edge := range page.Edges {
			thread := edge.Node.AsIGDirectThread
			if thread == nil || thread.ID == "" || seen[thread.ID] {
				continue
			}
			seen[thread.ID] = true
			chats = append(chats, mapThread(thread, self, folder, a.media))
		}
	}
	page := func(folder string, initial *slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]]) string {
		cursor := ""
		cursors := map[string]bool{}
		for number := 0; number < chatPages; number++ {
			current := slidetypes.Edged[slidetypes.Node[slidetypes.WrappedThreadInfo]]{}
			if initial != nil && number == 0 {
				current = *initial
			} else {
				if ctx.Err() != nil {
					return failureCode(ctx.Err())
				}
				response, err := client.PaginateMailbox(ctx, slidetypes.MakePaginateMailboxRequest(self, cursor, folder, nil))
				a.requests.note(ctx, &a.requests.mailboxPages, err != nil)
				if err != nil {
					return failureCode(err)
				}
				if response == nil || response.Mailbox == nil {
					return "invalid_response"
				}
				current = response.Mailbox.ThreadsByFolder
			}
			add(current, folder)
			// A cursor that does not advance would page the same rows forever; stop rather than spin against Instagram.
			if !current.PageInfo.HasNextPage || current.PageInfo.EndCursor == "" || cursors[current.PageInfo.EndCursor] {
				return ""
			}
			cursor = current.PageInfo.EndCursor
			cursors[cursor] = true
		}
		return ""
	}
	if code := page("INBOX", &mailbox.Mailbox.ThreadsByFolder); code != "" {
		return code, false
	}
	// Only the folders the app actually shows. PENDING is deliberately not read here: the app does not draw message
	// requests yet, so paging that folder on every connection spent round trips on rows that were then discarded,
	// which is most of what made the first list slow. Stage 7 reads it when it has somewhere to put it.
	//
	// A folder that fails is not a reason to show no conversations at all: the inbox is already in hand.
	// The spam folder's own rows are kept when it could not be read, rather than dropped until the next whole read.
	if page("SPAM", nil) != "" {
		a.chats.replaceKeeping(chats, "spam")
	} else {
		a.chats.replace(chats)
	}
	return "", true
}

// The inbox's first page alone: the one request the mailbox query answers with, laid over the list already held.
// A conversation that gains a message rises to the top of the inbox, so this is where anything new is; what changes
// below it — a read on the phone, a message taken back in an older conversation — waits for the next whole read.
// Every poll used to page the whole inbox and the spam folder, ten requests a poll on the real account.
func (a *readOnlyAdapter) loadFirstPage(ctx context.Context, client historyClient) (string, bool) {
	self := client.GetOwnFBID()
	if self <= 0 {
		return "identity_mismatch", false
	}
	mailbox, err := client.GetMailbox(ctx)
	a.requests.note(ctx, &a.requests.mailbox, err != nil)
	if err != nil {
		return failureCode(err), false
	}
	if mailbox == nil || mailbox.Mailbox == nil {
		return "invalid_response", false
	}
	var chats []*uiChat
	seen := map[string]bool{}
	for _, edge := range mailbox.Mailbox.ThreadsByFolder.Edges {
		thread := edge.Node.AsIGDirectThread
		if thread == nil || thread.ID == "" || seen[thread.ID] {
			continue
		}
		seen[thread.ID] = true
		chats = append(chats, mapThread(thread, self, "INBOX", a.media))
	}
	a.chats.merge(chats)
	return "", true
}

// Which read a list request makes: "all" pages every folder the app shows, "first" reads the inbox's first page. An
// explicit refresh with neither is a whole read, as it always was. An ordinary read of a list gone stale — after a
// send, or after thirty seconds — reads the first page once a whole read has been made, because the conversation that
// made it stale is the one that has just risen to the top.
func (a *readOnlyAdapter) listRead(pages string, refresh bool) (func(context.Context, historyClient) (string, bool), bool) {
	switch pages {
	case "all":
		return a.loadChats, true
	case "first":
		if !a.chats.swept() {
			// Nothing whole to lay a first page over yet.
			return a.loadChats, true
		}
		return a.loadFirstPage, true
	case "":
		if refresh || !a.chats.swept() {
			return a.loadChats, true
		}
		return a.loadFirstPage, true
	}
	return nil, false
}

func (a *readOnlyAdapter) readClientFor() (historyClient, string) {
	a.mu.Lock()
	client := a.client
	verified := a.snapshot.IdentityVerified
	// Every conversation read comes through here, so this is the one place the store has to exist by.
	if a.chats == nil {
		a.chats = newChatStore()
	}
	a.mu.Unlock()
	if client == nil || !verified {
		return nil, "login_required"
	}
	history, ok := client.(historyClient)
	if !ok {
		return nil, "unsupported"
	}
	return history, ""
}

func (a *readOnlyAdapter) uiChats(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.readClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		Count   int    `json:"count"`
		Refresh bool   `json:"refresh"`
		Pages   string `json:"pages"`
	}
	if len(raw) > 0 && json.Unmarshal(raw, &params) != nil {
		return nil, "invalid_params"
	}
	if params.Count < 0 || params.Count > 1000 {
		return nil, "invalid_params"
	}
	read, known := a.listRead(params.Pages, params.Refresh)
	if !known {
		return nil, "invalid_params"
	}
	if params.Refresh || !a.chats.fresh() {
		if code, ok := read(ctx, client); !ok {
			// The app's poll stops on this answer without telling the helper, so otherwise the realtime connection went
			// on reopening the refused session, with its cookies, after the next ordinary drop. This session's only.
			if refusal(code) {
				a.mu.Lock()
				if any(a.client) == any(client) {
					a.refusedRealtime = true
				}
				a.mu.Unlock()
			}
			// A list already read is better than an error for a read that failed; only a first load has nothing.
			// Except when a refresh was asked for by name: only the app's poll does that, and the poll is what has to
			// learn that Instagram is refusing — a checkpoint or an expired session answered with yesterday's list
			// looked like a quiet inbox, and the poll went on asking every twenty seconds indefinitely.
			if params.Refresh || len(a.chats.list(1)) == 0 {
				return nil, code
			}
		}
	}
	return a.chats.list(params.Count), ""
}

func (a *readOnlyAdapter) uiChat(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.readClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID string `json:"chatId"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" {
		return nil, "invalid_params"
	}
	response, err := client.GetThread(ctx, slidetypes.MakeGetThreadInfoRequest(params.ChatID))
	a.requests.note(ctx, &a.requests.threads, err != nil)
	if err != nil {
		if known, ok := a.chats.get(params.ChatID); ok {
			return known, ""
		}
		return nil, failureCode(err)
	}
	if response == nil || response.ThreadInfo.AsIGDirectThread == nil {
		return nil, "invalid_response"
	}
	thread := response.ThreadInfo.AsIGDirectThread
	if thread.ID != params.ChatID {
		return nil, "invalid_response"
	}
	chat := mapThread(thread, client.GetOwnFBID(), "", a.media)
	known, found := a.chats.get(chat.ID)
	detailFolder(chat, known, found)
	a.chats.put(chat)
	return chat, ""
}

func (a *readOnlyAdapter) uiMessages(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.readClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		ChatID    string `json:"chatId"`
		OlderThan string `json:"olderThan"`
		Count     int    `json:"count"`
	}
	if json.Unmarshal(raw, &params) != nil || params.ChatID == "" {
		return nil, "invalid_params"
	}
	count := params.Count
	if count <= 0 {
		count = 20
	}
	if count > 100 {
		return nil, "invalid_params"
	}
	self := client.GetOwnFBID()
	var page *slidetypes.SlideMessages
	var reads []uiRead
	if params.OlderThan == "" {
		// The newest page comes with the thread itself; only paging back needs a cursor.
		response, err := client.GetThread(ctx, slidetypes.MakeGetThreadInfoRequest(params.ChatID))
		a.requests.note(ctx, &a.requests.threads, err != nil)
		if err != nil {
			return nil, failureCode(err)
		}
		if response == nil || response.ThreadInfo.AsIGDirectThread == nil {
			return nil, "invalid_response"
		}
		page = response.ThreadInfo.AsIGDirectThread.SlideMessages
		// The thread brings everybody's read position with it, and this is the freshest answer the app will have.
		reads = threadReads(response.ThreadInfo.AsIGDirectThread, self)
	} else {
		response, err := client.PaginateMessages(ctx, &slidetypes.PaginateMessagesRequest{
			AfterCursor: &params.OlderThan, ThreadID: params.ChatID, FirstN: count, InitialMessagePageCount: count,
		})
		a.requests.note(ctx, &a.requests.messagePages, err != nil)
		if err != nil {
			return nil, failureCode(err)
		}
		if response == nil || response.ThreadInfo.AsIGDirectThread == nil {
			return nil, "invalid_response"
		}
		if response.ThreadInfo.AsIGDirectThread.ID != params.ChatID {
			return nil, "invalid_response"
		}
		// A paged response carries the messages alone, not the whole thread.
		page = response.ThreadInfo.AsIGDirectThread.Messages
	}
	result := uiMessagePage{Messages: []uiMessage{}, Reads: reads}
	if page == nil {
		return result, ""
	}
	for _, edge := range page.Edges {
		if edge.Node == nil || edge.Node.ID == "" {
			continue
		}
		result.Messages = append(result.Messages, mapMessage(edge.Node, params.ChatID, self, a.media))
	}
	sort.SliceStable(result.Messages, func(a, b int) bool { return result.Messages[a].At < result.Messages[b].At })
	result.HasMore = page.PageInfo.HasNextPage && page.PageInfo.EndCursor != ""
	if result.HasMore {
		result.Cursor = page.PageInfo.EndCursor
	}
	return result, ""
}

// A smaller copy has no id of its own. Naming it after the attachment keeps it stable across sessions; without an
// attachment id the message is the next most stable thing to hang it on.
func previewID(attachmentID, messageID string) string {
	if attachmentID != "" {
		return "preview:" + attachmentID
	}
	if messageID != "" {
		return "preview:" + messageID
	}
	return ""
}

// The picture a conversation shows. Instagram gives a thread image to groups and none to a direct conversation, so a
// direct one takes the other person's — which is what it is a picture of anyway.
//
// It deliberately reuses that person's own avatar id rather than minting a thread one. The app keeps avatars on disk
// under the id the conversation supplies, and a conversation supplying nothing skipped that cache entirely and
// downloaded every picture again at every launch. Sharing the id also means one download serves the row and the
// person inside it.
func threadPicture(thread *slidetypes.ThreadInfo, people []uiPerson, media *mediaStore) string {
	if thread.ThreadImageURL != "" {
		return media.remember("avatar:thread:"+thread.ID, thread.ThreadImageURL)
	}
	if thread.IsGroup {
		// A group with no picture of its own has no single person to borrow one from.
		return ""
	}
	for _, person := range people {
		if !person.Self && person.Avatar != "" {
			return person.Avatar
		}
	}
	return ""
}
