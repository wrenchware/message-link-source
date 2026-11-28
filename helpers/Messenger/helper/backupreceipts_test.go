package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"slices"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// The receipts one import page announced, by conversation, and the messages it announced as new.
func importEvents(t *testing.T, events *bytes.Buffer) (receipts, messages map[string][]string) {
	t.Helper()
	receipts, messages = map[string][]string{}, map[string][]string{}
	scanner := bufio.NewScanner(bytes.NewReader(events.Bytes()))
	scanner.Buffer(nil, 1<<20)
	for scanner.Scan() {
		var line struct {
			Event  string  `json:"event"`
			Result changes `json:"result"`
		}
		if json.Unmarshal(scanner.Bytes(), &line) != nil || line.Event != "changed" {
			continue
		}
		for chat, ids := range line.Result.Receipts {
			receipts[chat] = append(receipts[chat], ids...)
		}
		for chat, ids := range line.Result.Messages {
			messages[chat] = append(messages[chat], ids...)
		}
	}
	return receipts, messages
}

func keptWatermarkRow(t *testing.T, kept *messageStore, thread, contact int64) (row keptWatermark, found bool) {
	t.Helper()
	err := kept.db.QueryRow(`SELECT read_mark, read_from, read_at, delivered FROM receipt_watermark WHERE thread = ? AND contact = ?`,
		thread, contact).Scan(&row.read, &row.readFrom, &row.readAt, &row.delivered)
	return row, err == nil
}

// An encrypted conversation's read and delivery positions are reported once and kept only on this computer, so a
// backup carries them: after a restore the faces, "Read" and delivered counts are what they were, a position this
// computer has moved further since keeps its own, and one for a conversation not held here has nowhere to go.
func TestBackupCarriesEncryptedReceipts(t *testing.T) {
	ctx := context.Background()
	// Small pages, so the receipts arrive on a page of their own after the messages they redraw.
	budget := backupPageBudget
	backupPageBudget = 512
	t.Cleanup(func() { backupPageBudget = budget })
	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	seedBackupSource(t, source)
	backupMust(t, source.kept.saveReceipts(ctx, []keptWatermark{
		{thread: 7, contact: 555, read: 2000, readFrom: 1000, readAt: 2100, delivered: 2000},
		{thread: 7, contact: 556, readFrom: -1, delivered: 1500},
		// Deleted here and in the backup: a position there is nothing to show on.
		{thread: 9, contact: 555, read: 100, readFrom: -1},
	}, nil, nil))
	pages := exportAll(t, source)
	var first, last map[string]json.RawMessage
	backupMust(t, json.Unmarshal(pages[0], &first))
	backupMust(t, json.Unmarshal(pages[len(pages)-1], &last))
	var expected, exported backupTally
	backupMust(t, json.Unmarshal(first["expected"], &expected))
	backupMust(t, json.Unmarshal(last["exported"], &exported))
	if expected.Receipts != 3 || exported.Receipts != 3 {
		t.Fatalf("receipts expected=%d exported=%d", expected.Receipts, exported.Receipts)
	}

	target, events := backupAdapter(t, t.TempDir(), backupViewer)
	total := importAll(t, target, pages)
	if total.Sections.Receipts != (backupCounts{Added: 2, Refused: 1}) {
		t.Fatalf("unexpected receipt counts %+v", total.Sections.Receipts)
	}
	if row, ok := keptWatermarkRow(t, target.kept, 7, 555); !ok || row.read != 2000 || row.readFrom != 1000 || row.readAt != 2100 || row.delivered != 2000 {
		t.Fatalf("the reader's kept positions did not come back: %+v %v", row, ok)
	}
	if row, ok := keptWatermarkRow(t, target.kept, 7, 556); !ok || row.read != 0 || row.readFrom != -1 || row.delivered != 1500 {
		t.Fatalf("the delivery position did not come back: %+v %v", row, ok)
	}
	if _, ok := keptWatermarkRow(t, target.kept, 9, 555); ok {
		t.Fatal("a position for a deleted conversation was kept")
	}
	s := target.chats
	s.mu.Lock()
	m2 := s.messages[7]["m2"]
	readers, marks, seen, reached := s.readersOf(7, m2), s.readMarkOf(7, m2), s.seenAt(7, m2), s.deliveredTo(7, m2)
	secure := s.receipts.secure[7]
	s.mu.Unlock()
	if !slices.Equal(readers, []string{"555"}) || !slices.Equal(marks, []string{"555"}) || seen != 2100 || reached != 1 || !secure {
		t.Fatalf("the restored positions do not show: readers=%v marks=%v seen=%d reached=%d secure=%v", readers, marks, seen, reached, secure)
	}
	if receipts, _ := importEvents(t, events); !slices.Contains(receipts["7"], "m2") {
		t.Fatalf("the rows the positions moved were not redrawn: %v", receipts)
	}

	// Read further here since the backup: importing it again moves nothing back, in memory or kept.
	s.mu.Lock()
	s.advanceReadAt(7, 555, 4000, 0, 4100, true)
	s.mu.Unlock()
	target.keepReceipts(ctx)
	again := importAll(t, target, pages)
	if again.Sections.Receipts != (backupCounts{Skipped: 2, Refused: 1}) || again.Added != 0 {
		t.Fatalf("a second import moved positions: %+v", again)
	}
	if row, _ := keptWatermarkRow(t, target.kept, 7, 555); row.read != 4000 || row.readFrom != 2000 || row.readAt != 4100 {
		t.Fatalf("a newer position was moved back: %+v", row)
	}
}

