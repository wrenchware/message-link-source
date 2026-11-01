package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

const backupViewer = int64(1001)

var backupHeart = string(rune(0x2764))

// A signed-in helper's store for one account under its own root, restored the way login restores it.
func backupAdapter(t *testing.T, root string, viewer int64) (*adapter, *bytes.Buffer) {
	t.Helper()
	ctx := context.Background()
	events := &bytes.Buffer{}
	a := newAdapter(&output{encoder: json.NewEncoder(events)})
	t.Cleanup(a.stop)
	a.chats.setSelf(viewer)
	folder := filepath.Join(root, "accounts", messengerAccountKey(viewer))
	if err := os.MkdirAll(folder, 0o700); err != nil {
		t.Fatal(err)
	}
	kept, err := openAccountMessageStore(ctx, filepath.Join(folder, "messages.db"), viewer)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = kept.db.Close() })
	a.kept = kept
	kept.restore(ctx, a.chats)
	return a, events
}

// As if the helper had just signed in again: memory rebuilt from what the store holds.
func backupRelogin(a *adapter) {
	a.chats = newStore()
	a.chats.setSelf(backupViewer)
	a.kept.restore(context.Background(), a.chats)
}

func backupMust(t *testing.T, err error) {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
}

func seedBackupSource(t *testing.T, a *adapter) {
	t.Helper()
	ctx := context.Background()
	kept, s := a.kept, a.chats
	s.mu.Lock()
	direct := s.get(7)
	direct.name, direct.kind, direct.lastActivity = "Private direct title", table.ENCRYPTED_OVER_WA_ONE_TO_ONE, 9000
	direct.participants[backupViewer] = struct{}{}
	direct.participants[555] = struct{}{}
	group := s.get(8)
	group.name, group.kind, group.lastActivity, group.folder = "Private group title", table.GROUP_THREAD, 9000, "inbox"
	gone := s.get(9)
	gone.name, gone.deleted, gone.deletedAt = "Private gone title", true, 5000
	s.contacts[555] = "Private person name"
	s.pictures[555] = "https://example.test/private-face.jpg"
	s.hybrid[5550] = 7
	// The stand-in a conversation started here leaves under the person's address once Facebook pairs it (standIns,
	// retire): kept as a deleted, empty thread row beside the pairing that now leads to the real conversation.
	standIn := s.get(5550)
	standIn.kind, standIn.deleted, standIn.lastActivity = table.ENCRYPTED_OVER_WA_ONE_TO_ONE, true, 800
	standIn.participants[5550] = struct{}{}
	standIn.participants[backupViewer] = struct{}{}
	s.mu.Unlock()
	kept.saveIdentity(ctx, s)
	backupMust(t, kept.rememberChecked(ctx, 7, "m1", 555, "Private body one", "", 1000))
	kept.rememberEdit(ctx, "m1", "Private body one edited", 1500)
	backupMust(t, kept.rememberChecked(ctx, 7, "m2", backupViewer, "Private body two", "m1", 2000))
	kept.rememberMedia(ctx, 7, "m2", 2000, &attachment{id: "media-1", name: "Private photo.jpg", mime: "image/jpeg", size: 99,
		width: 4, height: 3, link: "https://example.test/private-link"}, &secureMedia{transport: []byte{1, 2, 3, 4}, kind: "image"})
	kept.rememberReaction(ctx, 7, "m2", 555, backupHeart)
	backupMust(t, kept.rememberChecked(ctx, 8, "m3", 555, "Private ordinary body", "", 3000, false))
	kept.rememberAttachments(ctx, 8, "m3", []*attachment{{id: "att-1", name: "Private file", mime: "image/png",
		source: "https://example.test/private-source.png", preview: "att-1-preview", description: "Private description", actionTitle: "Private action"}},
		map[string]string{"att-1-preview": "https://example.test/private-preview.png"})
	card, err := protectString("Private card text")
	backupMust(t, err)
	_, err = kept.db.ExecContext(ctx, `UPDATE encrypted_message SET card_text = ?, ai_updated_at = 3100 WHERE id = 'm3'`, card)
	backupMust(t, err)
	backupMust(t, kept.rememberChecked(ctx, 7, "m4", 555, "", "", 4000))
	kept.rememberShare(ctx, "m4", retainedShareFixture(t))
	backupMust(t, kept.rememberChecked(ctx, 7, "m5", 555, "Private taken back", "", 4500))
	kept.forget(ctx, 7, "m5")
	// Hung on a message the store does not have, so there is nothing to show it on.
	kept.rememberAttachments(ctx, 8, "missing-message", []*attachment{{id: "orphan-1", name: "Private orphan", mime: "image/png",
		source: "https://example.test/private-orphan.png"}}, nil)
}

