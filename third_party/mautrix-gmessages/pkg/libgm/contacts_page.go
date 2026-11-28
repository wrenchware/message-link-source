package libgm

// Added to the pinned libgm (see third_party/mautrix-gmessages/NATIVE_MESSAGES_PATCH.md). libgm's ListContacts sends
// one fixed request whose answer stopped at 50 contacts for the owner's phone (2026-10-05), and libgm offers no other
// way to send it.

import (
	"context"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// ListContactsPage is ListContacts with the request's three numbers chosen by the caller. Upstream sends 1, 350, 50;
// what each means is not documented upstream. A read: it creates, sends and marks nothing.
func (c *Client) ListContactsPage(ctx context.Context, i1, i2, i3 int32) (*gmproto.ListContactsResponse, error) {
	payload := &gmproto.ListContactsRequest{I1: i1, I2: i2, I3: i3}
	return typedResponse[*gmproto.ListContactsResponse](c.getSessionHandler().sendMessage(ctx, gmproto.ActionType_LIST_CONTACTS, payload))
}
