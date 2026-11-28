package main

import (
	"context"

	"go.mau.fi/whatsmeow/proto/waArmadilloXMA"
	"google.golang.org/protobuf/proto"
)

// Retain the actual share schema, including fields a future decoder may understand. This stays in the same local
// message database as the decoded message and its media directions; neither the payload nor its contents is logged.
func (m *messageStore) rememberShare(ctx context.Context, messageID string, content *waArmadilloXMA.ExtendedContentMessage) {
	if m == nil || messageID == "" || content == nil {
		return
	}
	payload, err := proto.Marshal(content)
	if err != nil {
		logf("e2ee share could not be encoded for keeping")
		return
	}
	payload, err = protectLocal(payload)
	if err != nil {
		logf("e2ee share could not be protected for keeping")
		return
	}
	_, err = m.db.ExecContext(ctx, `INSERT INTO encrypted_share (message, payload)
		SELECT ?, ? WHERE NOT EXISTS (SELECT 1 FROM deleted_message WHERE id = ?)
		ON CONFLICT(message) DO UPDATE SET payload = excluded.payload`, messageID, payload, messageID)
	if err != nil {
		logf("e2ee share could not be kept")
	}
}

// Decode retained shares on every start, after restoring the previously decoded media. Stable attachment ids update
// that media in place, allowing a later parser to repair a once-delivered share without contacting Facebook again.
func (m *messageStore) restoreShares(ctx context.Context, into *store) {
	rows, err := m.db.QueryContext(ctx, `SELECT parent.thread, parent.id, parent.sender, parent.body, parent.reply,
		parent.at, share.payload FROM encrypted_share AS share
		JOIN encrypted_message AS parent ON parent.id = share.message
		WHERE NOT EXISTS (SELECT 1 FROM deleted_message AS deleted WHERE deleted.id = parent.id)
		ORDER BY parent.at`)
	if err != nil {
		logf("e2ee kept shares could not be read")
		return
	}
	defer rows.Close()
	for rows.Next() {
		var thread, sender, at int64
		var messageID string
		var protectedBody, protectedReply, payload []byte
		if rows.Scan(&thread, &messageID, &sender, &protectedBody, &protectedReply, &at, &payload) != nil {
			logf("e2ee kept share could not be read")
			continue
		}
		body, bodyErr := unprotectString(protectedBody)
		replyTo, replyErr := unprotectString(protectedReply)
		payload, payloadErr := unprotectLocal(payload)
		if bodyErr != nil || replyErr != nil || payloadErr != nil {
			logf("e2ee kept share could not be unprotected")
			continue
		}
		var content waArmadilloXMA.ExtendedContentMessage
		if proto.Unmarshal(payload, &content) != nil {
			logf("e2ee kept share could not be decoded")
			continue
		}
		items, text := encryptedShare(&content, messageID)
		if text == "" {
			text = body
		}
		into.addEncrypted(thread, messageID, sender, text, replyTo, at)
		for _, media := range items {
			if media.item != nil {
				into.attachSecure(thread, messageID, at, media.item, media.held)
			}
		}
	}
	if rows.Err() != nil {
		logf("e2ee kept shares could not be read through")
	}
}
