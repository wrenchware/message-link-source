package main

import (
	"bytes"
	"context"
	"fmt"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Every identity row this store currently holds, by thread, as the exact bytes on disk.
func storedIdentities(t *testing.T, ctx context.Context, kept *messageStore) map[int64][]byte {
	t.Helper()
	rows, err := kept.db.QueryContext(ctx, `SELECT thread, identity FROM thread_identity`)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	held := map[int64][]byte{}
	for rows.Next() {
		var key int64
		var identity []byte
		if err := rows.Scan(&key, &identity); err != nil {
			t.Fatal(err)
		}
		held[key] = identity
	}
	return held
}

// protectLocal seals the same bytes differently every time, so "write only when it differs" could never tell an
// unchanged row from a changed one: every conversation, contact and picture was re-sealed and rewritten on every
// apply, which cost a cold start 15.1 seconds for eighteen new messages. Saving twice with nothing changed must now
// leave the stored bytes exactly as they were.
func TestUnchangedIdentityIsNotRewritten(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, t.TempDir()+"/messages.db")
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	s := newStore()
	s.get(7).kind = table.ONE_TO_ONE
	s.get(7).name = "Synthetic"
	s.get(8).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	kept.saveIdentity(ctx, s)
	first := storedIdentities(t, ctx, kept)
	if len(first) != 2 {
		t.Fatalf("expected two identity rows, got %d", len(first))
	}
	kept.saveIdentity(ctx, s)
	for key, identity := range storedIdentities(t, ctx, kept) {
		if !bytes.Equal(identity, first[key]) {
			t.Fatal("an unchanged conversation was sealed and written again")
		}
	}
}

// And a conversation that did change is still written, so the saving is of work and not of updates.
func TestChangedIdentityIsStillWritten(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/messages.db"
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	s := newStore()
	s.get(7).kind = table.ONE_TO_ONE
	s.get(7).name = "Before"
	kept.saveIdentity(ctx, s)
	before := storedIdentities(t, ctx, kept)
	s.get(7).name = "After"
	kept.saveIdentity(ctx, s)
	if bytes.Equal(storedIdentities(t, ctx, kept)[7], before[7]) {
		t.Fatal("a renamed conversation was not written")
	}
	kept.db.Close()
	// And it is the new name that comes back, not the one the memo might have held on to.
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	restored := newStore()
	kept.restore(ctx, restored)
	if restored.get(7).name != "After" {
		t.Fatalf("restored the name %q", restored.get(7).name)
	}
}

// A mutation that arrives before the app has asked the helper to connect must refuse at once. connect is a lifecycle
// method, so the dispatcher holds it until every request already in flight has finished — and a request spending
// sendWait waiting for the connection is waiting for something queued behind itself. A cold start lost exactly
// twenty seconds to that, because opening a conversation marks it read while login is still running.
func TestMutationDoesNotWaitForAConnectionNobodyHasAskedFor(t *testing.T) {
	a := newAdapter(&output{})
	started := time.Now()
	if a.readyToSend(context.Background()) {
		t.Fatal("a mutation was allowed before the helper had been asked to connect")
	}
	if waited := time.Since(started); waited > time.Second {
		t.Fatalf("the refusal took %v, so it waited for a connection that had not been started", waited)
	}
}

// The batch keepMessages writes must hold exactly what writing one at a time held. This is the archive, and for a
// conversation Facebook has since encrypted it is the only copy there is, so the saving must be of fsyncs and not of
// messages.
func TestBatchedKeepWritesEveryMessageAndItsAttachments(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/messages.db"
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	a := newAdapter(&output{})
	a.kept = kept
	const messages = 40
	ids := make([]string, 0, messages)
	for i := 0; i < messages; i++ {
		id := fmt.Sprintf("m%03d", i)
		ids = append(ids, id)
		a.chats.addEncrypted(7, id, 42, "synthetic "+id, "", int64(1000+i))
	}
	a.chats.attachSecure(7, ids[0], 1000, &attachment{id: "picture", mime: "image/jpeg"}, nil)
	a.keepMessages(ctx, &changes{Chats: []string{"7"}, Messages: map[string][]string{"7": ids}})
	kept.db.Close()

	reopened, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.db.Close()
	restored := newStore()
	reopened.restore(ctx, restored)
	for _, id := range ids {
		if _, _, _, _, ok := restored.rawMessage(7, id); !ok {
			t.Fatalf("the batch did not keep %s", id)
		}
	}
	var attachments int
	if err := reopened.db.QueryRowContext(ctx, `SELECT COUNT(*) FROM message_attachment WHERE message = ?`, ids[0]).Scan(&attachments); err != nil {
		t.Fatal(err)
	}
	if attachments != 1 {
		t.Fatalf("expected the attachment to be kept, found %d", attachments)
	}
}

