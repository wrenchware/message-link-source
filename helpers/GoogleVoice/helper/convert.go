package main

import (
	"fmt"
	"math"
	"slices"
	"strings"
	"time"
	"unicode/utf8"

	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// What the app is told about a conversation. Phone numbers and names are shown in the app's own window, as the
// other providers' are; nothing here is written to a log.
type voiceChat struct {
	ID           string             `json:"id"`
	Title        string             `json:"title"`
	Participants []voiceParticipant `json:"participants"`
	Unread       bool               `json:"unread"`
	LastActivity int64              `json:"lastActivity"`
	Preview      *voiceMessage      `json:"preview,omitempty"`
	Group        bool               `json:"group"`
	Archived     bool               `json:"archived"`
	Spam         bool               `json:"spam"`
}

type voiceParticipant struct {
	Number  string `json:"number"`
	Name    string `json:"name,omitempty"`
	Avatar  string `json:"avatar,omitempty"`
	Blocked bool   `json:"blocked,omitempty"`
}

type voiceMessage struct {
	ID          string            `json:"id"`
	ChatID      string            `json:"chatId"`
	Sender      string            `json:"sender,omitempty"`
	Timestamp   int64             `json:"timestamp"`
	Outgoing    bool              `json:"outgoing"`
	Kind        string            `json:"kind"`
	CallKind    string            `json:"callKind,omitempty"`
	Text        string            `json:"text,omitempty"`
	Attachments []voiceAttachment `json:"attachments,omitempty"`
}

// A voicemail's audio is asked for by this prefix and the message id; the helper remembers where Google keeps it.
const voicemailPrefix = "voicemail:"

type voiceAttachment struct {
	ID          string `json:"id"`
	Mime        string `json:"mime,omitempty"`
	Width       int32  `json:"width,omitempty"`
	Height      int32  `json:"height,omitempty"`
	Unsupported bool   `json:"unsupported,omitempty"`
	// Voicemail audio, fetched from Google only when it is played.
	Voicemail bool `json:"voicemail,omitempty"`
	Duration  int  `json:"duration,omitempty"`
}

func convertThread(thread *gvproto.Thread) voiceChat {
	return convertThreadNamed(thread, nil)
}

// A thread's participants named from the thread itself or, where it gives only a number, from the account's contacts.
func convertThreadNamed(thread *gvproto.Thread, contacts map[string]contactInfo) voiceChat {
	chat := voiceChat{ID: thread.GetID(), Unread: !thread.GetRead()}
	names := map[string]string{}
	for _, contact := range thread.GetContacts() {
		if contact.GetPhoneNumber() == "" {
			continue
		}
		name := contact.GetName()
		info := contacts[contact.GetPhoneNumber()]
		if numberOnly(name) && info.Name != "" {
			name = info.Name
		}
		chat.Participants = append(chat.Participants, voiceParticipant{
			Number: contact.GetPhoneNumber(), Name: name, Avatar: info.Avatar, Blocked: contact.GetBlocked(),
		})
		names[contact.GetPhoneNumber()] = name
	}
	for _, number := range thread.GetPhoneNumbers() {
		if _, known := names[number]; number != "" && !known {
			info := contacts[number]
			chat.Participants = append(chat.Participants, voiceParticipant{Number: number, Name: info.Name, Avatar: info.Avatar})
			names[number] = info.Name
		}
	}
	chat.Group = len(chat.Participants) > 1
	labels := make([]string, 0, len(chat.Participants))
	for _, person := range chat.Participants {
		if person.Name != "" {
			labels = append(labels, person.Name)
		} else {
			labels = append(labels, person.Number)
		}
	}
	chat.Title = strings.Join(labels, ", ")
	for _, folder := range thread.GetFolders() {
		switch folder {
		case gvproto.ThreadFolder_ALL_ARCHIVED_THREADS:
			chat.Archived = true
		case gvproto.ThreadFolder_ALL_SPAM_THREADS:
			chat.Spam = true
		}
	}
	// Google Voice returns a thread's messages newest first.
	if messages := thread.GetMessages(); len(messages) > 0 {
		preview := convertMessage(chat.ID, messages[0])
		chat.Preview = &preview
		chat.LastActivity = messages[0].GetTimestamp()
	}
	return chat
}

// Messages in the order the app draws them: oldest first.
func convertMessages(chatID string, messages []*gvproto.Message) []voiceMessage {
	converted := make([]voiceMessage, 0, len(messages))
	for _, message := range messages {
		converted = append(converted, convertMessage(chatID, message))
	}
	slices.Reverse(converted)
	return converted
}

func convertMessage(chatID string, message *gvproto.Message) voiceMessage {
	converted := voiceMessage{ID: message.GetID(), ChatID: chatID, Timestamp: message.GetTimestamp(), Kind: "text"}
	switch message.GetType() {
	case gvproto.Message_SMS_OUT, gvproto.Message_OUTGOING_CALL, gvproto.Message_OUTGOING_CALL_CANCELLED:
		converted.Outgoing = true
	default:
		converted.Sender = message.GetMMS().GetSenderPhoneNumber()
		if converted.Sender == "" {
			converted.Sender = message.GetContact().GetPhoneNumber()
		}
	}
	// Calls and voicemail share threads with texts. They are described in words, the way upstream's bridge shows them.
	if message.GetText() == "" && message.GetMMS() == nil {
		switch {
		case message.GetType() == gvproto.Message_VOICEMAIL:
			converted.Kind = "voicemail"
			converted.CallKind = "voicemail"
			if transcript := transcriptText(message.GetTranscript()); transcript != "" {
				converted.Text = "Voicemail: " + transcript
			} else {
				converted.Text = "Voicemail"
			}
			if message.GetMediaURL() != "" && message.GetID() != "" {
				seconds := float64(message.GetDurationSeconds())
				if math.IsNaN(seconds) || math.IsInf(seconds, 0) || seconds < 0 {
					seconds = 0
				}
				converted.Attachments = []voiceAttachment{{ID: voicemailPrefix + message.GetID(), Voicemail: true, Duration: int(math.Round(seconds))}}
			}
			return converted
		case message.GetType() == gvproto.Message_MISSED_CALL:
			converted.Kind = "call"
			converted.CallKind = "missed"
			converted.Text = "Missed voice call"
			return converted
		case message.GetType() == gvproto.Message_OUTGOING_CALL_CANCELLED:
			converted.Kind = "call"
			converted.CallKind = "unanswered"
			converted.Text = "Unanswered voice call"
			return converted
		case message.GetType() == gvproto.Message_INCOMING_CALL:
			converted.Kind = "call"
			converted.CallKind = "incoming"
			converted.Text = callText(message.GetDurationSeconds())
			return converted
		case message.GetType() == gvproto.Message_OUTGOING_CALL:
			converted.Kind = "call"
			converted.CallKind = "outgoing"
			converted.Text = callText(message.GetDurationSeconds())
			return converted
		}
		converted.Kind = "unsupported"
		return converted
	}
	if mms := message.GetMMS(); mms != nil {
		converted.Text = mms.GetText()
		if mms.GetSubject() != "" {
			converted.Text = strings.TrimSpace(mms.GetSubject() + "\n" + mms.GetText())
		}
		for _, attachment := range mms.GetAttachments() {
			converted.Attachments = append(converted.Attachments, convertAttachment(attachment))
		}
		return converted
	}
	converted.Text = message.GetText()
	return converted
}

func convertAttachment(attachment *gvproto.Attachment) voiceAttachment {
	converted := voiceAttachment{ID: attachment.GetID(), Mime: attachment.GetMimeType(),
		Unsupported: attachment.GetStatus() == gvproto.Attachment_NOT_SUPPORTED}
	var chosen *gvproto.Attachment_Metadata
	for _, size := range attachment.GetMetadata() {
		if size.GetSize() == gvproto.Attachment_Metadata_ORIGINAL {
			chosen = size
			break
		}
		if chosen == nil || size.GetWidth() > chosen.GetWidth() {
			chosen = size
		}
	}
	if chosen != nil {
		converted.Width, converted.Height = chosen.GetWidth(), chosen.GetHeight()
	}
	return converted
}

func transcriptText(transcript *gvproto.Message_Transcript) string {
	var parts []string
	for _, token := range transcript.GetTokens() {
		text := token.GetText()
		if len(text) == 0 || !utf8.Valid(text) {
			continue
		}
		if trimmed := strings.TrimSpace(string(text)); trimmed != "" {
			parts = append(parts, trimmed)
		}
	}
	return strings.Join(parts, " ")
}

func callText(seconds float32) string {
	if seconds <= 0 || math.IsNaN(float64(seconds)) || math.IsInf(float64(seconds), 0) {
		return "Voice call"
	}
	duration := time.Duration(math.Round(float64(seconds))) * time.Second
	if duration < time.Minute {
		return fmt.Sprintf("Voice call · %ds", int(duration.Seconds()))
	}
	return fmt.Sprintf("Voice call · %dm %02ds", int(duration.Minutes()), int(duration.Seconds())%60)
}
