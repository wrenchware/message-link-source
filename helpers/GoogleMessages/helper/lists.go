package main

import (
	"context"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func (a *adapter) listConversations(ctx context.Context, count int, folder gmproto.ListConversationsRequest_Folder) (*gmproto.ListConversationsResponse, error) {
	a.lists.Lock()
	defer a.lists.Unlock()
	resp, err := a.client.ListConversations(ctx, &gmproto.ListConversationsRequest{Count: int64(count), Folder: folder})
	for _, c := range resp.GetConversations() {
		a.conversations.note(c)
	}
	return resp, err
}
