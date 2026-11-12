package main

import "time"

// The same JSON shape the Google Messages helper returns, so the app maps both the same way.
type viewParticipant struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Self      bool   `json:"self"`
	NativeID  string `json:"nativeId"`
	AvatarURL string `json:"avatarUrl,omitempty"`
}

type viewMedia struct {
	ID     string `json:"id"`
	Name   string `json:"name,omitempty"`
	Mime   string `json:"mime,omitempty"`
	Size   int64  `json:"size,omitempty"`
	Width  int64  `json:"width,omitempty"`
	Height int64  `json:"height,omitempty"`
	Video  bool   `json:"video,omitempty"`
	// A video's still image, fetched like any other media.
	Preview string `json:"preview,omitempty"`
	// Where a shared reel, post or link leads. The picture is the thumbnail; opening it is the browser's job.
	Link string `json:"link,omitempty"`
	// A shared reel or video: the picture gets a play badge, and following it opens where it leads.
	Play bool `json:"play,omitempty"`
	// A gif: it plays by itself, on a loop, with no controls.
	Animated bool `json:"animated,omitempty"`
	// What a shared or linked page says about itself, for the line under the title in the card.
	Description string `json:"description,omitempty"`
	// What the button on a shared card says, in Facebook's words.
	ActionTitle string `json:"actionTitle,omitempty"`
}

// Messenger has its own message shape: statuses are numbers, and senders are contact ids.
type viewMessage struct {
	ID       string    `json:"id"`
	ChatID   string    `json:"chatId"`
	Sender   string    `json:"sender"`
	Name     string    `json:"name,omitempty"`
	Text     string    `json:"text"`
	At       time.Time `json:"at"`
	Outgoing bool      `json:"outgoing"`
	Deleted  bool      `json:"deleted"`
	ReplyTo  string      `json:"replyTo,omitempty"`
	// Facebook's own line across the conversation rather than a person's message (store.go's notice).
	Notice bool `json:"notice,omitempty"`
	// A row Facebook's own phone app does not show, so neither does this one (store.go's receipt).
	Hidden bool `json:"hidden,omitempty"`
	// The buyer's Marketplace profile, on a Marketplace chat's "started this chat." line (store.go's buyerProfileLocked).
	Profile string `json:"profile,omitempty"`
	Status   int64       `json:"status"`
	Media    []viewMedia `json:"media"`
	// One per person who reacted, with the emoji they chose.
	Reactions []viewReaction `json:"reactions,omitempty"`
	// Everyone who has read this far, by contact id. The conversation draws a face for each on the newest message
	// they have read, which is the last one where they appear here and not on the one after it.
	SeenBy []string `json:"seenBy,omitempty"`
	// Those for whom this is the newest message they have read — where their face is drawn. One place each, decided
	// over the whole conversation rather than against the next message.
	ReadMark []string `json:"readMark,omitempty"`
	// When the words were last changed, for a message that has been edited.
	EditedAt int64 `json:"editedAt,omitempty"`
	// For a message this account sent: how many of the other people it has reached, reading counted as reaching.
	Delivered int `json:"delivered,omitempty"`
	// For a message this account sent: when it was read, in milliseconds, where Facebook said when and this helper saw
	// that reading happen. Zero when that is not known, which is never a guess at it.
	SeenAt int64 `json:"seenAt,omitempty"`
}

type viewReaction struct {
	Key    string `json:"key"`
	Sender string `json:"sender"`
}

type viewChat struct {
	ExpectedTransport string            `json:"expectedTransport"`
	Transport         string            `json:"transport"`
	OutgoingID        string            `json:"outgoingId"`
	PreviewOutgoing   bool              `json:"previewOutgoing"`
	ReadOnly          bool              `json:"readOnly"`
	// Moved to end-to-end encryption: its messages travel on a channel this build does not speak.
	Encrypted         bool              `json:"encrypted,omitempty"`
	ID                string            `json:"id"`
	Title             string            `json:"title"`
	Participants      []viewParticipant `json:"participants"`
	Preview           string            `json:"preview"`
	// Who wrote the preview's words in a group, as the messages name them; left out for this account and one-to-ones.
	PreviewSender     string            `json:"previewSender,omitempty"`
	PreviewName       string            `json:"previewName,omitempty"`
	At                time.Time         `json:"at"`
	Unread            bool              `json:"unread"`
	// This account's read watermark (Unix ms): the app draws "New messages" above what came after it. Zero when
	// Messenger has not said, as for Unread.
	ReadAt            int64             `json:"readAt,omitempty"`
	Group             bool              `json:"group"`
	// A Marketplace buyer and seller's chat, by Facebook's own thread kind.
	Marketplace bool `json:"marketplace,omitempty"`
	Archived          bool              `json:"archived"`
	Hidden            bool              `json:"hidden"`
	AvatarColor       string            `json:"avatarColor,omitempty"`
	AvatarURL         string            `json:"avatarUrl,omitempty"`
	// Facebook's count of a group's members, which the participants above can fall short of while their rows arrive.
	Members int `json:"members,omitempty"`
	// How this account muted the conversation in Messenger, perhaps on another device: -1 until turned off, otherwise
	// when it ends (Unix ms). Left out when it is not muted or the mute has ended. The app shows it; nothing sends one.
	MutedUntil int64 `json:"mutedUntil,omitempty"`
	// The group changes Messenger is known to take from this account for this conversation (groupstate.go). Each is
	// visible to everybody in the group, so each is offered only where the helper can say it is allowed.
	CanRename        bool `json:"canRename,omitempty"`
	CanAddMembers    bool `json:"canAddMembers,omitempty"`
	CanRemoveMembers bool `json:"canRemoveMembers,omitempty"`
}

// How many of a page's messages carry an attachment, for the log: a conversation whose pictures have gone missing
// shows as a page of messages with none.
func withMedia(page []viewMessage) int {
	total := 0
	for _, current := range page {
		if len(current.Media) > 0 {
			total++
		}
	}
	return total
}