func exportAll(t *testing.T, a *adapter) [][]byte {
	t.Helper()
	ctx := context.Background()
	var pages [][]byte
	cursor := ""
	for range 10000 {
		params, _ := json.Marshal(map[string]string{"cursor": cursor})
		result, code := a.backupExport(ctx, params)
		if code != "" {
			t.Fatalf("export page %d refused: %s", len(pages), code)
		}
		raw, err := json.Marshal(result)
		backupMust(t, err)
		pages = append(pages, raw)
		cursor = result.(*backupPageOut).Next
		if cursor == "" {
			return pages
		}
	}
	t.Fatal("the export never finished")
	return nil
}

func importAll(t *testing.T, a *adapter, pages [][]byte) backupImportResult {
	t.Helper()
	var total backupImportResult
	for index, page := range pages {
		result, code := a.backupImport(context.Background(), page)
		if code != "" {
			t.Fatalf("import page %d refused: %s", index, code)
		}
		counted := result.(*backupImportResult)
		total.Added += counted.Added
		total.Skipped += counted.Skipped
		total.Refused += counted.Refused
		total.Removed += counted.Removed
		total.Cutoffs += counted.Cutoffs
		sum, part := total.Sections.all(), counted.Sections.all()
		for i := range sum {
			sum[i].Added += part[i].Added
			sum[i].Skipped += part[i].Skipped
			sum[i].Refused += part[i].Refused
			sum[i].Removed += part[i].Removed
		}
	}
	return total
}

// Everything a store restored at sign-in shows, in a form two stores can be compared by.
type backupView struct {
	Threads   map[int64]savedThread
	Contacts  map[int64]string
	Pictures  map[int64]string
	Hybrid    map[int64]int64
	Messages  map[string]string
	Attached  map[string][]string
	Files     map[string]string
	Reactions map[string]map[int64]string
	Revoked   []string
}

func viewOfStore(s *store) backupView {
	s.mu.Lock()
	defer s.mu.Unlock()
	view := backupView{Threads: map[int64]savedThread{}, Contacts: map[int64]string{}, Pictures: map[int64]string{},
		Hybrid: map[int64]int64{}, Messages: map[string]string{}, Attached: map[string][]string{}, Files: map[string]string{},
		Reactions: map[string]map[int64]string{}}
	for key, t := range s.threads {
		view.Threads[key] = savedThread{key, t.name, t.picture, t.folder, t.snippet, t.kind, t.participants, t.lastActivity,
			t.readAt, t.anchorAt, t.anchorID, t.cannotReply, t.deleted, t.unresolved, t.deletedAt, t.previewOutgoing, t.muteUntil}
	}
	for id, name := range s.contacts {
		view.Contacts[id] = name
	}
	for id, picture := range s.pictures {
		view.Pictures[id] = picture
	}
	for jid, key := range s.hybrid {
		view.Hybrid[jid] = key
	}
	for key, thread := range s.messages {
		for id, m := range thread {
			view.Messages[id] = fmt.Sprintf("thread=%d sender=%d text=%q reply=%q at=%d encrypted=%v edited=%d ai=%d card=%q",
				key, m.sender, m.text, m.replyTo, m.timestamp, m.encrypted, m.editedAt, m.aiUpdatedAt, m.cardText)
			for _, item := range s.attached[id] {
				source := ""
				if held := s.media[item.id]; held != nil {
					source = held.source
				}
				preview := ""
				if held := s.media[item.preview]; held != nil {
					preview = held.source
				}
				view.Attached[id] = append(view.Attached[id], fmt.Sprintf("%s %q %q %d %dx%d %v %v %q %q %q %q %q %q %q %q",
					item.id, item.name, item.mime, item.size, item.width, item.height, item.video, item.animated, item.source,
					item.preview, item.playable, item.link, item.description, item.actionTitle, source, preview))
				if held := s.files[item.id]; held != nil {
					view.Files[item.id] = fmt.Sprintf("%x %s", held.transport, held.kind)
				}
			}
			sort.Strings(view.Attached[id])
			if held := s.reactions[id]; len(held) > 0 {
				view.Reactions[id] = map[int64]string{}
				for actor, emoji := range held {
					view.Reactions[id][actor] = emoji
				}
			}
		}
	}
	for id := range s.revoked {
		view.Revoked = append(view.Revoked, id)
	}
	sort.Strings(view.Revoked)
	return view
}

