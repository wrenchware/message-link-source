package main

import (
	"context"
	"sort"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Receipts for encrypted conversations, kept in the account's messages.db. The encrypted connection says who read or
// received a message once, when it happens, and never again — so without this every face and "Read" in an encrypted
// conversation was gone at the next start. Only ids, contact numbers and moments are kept, the same kind of values
// the message tables beside them already hold in the clear.
//
// Ordinary conversations are not kept: Facebook sends their watermarks again with every participant snapshot.
const (
	receiptWatermarkTable = `CREATE TABLE IF NOT EXISTS receipt_watermark (
		thread INTEGER NOT NULL,
		contact INTEGER NOT NULL,
		read_mark INTEGER NOT NULL,
		read_from INTEGER NOT NULL,
		read_at INTEGER NOT NULL,
		delivered INTEGER NOT NULL,
		PRIMARY KEY (thread, contact)
	)`
	// Receipts naming a message that had not arrived when they did. Kept so a message delivered at a later start still
	// gets them, for as long as receiptbook.go lets one wait; parked is when this helper began keeping it.
	receiptWaitingTable = `CREATE TABLE IF NOT EXISTS receipt_waiting (
		message TEXT NOT NULL,
		thread INTEGER NOT NULL,
		contact INTEGER NOT NULL,
		kind INTEGER NOT NULL,
		at INTEGER NOT NULL,
		parked INTEGER NOT NULL,
		PRIMARY KEY (message, contact, kind)
	)`
)

// One person's positions in one conversation, written so each only ever moves forward, here as in memory. A backup
// import writes with it too, so a position this computer has moved further since the backup keeps its own.
const keepWatermarkStatement = `INSERT INTO receipt_watermark (thread, contact, read_mark, read_from, read_at, delivered)
	VALUES (?, ?, ?, ?, ?, ?)
	ON CONFLICT(thread, contact) DO UPDATE SET
	 read_from = CASE WHEN excluded.read_mark > read_mark THEN excluded.read_from ELSE read_from END,
	 read_at = CASE WHEN excluded.read_mark > read_mark THEN excluded.read_at ELSE read_at END,
	 read_mark = MAX(read_mark, excluded.read_mark),
	 delivered = MAX(delivered, excluded.delivered)`

type keptWatermark struct {
	thread, contact                   int64
	read, readFrom, readAt, delivered int64
	gone                              bool
}

type keptWaiting struct {
	message                           string
	thread, contact, kind, at, parked int64
}

// What has changed since the last keep, taken under the lock so the writes can happen outside it. Watermarks for
// conversations that are not encrypted are dropped here rather than written.
func (s *store) receiptsToKeep() (watermarks []keptWatermark, waiting []keptWaiting, cleared []string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	book := s.receipts
	if book == nil || len(book.dirty)+len(book.parked)+len(book.cleared) == 0 {
		return nil, nil, nil
	}
	for key := range book.dirty {
		thread := s.threads[key.thread]
		encrypted := book.secure[key.thread] || thread != nil && isEncryptedKind(thread.kind)
		if !encrypted {
			continue
		}
		mark := book.marks[key.thread][key.contact]
		read := int64(0)
		if thread != nil {
			read = thread.readBy[key.contact]
		}
		if mark == nil && read == 0 {
			watermarks = append(watermarks, keptWatermark{thread: key.thread, contact: key.contact, gone: true})
			continue
		}
		row := keptWatermark{thread: key.thread, contact: key.contact, read: read, readFrom: -1}
		if mark != nil {
			row.readFrom, row.readAt, row.delivered = mark.readFrom, mark.readAt, mark.delivered
		}
		watermarks = append(watermarks, row)
	}
	for id := range book.parked {
		for _, receipt := range book.waiting[id] {
			waiting = append(waiting, keptWaiting{message: id, thread: receipt.thread, contact: receipt.contact,
				kind: int64(receipt.kind), at: receipt.at, parked: receipt.since})
		}
	}
	for id := range book.cleared {
		cleared = append(cleared, id)
	}
	sort.Strings(cleared)
	book.dirty, book.parked, book.cleared = map[receiptKey]bool{}, map[string]bool{}, map[string]bool{}
	return watermarks, waiting, cleared
}

// Writes whatever receipts changed since the last keep, in one transaction. A failed write puts nothing back: the
// state in memory is still right for this session, and the next receipt for that person writes their row again.
func (a *adapter) keepReceipts(ctx context.Context) {
	a.mu.Lock()
	kept := a.kept
	a.mu.Unlock()
	if kept == nil {
		return
	}
	watermarks, waiting, cleared := a.chats.receiptsToKeep()
	if len(watermarks)+len(waiting)+len(cleared) == 0 {
		return
	}
	if err := kept.saveReceipts(ctx, watermarks, waiting, cleared); err != nil {
		logf("receipts could not be kept")
	}
}

func (m *messageStore) saveReceipts(ctx context.Context, watermarks []keptWatermark, waiting []keptWaiting, cleared []string) error {
	tx, err := m.db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	for _, row := range watermarks {
		if row.gone {
			_, err = tx.ExecContext(ctx, `DELETE FROM receipt_watermark WHERE thread = ? AND contact = ?`, row.thread, row.contact)
		} else {
			_, err = tx.ExecContext(ctx, keepWatermarkStatement, row.thread, row.contact, row.read, row.readFrom, row.readAt, row.delivered)
		}
		if err != nil {
			return err
		}
	}
	// Removed before written: a message whose receipts were cleared and then named again keeps the new ones.
	for _, id := range cleared {
		if _, err = tx.ExecContext(ctx, `DELETE FROM receipt_waiting WHERE message = ?`, id); err != nil {
			return err
		}
	}
	for _, row := range waiting {
		if _, err = tx.ExecContext(ctx, `INSERT INTO receipt_waiting (message, thread, contact, kind, at, parked) VALUES (?, ?, ?, ?, ?, ?)
			ON CONFLICT(message, contact, kind) DO UPDATE SET at = MAX(at, excluded.at), thread = excluded.thread,
			 parked = MIN(parked, excluded.parked)`,
			row.message, row.thread, row.contact, row.kind, row.at, row.parked); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// A deleted conversation's kept receipts, removed with the rest of its stored history (messageStore.forgetThread).
const (
	forgetThreadWatermarks = `DELETE FROM receipt_watermark WHERE thread = ?`
	forgetThreadWaiting    = `DELETE FROM receipt_waiting WHERE thread = ?`
)

// Everything kept, back into memory: after the messages, so a receipt waiting for a message that was kept applies at
// once. Returns how many positions and waiting receipts came back and how many of those settled straight away.
func (m *messageStore) restoreReceipts(ctx context.Context, into *store) (watermarks, waiting, settled int) {
	if m == nil {
		return 0, 0, 0
	}
	dropped := 0
	defer func() {
		logf("receipts restored watermarks=%d waiting=%d settled=%d dropped=%d", watermarks, waiting, settled, dropped)
	}()
	// Receipts past their age are given up here, and so is whatever does not fit beside the newest, so what is kept
	// and what is in memory stay the same set.
	for _, trim := range []struct {
		statement string
		argument  int64
	}{
		{`DELETE FROM receipt_waiting WHERE parked < ?`, receiptClock() - waitingReceiptAge},
		{`DELETE FROM receipt_waiting WHERE rowid NOT IN (SELECT rowid FROM receipt_waiting ORDER BY parked DESC LIMIT ?)`, maxWaitingReceipts},
	} {
		if result, err := m.db.ExecContext(ctx, trim.statement, trim.argument); err == nil {
			if count, err := result.RowsAffected(); err == nil {
				dropped += int(count)
			}
		}
	}
	rows, err := m.db.QueryContext(ctx, `SELECT thread, contact, read_mark, read_from, read_at, delivered FROM receipt_watermark`)
	if err != nil {
		logf("kept receipts could not be read")
		return 0, 0, 0
	}
	var kept []keptWatermark
	for rows.Next() {
		var row keptWatermark
		if rows.Scan(&row.thread, &row.contact, &row.read, &row.readFrom, &row.readAt, &row.delivered) == nil {
			kept = append(kept, row)
		}
	}
	rows.Close()
	var parked []keptWaiting
	// Longest waiting first, so each message's first receipt says how long it has waited.
	if rows, err = m.db.QueryContext(ctx, `SELECT message, thread, contact, kind, at, parked FROM receipt_waiting
		ORDER BY parked, message LIMIT ?`, maxWaitingReceipts); err == nil {
		for rows.Next() {
			var row keptWaiting
			if rows.Scan(&row.message, &row.thread, &row.contact, &row.kind, &row.at, &row.parked) == nil {
				parked = append(parked, row)
			}
		}
		rows.Close()
	}
	into.mu.Lock()
	defer into.mu.Unlock()
	book := into.book()
	for _, row := range kept {
		key := row.thread
		// A conversation first known by its encrypted address may since have been paired with its ordinary number.
		if paired, ok := into.hybrid[key]; ok {
			key = paired
		}
		// Only onto a conversation the kept identity brought back: a position is not reason enough to list one.
		current, ok := into.threads[key]
		if !ok || current.deleted || row.contact == 0 || row.contact == into.self && into.self != 0 {
			continue
		}
		if current.readBy == nil {
			current.readBy = map[int64]int64{}
		}
		if row.read > current.readBy[row.contact] {
			current.readBy[row.contact] = row.read
			mark := into.markOf(key, row.contact)
			mark.readFrom, mark.readAt = row.readFrom, row.readAt
		}
		if row.delivered > 0 {
			mark := into.markOf(key, row.contact)
			mark.delivered = max(mark.delivered, row.delivered)
		}
		book.secure[key] = true
		watermarks++
	}
	for _, row := range parked {
		thread := row.thread
		if paired, ok := into.hybrid[thread]; ok {
			// Written again under the paired number, so deleting that conversation finds it.
			thread = paired
			book.parked[row.message] = true
		}
		book.waiting[row.message] = append(book.waiting[row.message], waitingReceipt{thread: thread, contact: row.contact,
			kind: receiptKind(row.kind), at: row.at, since: row.parked})
		waiting++
	}
	for id := range book.waiting {
		if key, current, ok := into.messageAnywhere(id); ok {
			into.settleWaiting(key, current)
			settled++
		}
	}
	return watermarks, waiting, settled
}

func isEncryptedKind(kind table.ThreadType) bool {
	return kind == table.ENCRYPTED_OVER_WA_ONE_TO_ONE || kind == table.ENCRYPTED_OVER_WA_GROUP
}