// A restored message can settle a receipt that was waiting for it, and can land beneath a reader's face and take it
// over. Either way the row the face leaves is redrawn with the new message, and a settled receipt is kept and no
// longer waits.
func TestBackupImportRedrawsTheRowAFaceLeaves(t *testing.T) {
	ctx := context.Background()
	seed := func(a *adapter, messages map[string]int64) {
		a.chats.mu.Lock()
		direct := a.chats.get(7)
		direct.name, direct.kind, direct.lastActivity = "Private direct title", table.ENCRYPTED_OVER_WA_ONE_TO_ONE, 9000
		direct.participants[backupViewer] = struct{}{}
		direct.participants[555] = struct{}{}
		a.chats.mu.Unlock()
		a.kept.saveIdentity(ctx, a.chats)
		for id, at := range messages {
			backupMust(t, a.kept.rememberChecked(ctx, 7, id, backupViewer, "Private body "+id, "", at))
		}
		backupRelogin(a)
	}
	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	seed(source, map[string]int64{"A": 1000, "X": 3000})
	pages := exportAll(t, source)

	for _, waiting := range []bool{true, false} {
		target, events := backupAdapter(t, t.TempDir(), backupViewer)
		seed(target, map[string]int64{"A": 1000})
		s := target.chats
		s.mu.Lock()
		if waiting {
			// The reader's face is on A; their receipt for X, which this computer never received, waits for it.
			s.advanceReadAt(7, 555, 1000, 0, 0, false)
			s.applyEncryptedReceipt(7, 555, receiptRead, []string{"X"}, 3500)
		} else {
			// Read past X, which is not here, so the face sits on A until X arrives.
			s.advanceReadAt(7, 555, 3500, 0, 0, false)
		}
		s.mu.Unlock()
		target.keepReceipts(ctx)
		importAll(t, target, pages)

		receipts, messages := importEvents(t, events)
		if !slices.Contains(messages["7"], "X") || !slices.Contains(receipts["7"], "A") || slices.Contains(receipts["7"], "X") {
			t.Fatalf("waiting=%v: the row the face left was not redrawn: receipts=%v messages=%v", waiting, receipts, messages)
		}
		s.mu.Lock()
		marksA, marksX := s.readMarkOf(7, s.messages[7]["A"]), s.readMarkOf(7, s.messages[7]["X"])
		pending := len(s.receipts.waiting)
		s.mu.Unlock()
		if len(marksA) != 0 || !slices.Equal(marksX, []string{"555"}) || pending != 0 {
			t.Fatalf("waiting=%v: the face did not move to the restored message: A=%v X=%v waiting=%d", waiting, marksA, marksX, pending)
		}
		if tableCount(t, target.kept, "receipt_waiting") != 0 {
			t.Fatalf("waiting=%v: a settled receipt is still kept as waiting", waiting)
		}
		if row, ok := keptWatermarkRow(t, target.kept, 7, 555); !ok || waiting && (row.read != 3000 || row.readAt != 3500) {
			t.Fatalf("waiting=%v: the settled position was not kept: %+v %v", waiting, row, ok)
		}
	}
}

// Every table in the message store is either carried by a backup or named as deliberately left out of it (backup.go),
// so a table added for state that exists nowhere else cannot be dropped from every restore without anyone noticing.
func TestBackupAccountsForEveryStoreTable(t *testing.T) {
	a, _ := backupAdapter(t, t.TempDir(), backupViewer)
	carried := map[string]bool{"thread_contact": true, "thread_identity": true, "thread_mapping": true, "deleted_message": true,
		"encrypted_message": true, "encrypted_media": true, "encrypted_share": true, "message_attachment": true,
		"encrypted_reaction": true, "receipt_watermark": true}
	// store_owner says whose store it is and is checked, not carried; receipt_waiting names messages this computer
	// never had; archived_v3 holds the archive walk's markers, made when the first walk ends.
	leftOut := map[string]bool{"store_owner": true, "receipt_waiting": true, "archived_v3": true}
	a.kept.markComplete(context.Background(), 7)
	rows, err := a.kept.db.Query(`SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'`)
	backupMust(t, err)
	defer rows.Close()
	for rows.Next() {
		var name string
		backupMust(t, rows.Scan(&name))
		if !carried[name] && !leftOut[name] {
			t.Errorf("messages.db table %q is neither carried by a backup nor named as left out of it", name)
		}
	}
	backupMust(t, rows.Err())
}