func restoredView(t *testing.T, kept *messageStore) backupView {
	t.Helper()
	back := newStore()
	back.setSelf(backupViewer)
	kept.restore(context.Background(), back)
	return viewOfStore(back)
}

func fileState(t *testing.T, path string) string {
	t.Helper()
	info, err := os.Stat(path)
	backupMust(t, err)
	data, err := os.ReadFile(path)
	backupMust(t, err)
	return fmt.Sprintf("%x %d %d", sha256.Sum256(data), info.Size(), info.ModTime().UnixNano())
}

func tableCount(t *testing.T, kept *messageStore, name string) int {
	t.Helper()
	var count int
	backupMust(t, kept.db.QueryRow(`SELECT COUNT(*) FROM `+name).Scan(&count))
	return count
}

func TestBackupRoundTripRestoresHistoryIntoAFreshStore(t *testing.T) {
	budget := backupPageBudget
	backupPageBudget = 512
	t.Cleanup(func() { backupPageBudget = budget })
	logPath := filepath.Join(t.TempDir(), "helper.log")
	t.Setenv("NATIVEBEEPER_MESSENGER_LOG", logPath)
	openLog()
	defer closeLog()

	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	seedBackupSource(t, source)
	sourceFolder := filepath.Dir(source.kept.path)
	// The device's own keys sit beside the history. A backup must neither read nor carry them.
	const deviceMarker = "synthetic-device-identity-key-material"
	sourceDevice := filepath.Join(sourceFolder, "e2ee.db")
	backupMust(t, os.WriteFile(sourceDevice, []byte(deviceMarker), 0o600))
	deviceBefore := fileState(t, sourceDevice)
	storeBefore := fileState(t, source.kept.path)

	pages := exportAll(t, source)
	if len(pages) < 3 {
		t.Fatalf("a small page budget should have produced several pages, got %d", len(pages))
	}
	if _, err := os.Stat(filepath.Join(sourceFolder, backupSnapshotName)); !os.IsNotExist(err) {
		t.Fatal("the export's snapshot outlived the export")
	}
	if fileState(t, source.kept.path) != storeBefore {
		t.Fatal("exporting changed the live store")
	}
	var first, last map[string]json.RawMessage
	backupMust(t, json.Unmarshal(pages[0], &first))
	backupMust(t, json.Unmarshal(pages[len(pages)-1], &last))
	for _, field := range []string{"format", "version", "account", "export", "page", "expected", "next"} {
		if _, ok := first[field]; !ok {
			t.Fatalf("the first page has no %q", field)
		}
	}
	var expected, exported backupTally
	backupMust(t, json.Unmarshal(first["expected"], &expected))
	backupMust(t, json.Unmarshal(last["exported"], &exported))
	if expected.Messages != 4 || expected.Deletions != 1 || expected.Threads != 4 || expected.Contacts != 1 || expected.Mappings != 1 ||
		expected.Media != 1 || expected.Shares != 1 || expected.Attachments != 1 || expected.Reactions != 1 || expected.Orphans != 1 {
		t.Fatalf("unexpected expected counts %+v", expected)
	}
	expected.Orphans = 0
	if exported != expected {
		t.Fatalf("exported %+v, expected %+v", exported, expected)
	}
	for _, page := range pages {
		if bytes.Contains(page, []byte(deviceMarker)) {
			t.Fatal("the device file reached a backup page")
		}
		if bytes.Contains(page, []byte("Private taken back")) || bytes.Contains(page, []byte("orphan-1")) {
			t.Fatal("a deleted message or an orphan row was exported")
		}
	}

	target, events := backupAdapter(t, t.TempDir(), backupViewer)
	targetDevice := filepath.Join(filepath.Dir(target.kept.path), "e2ee.db")
	backupMust(t, os.WriteFile(targetDevice, []byte(deviceMarker), 0o600))
	targetDeviceBefore := fileState(t, targetDevice)
	total := importAll(t, target, pages)
	if total.Sections.Messages.Added != 4 || total.Sections.Media.Added != 1 || total.Sections.Shares.Added != 1 ||
		total.Sections.Attachments.Added != 1 || total.Sections.Reactions.Added != 1 || total.Sections.Deletions.Added != 1 ||
		total.Sections.Threads.Added != 4 || total.Sections.Contacts.Added != 1 || total.Sections.Mappings.Added != 1 ||
		total.Refused != 0 || total.Removed != 0 {
		t.Fatalf("unexpected import counts %+v", total)
	}
	restored := restoredView(t, target.kept)
	if !reflect.DeepEqual(restored, restoredView(t, source.kept)) {
		t.Fatalf("the imported store does not restore the same history:\n%+v\n%+v", restored, restoredView(t, source.kept))
	}
	// And that history is the whole of it, not two equally empty stores.
	if len(restored.Messages) != 4 || restored.Files["media-1"] != "01020304 image" || len(restored.Attached["m3"]) != 1 ||
		len(restored.Attached["m4"]) == 0 || restored.Reactions["m2"][555] != backupHeart || restored.Hybrid[5550] != 7 || !restored.Threads[5550].Deleted ||
		!restored.Threads[9].Deleted || restored.Threads[9].DeletedAt != 5000 || restored.Contacts[555] == "" || restored.Pictures[555] == "" ||
		!strings.Contains(restored.Messages["m3"], `card="Private card text"`) || !strings.Contains(restored.Messages["m1"], "edited=1500") ||
		!strings.Contains(restored.Messages["m3"], "encrypted=false") || len(restored.Revoked) != 1 {
		t.Fatalf("the restored history is incomplete: %+v", restored)
	}
	// Shown straight away, not only after the next sign-in, and announced so the app redraws those conversations.
	if len(target.chats.history(7, 0)) != 3 || len(target.chats.history(8, 0)) != 1 || !target.chats.isRevoked("m5") {
		t.Fatal("the imported history is not in the running helper's conversations")
	}
	if !strings.Contains(events.String(), `"event":"changed"`) || !strings.Contains(events.String(), `"m2"`) {
		t.Fatal("the import did not announce what it changed")
	}
	raw, err := os.ReadFile(target.kept.path)
	backupMust(t, err)
	for _, secret := range []string{"Private body", "Private person name", "private-face", "Private card text", "private-source"} {
		if bytes.Contains(raw, []byte(secret)) {
			t.Fatal("an imported value was written to SQLite unsealed")
		}
	}
	if fileState(t, sourceDevice) != deviceBefore || fileState(t, targetDevice) != targetDeviceBefore {
		t.Fatal("the backup touched a device file")
	}
	closeLog()
	log, err := os.ReadFile(logPath)
	backupMust(t, err)
	for _, secret := range []string{"Private", "example.test", "m1", "media-1", "att-1", messengerAccountKey(backupViewer)} {
		if strings.Contains(string(log), secret) {
			t.Fatalf("the diagnostic log recorded a value: %q", secret)
		}
	}
	for _, signal := range []string{"backup export started snapshot=", "backup export ended reason=finished", "backup import page=0 added="} {
		if !strings.Contains(string(log), signal) {
			t.Fatalf("the diagnostic log is missing %q", signal)
		}
	}
}

