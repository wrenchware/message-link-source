package main

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// A diagnostic log, for working out why Facebook answered the way it did. Counts, states and error codes only: no
// names, message text, snippets, urls, cookies or raw identifiers ever reach it. Conversations and people appear as a
// short one-way tag, which is enough to follow one of them through the log and nothing else.
const logLimit = 4 << 20

type logger struct {
	mu      sync.Mutex
	file    *os.File
	written int64
}

var diagnostics = &logger{}

// %LOCALAPPDATA%\Messages for Windows\Messenger\helper.log, beside the session the app already keeps there, or inside the
// data root when one is set, as every database already is. A development copy started with only the root used to
// rotate the installed app's log to .previous here, and the record of the real run went with it.
func logPath() string {
	// A diagnostic run alongside the app sets this, so the two do not write over each other's log.
	if named := os.Getenv("MESSAGELINK_MESSENGER_LOG"); named != "" {
		return named
	}
	root := messengerStorageRoot()
	if root == "" {
		return ""
	}
	return filepath.Join(root, "helper.log")
}

func openLog() {
	path := logPath()
	if path == "" {
		return
	}
	if os.MkdirAll(filepath.Dir(path), 0o700) != nil {
		return
	}
	// The run before this one is kept alongside. A helper that stops unexpectedly is restarted straight away, and
	// replacing the log outright destroyed the account of why it stopped before anyone could read it.
	_ = os.Remove(path + ".previous")
	_ = os.Rename(path, path+".previous")
	file, err := os.Create(path)
	if err != nil {
		return
	}
	diagnostics.mu.Lock()
	diagnostics.file = file
	diagnostics.written = 0
	diagnostics.mu.Unlock()
	logf("helper started, protocol %d, %s", protocolVersion, libraryVersion)
}

func logf(format string, args ...any) {
	diagnostics.mu.Lock()
	defer diagnostics.mu.Unlock()
	if diagnostics.file == nil || diagnostics.written > logLimit {
		return
	}
	line := time.Now().Format("15:04:05.000") + " " + fmt.Sprintf(format, args...) + "\n"
	written, err := diagnostics.file.WriteString(line)
	if err != nil {
		diagnostics.file = nil
		return
	}
	diagnostics.written += int64(written)
	if diagnostics.written > logLimit {
		_, _ = diagnostics.file.WriteString("log limit reached; nothing further is recorded\n")
	}
}

func closeLog() {
	diagnostics.mu.Lock()
	defer diagnostics.mu.Unlock()
	if diagnostics.file != nil {
		_ = diagnostics.file.Close()
		diagnostics.file = nil
	}
}

// What a batch of rows contained, by kind. The kinds are what the store reads, plus the ones whose absence explains
// an empty conversation.
func logRows(source string, tbl *table.LSTable, touched *changes) {
	if tbl == nil {
		return
	}
	toArchive, toInbox, archivedByName, archivedByParent := folderRowCounts(tbl)
	logf("%s rows insertThread=%d updateThread=%d deleteThread=%d deletePartial=%d deleteRequest=%d upsertMsg=%d insertMsg=%d range=%d deleteMsg=%d unsend=%d contact=%d verify=%d participant=%d blob=%d xma=%d item=%d cta=%d replace=%d failed=%d hybrid=%d toArchive=%d toInbox=%d archivedByName=%d archivedByParent=%d | chats=%d messages=%d deleted=%d receipts=%d",
		source,
		len(tbl.LSDeleteThenInsertThread), len(tbl.LSUpdateOrInsertThread),
		len(tbl.LSDeleteThread), len(tbl.LSDeletePartialThread), len(tbl.LSDeleteMessageRequest),
		len(tbl.LSUpsertMessage), len(tbl.LSInsertMessage), len(tbl.LSInsertNewMessageRange),
		len(tbl.LSDeleteMessage), len(tbl.LSDeleteThenInsertMessage),
		len(tbl.LSDeleteThenInsertContact), len(tbl.LSVerifyContactRowExists), len(tbl.LSVerifyContactParticipantExist),
		len(tbl.LSInsertBlobAttachment), len(tbl.LSInsertXmaAttachment), len(tbl.LSInsertAttachmentItem), len(tbl.LSInsertAttachmentCta),
		len(tbl.LSReplaceOptimsiticMessage), len(tbl.LSMarkOptimisticMessageFailed)+len(tbl.LSHandleFailedTask),
		len(tbl.LSVerifyHybridThreadExists),
		toArchive, toInbox, archivedByName, archivedByParent,
		len(touched.Chats), len(touched.Messages), len(touched.Deleted), len(touched.Receipts))
	logMessageShape(source, tbl)
}

// What the message rows in a batch look like, without any of what they say: how many carry text, how many name a
// sender, and what authority levels Facebook marked them with. A conversation answered with bare rows looks quite
// different here from one answered properly, which is the difference worth seeing.
func logMessageShape(source string, tbl *table.LSTable) {
	if len(tbl.LSUpsertMessage) == 0 {
		return
	}
	text, sender, unsent := 0, 0, 0
	authority := map[int64]int{}
	for _, row := range tbl.LSUpsertMessage {
		if row.Text != "" {
			text++
		}
		if row.SenderId != 0 {
			sender++
		}
		if row.IsUnsent {
			unsent++
		}
		authority[row.AuthorityLevel]++
	}
	levels := make([]string, 0, len(authority))
	for level, count := range authority {
		levels = append(levels, fmt.Sprintf("%d:%d", level, count))
	}
	sort.Strings(levels)
	logf("%s messages=%d withText=%d withSender=%d unsent=%d authority=%s",
		source, len(tbl.LSUpsertMessage), text, sender, unsent, strings.Join(levels, ","))
}

// A conversation or person as six characters that cannot be turned back into the identifier.
func tag(id int64) string {
	if id == 0 {
		return "none"
	}
	sum := sha256.Sum256([]byte("messenger-helper:" + strconv.FormatInt(id, 10)))
	return hex.EncodeToString(sum[:3])
}

func tagOf(id string) string {
	if id == "" {
		return "none"
	}
	sum := sha256.Sum256([]byte("messenger-helper:" + id))
	return hex.EncodeToString(sum[:3])
}
