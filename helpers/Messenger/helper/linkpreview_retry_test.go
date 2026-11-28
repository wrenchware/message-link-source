package main

import (
	"context"
	"sync/atomic"
	"testing"
)

func TestLinkPreviewExpiredImageRefreshesOnceThenKeepsLink(t *testing.T) {
	var requests atomic.Int32
	a, events := previewAdapter(t, func(context.Context, string) (*linkPreviewResult, error) {
		requests.Add(1)
		return fixturePreview(), nil
	})
	const link = "https://example.test/reel"
	a.chats.addEncrypted(7, "synthetic-expiring", 4, link, "", 1700)
	a.loadLinkPreviews(7, []string{"synthetic-expiring"})
	awaitPreviewSignal(t, events.changed)
	first := a.chats.history(7, 0)[0].Media[0].ID
	a.retryLinkPreview(first)
	awaitPreviewSignal(t, events.changed) // Removed stale picture.
	awaitPreviewSignal(t, events.changed) // Refreshed page supplied another picture.
	if requests.Load() != 2 {
		t.Fatal("an expired image must trigger one metadata refresh")
	}
	a.retryLinkPreview(first)
	awaitPreviewSignal(t, events.changed)
	a.loadLinkPreviews(7, []string{"synthetic-expiring"})
	a.stop()
	a.previewJobs.wg.Wait()
	page := a.chats.history(7, 0)
	if requests.Load() != 2 || len(page) != 1 || page[0].Text != link || len(page[0].Media) != 0 {
		t.Fatal("repeated image failure must cool down and retain the original link without another fetch loop")
	}
}