func TestBackupImportRefusesAnotherAccountAndUnknownFormats(t *testing.T) {
	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	seedBackupSource(t, source)
	pages := exportAll(t, source)
	other, _ := backupAdapter(t, t.TempDir(), 2002)
	for _, page := range pages {
		if _, code := other.backupImport(context.Background(), page); code != "backup_other_account" {
			t.Fatalf("another account's page was not refused: %q", code)
		}
	}
	for _, name := range []string{"encrypted_message", "deleted_message", "thread_identity", "thread_contact"} {
		if tableCount(t, other.kept, name) != 0 {
			t.Fatal("a refused page still wrote to the store")
		}
	}
	var page map[string]any
	backupMust(t, json.Unmarshal(pages[0], &page))
	page["version"] = backupVersion + 1
	future, _ := json.Marshal(page)
	if _, code := source.backupImport(context.Background(), future); code != "backup_unsupported" {
		t.Fatalf("a newer format was not refused: %q", code)
	}
	if _, code := source.backupImport(context.Background(), json.RawMessage(`{"format":`)); code != "invalid_input" {
		t.Fatal("a malformed page was not refused as input")
	}
}

func TestBackupImportIsIdempotent(t *testing.T) {
	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	seedBackupSource(t, source)
	pages := exportAll(t, source)
	target, _ := backupAdapter(t, t.TempDir(), backupViewer)
	first := importAll(t, target, pages)
	counts := map[string]int{}
	tables := []string{"encrypted_message", "encrypted_media", "encrypted_share", "message_attachment", "encrypted_reaction",
		"deleted_message", "thread_identity", "thread_contact", "thread_mapping"}
	for _, name := range tables {
		counts[name] = tableCount(t, target.kept, name)
	}
	before := restoredView(t, target.kept)
	again := importAll(t, target, pages)
	if first.Added == 0 || again.Added != 0 || again.Removed != 0 || again.Refused != 0 || again.Skipped != first.Added {
		t.Fatalf("a second import was not a no-op: first %+v, again %+v", first, again)
	}
	for _, name := range tables {
		if tableCount(t, target.kept, name) != counts[name] {
			t.Fatalf("a second import changed %s", name)
		}
	}
	if !reflect.DeepEqual(restoredView(t, target.kept), before) {
		t.Fatal("a second import changed what the store restores")
	}
	// Exporting the imported store and importing that into itself changes nothing either.
	if again := importAll(t, target, exportAll(t, target)); again.Added != 0 || again.Removed != 0 {
		t.Fatalf("re-importing the store's own export changed it: %+v", again)
	}
}

