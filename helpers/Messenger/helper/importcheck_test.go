package main

import (
	"encoding/json"
	"testing"
)

// An import that could not save the threads and contacts of a page must fail the page, not count them: the move compares
// what was reported with what is there, and a page that reported rows it did not keep is a history that is not whole.
func TestImportFailsWhenThreadsAndContactsCannotBeSaved(t *testing.T) {
	budget := backupPageBudget
	backupPageBudget = 512
	t.Cleanup(func() { backupPageBudget = budget })
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "1")
	account := `{"account":"1001","create":true}`

	moveRoot(t)
	source := moveAdapter(t)
	if _, code := moveCall(t, source, "backup.open", account); code != "" {
		t.Fatalf("source open: %q", code)
	}
	seedBackupSource(t, source)
	pages, _, _ := offlineExport(t, source)

	moveRoot(t)
	target := moveAdapter(t)
	if _, code := moveCall(t, target, "backup.open", account); code != "" {
		t.Fatalf("target open: %q", code)
	}
	// The identity table cannot be written (a disk that fills, a store that is damaged): the page that carries threads must fail.
	if _, err := target.kept.db.Exec(`DROP TABLE thread_identity`); err != nil {
		t.Fatal(err)
	}
	failed := false
	for _, page := range pages {
		var shape struct { //nolint
			Threads []json.RawMessage `json:"threads"`
		}
		backupMust(t, json.Unmarshal(page, &shape))
		if _, code := moveCall(t, target, "backup.import", string(page)); code != "" {
			if code != "backup_failed" {
				t.Fatalf("a page whose threads could not be saved answered %q", code)
			}
			failed = true
		}
	}
	if !failed {
		t.Fatal("no page failed although the identity rows could not be saved")
	}
}

// backup.count reads the store back, in the sections an export counts, so a move checks the database and not what the importer said.
func TestBackupCountReadsTheStoreBack(t *testing.T) {
	budget := backupPageBudget
	backupPageBudget = 512
	t.Cleanup(func() { backupPageBudget = budget })
	t.Setenv("MESSAGELINK_MESSENGER_OFFLINE", "1")
	account := `{"account":"1001","create":true}`

	moveRoot(t)
	source := moveAdapter(t)
	if _, code := moveCall(t, source, "backup.open", account); code != "" {
		t.Fatalf("source open: %q", code)
	}
	seedBackupSource(t, source)
	pages, _, exported := offlineExport(t, source)

	moveRoot(t)
	target := moveAdapter(t)
	if _, code := moveCall(t, target, "backup.open", account); code != "" {
		t.Fatalf("target open: %q", code)
	}
	empty, code := moveCall(t, target, "backup.count", "")
	if code != "" || empty.(backupTally).Messages != 0 {
		t.Fatalf("an empty store counted %+v (%q)", empty, code)
	}
	for _, page := range pages {
		if _, code := moveCall(t, target, "backup.import", string(page)); code != "" {
			t.Fatalf("import answered %q", code)
		}
	}
	held, code := moveCall(t, target, "backup.count", "")
	if code != "" {
		t.Fatalf("count answered %q", code)
	}
	got := held.(backupTally)
	if got.Contacts != exported.Contacts || got.Threads != exported.Threads || got.Mappings != exported.Mappings || got.Deletions != exported.Deletions || got.Messages != exported.Messages ||
		got.Media != exported.Media || got.Shares != exported.Shares || got.Attachments != exported.Attachments || got.Reactions != exported.Reactions || got.Receipts != exported.Receipts {
		t.Fatalf("the store holds %+v but the export wrote %+v", got, exported)
	}
	// Before backup.open there is no store to count.
	bare := moveAdapter(t)
	if _, code := moveCall(t, bare, "backup.count", ""); code != "backup_unavailable" {
		t.Fatalf("a count with no store answered %q", code)
	}
}
