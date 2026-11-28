package main

import "go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"

// Interactive pages default to 20; search indexing may request up to 100 per phone round trip.
func pageCount(requested int) int64 {
	if requested < 1 {
		return 20
	}
	if requested > 100 {
		return 100
	}
	return int64(requested)
}

// Without a returned cursor, continue from the oldest message (microseconds to
// cursor milliseconds), matching the upstream connector.
func pageCursor(result *gmproto.ListMessagesResponse) *gmproto.Cursor {
	cursor := result.GetCursor()
	if cursor == nil && len(result.GetMessages()) > 0 {
		oldest := result.GetMessages()[0]
		for _, m := range result.GetMessages() {
			if m.GetTimestamp() < oldest.GetTimestamp() {
				oldest = m
			}
		}
		cursor = &gmproto.Cursor{LastItemID: oldest.GetMessageID(), LastItemTimestamp: oldest.GetTimestamp() / 1000}
	}
	return cursor
}

// The three folders the list and the Spam view read.
func listFolder(name string) (gmproto.ListConversationsRequest_Folder, bool) {
	switch name {
	case "", "inbox":
		return gmproto.ListConversationsRequest_INBOX, true
	case "archive":
		return gmproto.ListConversationsRequest_ARCHIVE, true
	case "spam":
		return gmproto.ListConversationsRequest_SPAM_BLOCKED, true
	}
	return gmproto.ListConversationsRequest_UNKNOWN, false
}