func TestBackupImportHonoursDeletionsBothWays(t *testing.T) {
	ctx := context.Background()
	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	s := source.chats
	s.mu.Lock()
	for _, key := range []int64{7, 8, 10} {
		s.get(key).name = "Private title"
	}
	s.get(10).deleted, s.get(10).deletedAt = true, 6000
	s.mu.Unlock()
	source.kept.saveIdentity(ctx, s)
	for _, m := range []struct {
		thread int64
		id     string
		at     int64
	}{{7, "A", 1000}, {7, "B", 2000}, {7, "F", 2500}, {8, "C", 3000}, {7, "D", 2600}} {
		backupMust(t, source.kept.rememberChecked(ctx, m.thread, m.id, 555, "Private "+m.id, "", m.at))
	}
	source.kept.rememberReaction(ctx, 7, "B", 555, backupHeart)
	source.kept.forget(ctx, 7, "D")

	target, events := backupAdapter(t, t.TempDir(), backupViewer)
	ts := target.chats
	ts.mu.Lock()
	for _, key := range []int64{7, 8, 10} {
		ts.get(key).name = "Private title"
	}
	ts.get(8).deleted, ts.get(8).deletedAt = true, 3500
	ts.mu.Unlock()
	target.kept.saveIdentity(ctx, ts)
	// Deleted here after the backup was taken.
	backupMust(t, target.kept.rememberChecked(ctx, 7, "A", 555, "Private A", "", 1000))
	target.kept.forget(ctx, 7, "A")
	// Here, with its reaction taken away since.
	backupMust(t, target.kept.rememberChecked(ctx, 7, "B", 555, "Private B", "", 2000))
	// Deleted in the backup, still here.
	backupMust(t, target.kept.rememberChecked(ctx, 7, "D", 555, "Private D", "", 2600))
	// In a conversation the backup deleted later than this computer knows.
	backupMust(t, target.kept.rememberChecked(ctx, 10, "E", 555, "Private E", "", 5500))
	backupRelogin(target)
	if !target.chats.hasMessage(7, "D") || !target.chats.hasMessage(10, "E") {
		t.Fatal("the target was not set up")
	}

	total := importAll(t, target, exportAll(t, source))
	sections := total.Sections
	if sections.Messages.Added != 1 || sections.Messages.Refused != 2 || sections.Messages.Skipped != 1 {
		t.Fatalf("messages: %+v", sections.Messages)
	}
	if sections.Reactions.Added != 0 || sections.Reactions.Skipped != 1 {
		t.Fatalf("a reaction taken away here was put back: %+v", sections.Reactions)
	}
	if sections.Deletions.Added != 1 || sections.Deletions.Removed != 1 || sections.Threads.Removed != 1 || total.Cutoffs != 1 {
		t.Fatalf("the backup's deletions were not applied: %+v cutoffs=%d", sections, total.Cutoffs)
	}
	view := restoredView(t, target.kept)
	for _, gone := range []string{"A", "C", "D", "E"} {
		if _, ok := view.Messages[gone]; ok {
			t.Fatalf("%s came back after being deleted", gone)
		}
		if target.chats.hasMessage(7, gone) || target.chats.hasMessage(8, gone) || target.chats.hasMessage(10, gone) {
			t.Fatalf("%s is still showing in the running helper", gone)
		}
	}
	if _, ok := view.Messages["F"]; !ok || len(view.Reactions["B"]) != 0 {
		t.Fatal("the new message was not restored, or the old reaction came back")
	}
	if target.chats.cutoffOf(10) != 6000 || view.Threads[10].DeletedAt != 6000 || view.Threads[8].DeletedAt != 3500 {
		t.Fatal("the conversation cutoffs were not merged")
	}
	var deletions int
	backupMust(t, target.kept.db.QueryRow(`SELECT COUNT(*) FROM deleted_message WHERE id IN ('A', 'D')`).Scan(&deletions))
	if deletions != 2 {
		t.Fatal("the deletion records were not both kept")
	}
	if !strings.Contains(events.String(), `"deleted":{"10":["E"],"7":["D"]}`) {
		t.Fatalf("the removals were not announced: %s", events.String())
	}
}