// Every contact row this store currently holds, by id, as the exact sealed name and picture on disk.
func storedContacts(t *testing.T, ctx context.Context, kept *messageStore) map[int64][2][]byte {
	t.Helper()
	rows, err := kept.db.QueryContext(ctx, `SELECT id, name, picture FROM thread_contact`)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	held := map[int64][2][]byte{}
	for rows.Next() {
		var id int64
		var name, picture []byte
		if err := rows.Scan(&id, &name, &picture); err != nil {
			t.Fatal(err)
		}
		held[id] = [2][]byte{name, picture}
	}
	return held
}

// The memo used to start empty in every helper process, so the first apply after login re-sealed and rewrote every
// conversation and contact it had just read back unchanged. Restoring now seeds it from what each row holds: the
// first save after a restart writes nothing, and the bytes on disk stay exactly as they were.
func TestRestoredIdentityIsNotRewrittenByTheFirstSave(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + "/messages.db"
	kept, err := openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	s := newStore()
	s.get(7).kind = table.ONE_TO_ONE
	s.get(7).name = "Synthetic"
	s.get(7).participants[555] = struct{}{}
	s.get(8).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	s.remember(555, "Synthetic peer", "", "https://example.test/peer.jpg")
	s.remember(556, "", "", "https://example.test/nameless.jpg")
	if written := kept.saveIdentity(ctx, s); written != 4 {
		t.Fatalf("expected two conversations and two contacts written, got %d", written)
	}
	threads, contacts := storedIdentities(t, ctx, kept), storedContacts(t, ctx, kept)
	kept.db.Close()

	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	restored := newStore()
	kept.restore(ctx, restored)
	if written := kept.saveIdentity(ctx, restored); written != 0 {
		t.Fatalf("the first save after a restore rewrote %d unchanged rows", written)
	}
	for key, identity := range storedIdentities(t, ctx, kept) {
		if !bytes.Equal(identity, threads[key]) {
			t.Fatal("an unchanged conversation was sealed and written again")
		}
	}
	for id, row := range storedContacts(t, ctx, kept) {
		if !bytes.Equal(row[0], contacts[id][0]) || !bytes.Equal(row[1], contacts[id][1]) {
			t.Fatal("an unchanged contact was sealed and written again")
		}
	}
	// A change after the restore is still written, and it is what the next restore reads.
	restored.get(7).name = "Renamed"
	if written := kept.saveIdentity(ctx, restored); written != 1 {
		t.Fatalf("a renamed conversation wrote %d rows", written)
	}
	kept.db.Close()
	kept, err = openMessageStore(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	again := newStore()
	kept.restore(ctx, again)
	if again.get(7).name != "Renamed" {
		t.Fatalf("restored the name %q", again.get(7).name)
	}
}

// The seed is what the row holds, never the state it was restored into. Memory can already hold more than the row
// (a second login in the same process keeps its store), and a seed taken from memory would claim the row held that
// too and skip the write it still needs.
func TestRestoreSeedsFromTheRowNotFromMemory(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, t.TempDir()+"/messages.db")
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	s := newStore()
	s.remember(555, "Synthetic peer", "", "")
	kept.saveIdentity(ctx, s)
	kept.savedThreads, kept.savedContacts = nil, nil

	merged := newStore()
	merged.remember(555, "", "", "https://example.test/newer.jpg")
	kept.restore(ctx, merged)
	if written := kept.saveIdentity(ctx, merged); written != 1 {
		t.Fatalf("a picture only memory held wrote %d rows", written)
	}
	restored := newStore()
	kept.restore(ctx, restored)
	if restored.pictures[555] != "https://example.test/newer.jpg" {
		t.Fatal("the picture memory held was never written")
	}
}

// A stand-in row deleted once its conversation is paired takes its memo with it. Otherwise the same address coming
// back with the same content would be taken as already written, to a row that no longer exists.
func TestDeletedStandInForgetsItsMemo(t *testing.T) {
	ctx := context.Background()
	kept, err := openMessageStore(ctx, t.TempDir()+"/messages.db")
	if err != nil {
		t.Fatal(err)
	}
	defer kept.db.Close()
	s := newStore()
	s.get(555).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	s.get(7).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	kept.saveIdentity(ctx, s)
	delete(s.threads, 555)
	s.hybrid[555] = 7
	kept.saveIdentity(ctx, s)
	if _, ok := storedIdentities(t, ctx, kept)[555]; ok {
		t.Fatal("the stand-in row was not deleted")
	}
	delete(s.hybrid, 555)
	s.get(555).kind = table.ENCRYPTED_OVER_WA_ONE_TO_ONE
	kept.saveIdentity(ctx, s)
	if _, ok := storedIdentities(t, ctx, kept)[555]; !ok {
		t.Fatal("a reappearing address was skipped on the strength of a deleted row's memo")
	}
}
