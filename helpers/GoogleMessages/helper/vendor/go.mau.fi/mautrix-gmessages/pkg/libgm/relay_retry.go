package libgm

// Added to the pinned libgm (see third_party/mautrix-gmessages/NATIVE_MESSAGES_PATCH.md).

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// The actions to the phone that are posted again when Google answers 5xx, as upstream posts every request. Every action
// travels through the same relay endpoint, and a 5xx does not say whether the relay had already passed the request on,
// so posting a send again can deliver it twice, and the same holds for a reaction, a delete, a read marker or a new
// group. This is a list of what may repeat rather than of what may not: an action missing from it, including any that
// upstream adds later, is posted once, and the caller reports its outcome as uncertain.
var repeatableRelayActions = map[gmproto.ActionType]bool{
	// Reads: each asks the phone for something and changes nothing on it or for anyone in a conversation.
	gmproto.ActionType_LIST_CONVERSATIONS:         true,
	gmproto.ActionType_LIST_MESSAGES:              true,
	gmproto.ActionType_LIST_CONTACTS:              true,
	gmproto.ActionType_LIST_TOP_CONTACTS:          true,
	gmproto.ActionType_GET_CONVERSATION:           true,
	gmproto.ActionType_GET_CONVERSATION_TYPE:      true,
	gmproto.ActionType_GET_CONTACTS_THUMBNAIL:     true,
	gmproto.ActionType_GET_PARTICIPANTS_THUMBNAIL: true,
	gmproto.ActionType_IS_BUGLE_DEFAULT:           true,
	// Asks the phone for the full-size copy of a picture it already holds; asking twice makes the same copy.
	gmproto.ActionType_GET_FULL_SIZE_IMAGE: true,
	// Connection upkeep between this client and the phone, repeated as the same request: making this session the one
	// that receives updates (the request id is the session id), a ping, and an acknowledgement. None reaches a
	// conversation or changes what the account holds.
	gmproto.ActionType_GET_UPDATES:           true,
	gmproto.ActionType_NOTIFY_DITTO_ACTIVITY: true,
	gmproto.ActionType_ACK_BROWSER_PRESENCE:  true,
}

// How many times a request carrying this action may be posted while Google answers 5xx.
func relayAttempts(action gmproto.ActionType) int {
	if repeatableRelayActions[action] {
		return ServerErrorMaxAttempts
	}
	return 1
}