func TestBackupExportPagesAreBoundedConsistentAndComplete(t *testing.T) {
	ctx := context.Background()
	budget, limit := backupPageBudget, backupUnitLimit
	backupPageBudget, backupUnitLimit = 4096, 6000
	t.Cleanup(func() { backupPageBudget, backupUnitLimit = budget, limit })
	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	want := map[string]bool{}
	for index := range 150 {
		id := fmt.Sprintf("paged-%03d", index)
		backupMust(t, source.kept.rememberChecked(ctx, 7, id, 555, strings.Repeat("Private words ", 8)+string(rune(0x1F600)), "", int64(1000+index)))
		want[id] = true
	}
	backupMust(t, source.kept.rememberChecked(ctx, 7, "huge", 555, strings.Repeat("x", 8000), "", 900))

	first, code := source.backupExport(ctx, json.RawMessage(`{"cursor":""}`))
	if code != "" {
		t.Fatal(code)
	}
	firstPage := first.(*backupPageOut)
	// Written after the export began: the snapshot must not see it, however the pages fall.
	backupMust(t, source.kept.rememberChecked(ctx, 7, "late", 555, "Private late", "", 99999))
	if _, code := source.backupExport(ctx, json.RawMessage(`{"cursor":"nonsense"}`)); code != "invalid_input" {
		t.Fatal("a malformed cursor was accepted")
	}
	if _, code := source.backupExport(ctx, json.RawMessage(`{"cursor":"0000000000000000.4.0"}`)); code != "backup_expired" {
		t.Fatal("a cursor from another export was accepted")
	}
	pages := []*backupPageOut{firstPage}
	for cursor := firstPage.Next; cursor != ""; {
		params, _ := json.Marshal(map[string]string{"cursor": cursor})
		result, code := source.backupExport(ctx, params)
		if code != "" {
			t.Fatal(code)
		}
		page := result.(*backupPageOut)
		if len(pages) == 2 && page.Next != "" {
			// An answer that went missing is asked for again and comes back the same, counted once.
			again, code := source.backupExport(ctx, params)
			if code != "" || again.(*backupPageOut) != page {
				t.Fatal("asking for the same page again did not answer it again")
			}
			// Jumping ahead would silently skip rows, so only the cursor handed out is answered.
			id, section, rowid, _ := parseBackupCursor(page.Next)
			skipped, _ := json.Marshal(map[string]string{"cursor": backupCursor(id, section, rowid+1)})
			if _, code := source.backupExport(ctx, skipped); code != "backup_expired" {
				t.Fatal("a cursor that was never handed out was answered")
			}
		}
		pages = append(pages, page)
		cursor = page.Next
		if len(pages) > 200 {
			t.Fatal("the export did not advance")
		}
	}
	if len(pages) < 5 {
		t.Fatalf("expected many pages, got %d", len(pages))
	}
	seen := map[string]bool{}
	for index, page := range pages {
		if page.Page != index || page.Export != firstPage.Export {
			t.Fatal("pages are out of sequence")
		}
		records := 0
		size := 0
		for _, record := range page.Messages {
			var m backupMessage
			backupMust(t, json.Unmarshal(record, &m))
			if seen[m.ID] {
				t.Fatalf("%s was exported twice", m.ID)
			}
			seen[m.ID] = true
			size += escapedSize(record)
			records++
		}
		if records > 1 && size > backupPageBudget {
			t.Fatalf("page %d holds %d escaped bytes over a %d budget", index, size, backupPageBudget)
		}
		raw, _ := json.Marshal(page)
		if escapedSize(raw) > max(backupPageBudget, backupUnitLimit)+1024+records {
			t.Fatal("a page could not travel back through the request line")
		}
	}
	if !reflect.DeepEqual(seen, want) {
		t.Fatalf("exported %d messages, wanted %d; late or huge ones must be absent", len(seen), len(want))
	}
	final := pages[len(pages)-1].Exported
	if final == nil || final.Messages != 150 || final.Oversized != 1 || firstPage.Expected.Messages != 151 {
		t.Fatalf("the totals do not account for every message: %+v", final)
	}
	if _, code := source.backupExport(ctx, json.RawMessage(`{"cursor":"`+firstPage.Next+`"}`)); code != "backup_expired" {
		t.Fatal("a finished export kept answering")
	}
}

