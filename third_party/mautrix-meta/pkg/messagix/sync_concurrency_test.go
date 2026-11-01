package messagix

import (
	"encoding/json"
	"fmt"
	"sync"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func testSyncManager() *SyncManager {
	client := &Client{Logger: zerolog.Nop()}
	manager := client.newSyncManager()
	client.syncManager = manager
	return manager
}

func TestSyncSnapshotsDoNotExposeMutableState(t *testing.T) {
	sm := testSyncManager()
	transaction := &table.LSExecuteFirstBlockForSyncTransactionV4{DatabaseID: 1, NextCursor: "first", CurrentSeqID: 17}
	if err := sm.SyncTransactions([]*table.LSExecuteFirstBlockForSyncTransactionV4{transaction}); err != nil {
		t.Fatal(err)
	}
	transaction.NextCursor = "caller-mutated"
	if sm.GetCursor(1) != "first" {
		t.Fatal("cursor aliases received table")
	}
	snapshot, ok := sm.databaseSnapshot(1)
	if !ok || *snapshot.LastAppliedCursor != "first" {
		t.Fatal("missing snapshot")
	}
	*snapshot.LastAppliedCursor = "changed"
	snapshot.CurrentSeqID = 0
	owned := sm.storeSnapshot()
	owned[1].LastAppliedCursor = nil
	delete(owned, 2)
	actual, _ := sm.databaseSnapshot(1)
	if *actual.LastAppliedCursor != "first" || actual.CurrentSeqID != 17 {
		t.Fatal("database snapshot changed live store")
	}
	if _, ok := sm.databaseSnapshot(2); !ok {
		t.Fatal("dump snapshot deleted live store")
	}
	rangeRow := &table.LSUpsertSyncGroupThreadsRange{SyncGroup: 95, HasMoreBefore: true, MinLastActivityTimestampMS: 42, MinThreadKey: 123}
	if err := sm.updateThreadRanges([]*table.LSUpsertSyncGroupThreadsRange{rangeRow}); err != nil {
		t.Fatal(err)
	}
	held := sm.getSyncGroupKeyStore(95)
	held.MinThreadKey = 456
	current := sm.getSyncGroupKeyStore(95)
	if current.MinThreadKey != 123 {
		t.Fatal("range snapshot changed live store")
	}
	rangeRow.MinThreadKey = 789
	sm.updateThreadRanges([]*table.LSUpsertSyncGroupThreadsRange{rangeRow})
	if held.MinThreadKey != 456 || current.MinThreadKey != 123 {
		t.Fatal("live update mutated retained range snapshot")
	}
	if sm.GetCursor(999) != "" || sm.getSyncGroupKeyStore(999) != nil {
		t.Fatal("unknown group was invented")
	}
}

func TestThreadRangeWithNothingOlderEndsPaging(t *testing.T) {
	sm := testSyncManager()
	update := func(row *table.LSUpsertSyncGroupThreadsRange) {
		t.Helper()
		if err := sm.updateThreadRanges([]*table.LSUpsertSyncGroupThreadsRange{row}); err != nil {
			t.Fatal(err)
		}
	}
	update(&table.LSUpsertSyncGroupThreadsRange{SyncGroup: 95, HasMoreBefore: true, MinThreadKey: 123})
	update(&table.LSUpsertSyncGroupThreadsRange{SyncGroup: 95, ParentThreadKey: 7})
	if !sm.getSyncGroupKeyStore(95).HasMoreBefore {
		t.Fatal("a range under one parent thread ended the whole list")
	}
	update(&table.LSUpsertSyncGroupThreadsRange{SyncGroup: 95})
	current := sm.getSyncGroupKeyStore(95)
	if current.HasMoreBefore {
		t.Fatal("the list still claims older threads after Facebook said there are none")
	}
	if current.MinThreadKey != 123 {
		t.Fatal("the end of the list moved the cursor")
	}
}

func TestCursorRangeReadersAndSyncWorkersRunConcurrently(t *testing.T) {
	sm := testSyncManager()
	var workers sync.WaitGroup
	for worker := range 16 {
		workers.Add(1)
		go func(worker int) {
			defer workers.Done()
			for update := range 250 {
				group := int64(1)
				if worker%2 != 0 {
					group = 95
				}
				cursor := fmt.Sprintf("synthetic-%d-%d", worker, update)
				if err := sm.SyncTransactions([]*table.LSExecuteFirstBlockForSyncTransactionV4{{DatabaseID: group, NextCursor: cursor, CurrentSeqID: int64(update)}}); err != nil {
					t.Error(err)
					return
				}
				if err := sm.updateThreadRanges([]*table.LSUpsertSyncGroupThreadsRange{{SyncGroup: group, HasMoreBefore: true, MinThreadKey: int64(update)}}); err != nil {
					t.Error(err)
					return
				}
				snapshot, ok := sm.databaseSnapshot(group)
				if !ok || snapshot.LastAppliedCursor == nil {
					t.Error("missing cursor snapshot")
					return
				}
				before := *snapshot.LastAppliedCursor
				rangeSnapshot := sm.getSyncGroupKeyStore(group)
				rangeBefore := *rangeSnapshot
				if _, err := json.Marshal(sm.storeSnapshot()); err != nil {
					t.Error(err)
					return
				}
				sm.GetCursor(group)
				if *snapshot.LastAppliedCursor != before || *rangeSnapshot != rangeBefore {
					t.Error("retained snapshot changed during concurrent request")
					return
				}
			}
		}(worker)
	}
	workers.Wait()
}
