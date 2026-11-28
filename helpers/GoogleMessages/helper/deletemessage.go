package main

import (
	"context"
	"encoding/json"
)

// Removing a message from a Google Messages conversation.
//
// **This is not a recall.** The request Google's protocol accepts carries a message id and nothing else — there is no
// scope, no "for everyone", and no variant that reaches the other person's phone. What it does is take the message
// out of this account's own conversation, on the phone and every device signed in to it. Whoever it was sent to keeps
// their copy, on RCS exactly as on SMS. The app must say so rather than offering something it cannot do.
func (a *adapter) deleteMessage(ctx context.Context, r request) (any, string) {
	if a.client == nil {
		return nil, "pairing_required"
	}
	var p struct {
		MessageID string `json:"messageId"`
		Everyone  bool   `json:"everyone"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.MessageID == "" {
		return nil, "invalid_input"
	}
	if p.Everyone {
		return nil, "delete_everyone_unsupported"
	}
	a.mediaMu.Lock()
	known := a.messageRefs[p.MessageID] != nil
	a.mediaMu.Unlock()
	if !known {
		return nil, "message_unavailable"
	}
	resp, err := a.client.DeleteMessage(ctx, p.MessageID)
	if err != nil {
		return nil, "delete_failed"
	}
	if !resp.GetSuccess() {
		return nil, "delete_rejected"
	}
	// The phone reports the removal back as a message update, which is what takes it out of the conversation here;
	// nothing is removed locally on the strength of the request alone.
	return map[string]any{"outcome": "accepted"}, ""
}