func TestBackupExportIsAbandonedWhenIdleOrEnded(t *testing.T) {
	ctx := context.Background()
	budget, idle := backupPageBudget, backupIdle
	backupPageBudget, backupIdle = 256, 40*time.Millisecond
	t.Cleanup(func() { backupPageBudget, backupIdle = budget, idle })
	source, _ := backupAdapter(t, t.TempDir(), backupViewer)
	seedBackupSource(t, source)
	snapshot := filepath.Join(filepath.Dir(source.kept.path), backupSnapshotName)
	result, code := source.backupExport(ctx, json.RawMessage(`{"cursor":""}`))
	if code != "" || result.(*backupPageOut).Next == "" {
		t.Fatal("the export should have more than one page")
	}
	if _, err := os.Stat(snapshot); err != nil {
		t.Fatal("the export holds no snapshot")
	}
	deadline := time.Now().Add(3 * time.Second)
	for {
		if _, err := os.Stat(snapshot); os.IsNotExist(err) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("an idle export kept its snapshot")
		}
		time.Sleep(20 * time.Millisecond)
	}
	params, _ := json.Marshal(map[string]string{"cursor": result.(*backupPageOut).Next})
	if _, code := source.backupExport(ctx, params); code != "backup_expired" {
		t.Fatal("an abandoned export kept answering")
	}

	backupIdle = time.Minute
	if _, code := source.backupExport(ctx, json.RawMessage(`{}`)); code != "" {
		t.Fatal(code)
	}
	if _, code := source.backupCall(ctx, request{Method: "backup.exportEnd"}); code != "" {
		t.Fatal("ending an export failed")
	}
	if _, err := os.Stat(snapshot); !os.IsNotExist(err) {
		t.Fatal("an ended export kept its snapshot")
	}
	// Left behind by a helper that stopped mid-export, and gone at the next sign-in.
	backupMust(t, os.WriteFile(snapshot, []byte("stale"), 0o600))
	discardStaleBackup(source.kept)
	if _, err := os.Stat(snapshot); !os.IsNotExist(err) {
		t.Fatal("a stale snapshot survived sign-in")
	}
}

func TestBackupMethodsAreLocalAndNeedASignedInStore(t *testing.T) {
	ctx := context.Background()
	for _, method := range []string{"backup.export", "backup.exportEnd", "backup.import"} {
		// Neither a lifecycle step nor a contact-visible mutation: nothing waits on them and they wait on nothing.
		if lifecycleMethod(method) || mutationMethod(method) {
			t.Fatalf("%s is queued like an account action", method)
		}
	}
	a := newAdapter(nil)
	defer a.stop()
	for _, method := range []string{"backup.export", "backup.import"} {
		if _, code := a.callContext(ctx, request{ID: 1, Method: method, Params: json.RawMessage(`{}`)}); code != "login_required" {
			t.Fatalf("%s ran before sign-in: %q", method, code)
		}
	}
	if _, code := a.callContext(ctx, request{ID: 1, Method: "backup.exportEnd"}); code != "" {
		t.Fatal("ending an export that never started failed")
	}
	// Signed in, but with no store to read: said plainly rather than exporting nothing.
	if _, code := a.backupExport(ctx, nil); code != "backup_unavailable" {
		t.Fatalf("an export ran without a store: %q", code)
	}
	a.observeReceipts = true
	for _, method := range []string{"backup.export", "backup.exportEnd", "backup.import"} {
		if _, code := a.callContext(ctx, request{ID: 1, Method: method}); code != "observation_only" {
			t.Fatalf("the observation process ran %s", method)
		}
	}
}

func TestBackupFormatCarriesEveryRetainedThreadField(t *testing.T) {
	// A field added to the retained identity and not to the backup would be silently lost in every restore.
	if fields := reflect.TypeOf(savedThread{}).NumField(); fields != 17 {
		t.Fatalf("savedThread has %d fields; carry the new one in backupThread, then update this count", fields)
	}
	if escapedSize([]byte(`a"<`)) != 13 || escapedSize([]byte(string(rune(0xE9)))) != 6 || escapedSize([]byte(string(rune(0x1F600)))) != 12 {
		t.Fatal("the escaped size does not count .NET's escaping")
	}
}
