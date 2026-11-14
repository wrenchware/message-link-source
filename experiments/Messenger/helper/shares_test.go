package main

import (
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/lightspeed"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// These are synthetic rows using the pinned v0.2608.1 schema, not captured account data.
// attachments.go gives AttachmentItem its own ThreadKey (2), MessageId (4), image
// fields (18, 19, 24, 52), and DefaultActionUrl (30). WrapMessages joins XMA to its
// message by MessageId and CTA to XMA by AttachmentFbid; it never consumes items.
// In particular, these tests do not assume an item's FBID equals a container's FBID.
func shareTestMessage(s *store, id string) {
	s.apply(&table.LSTable{LSUpsertMessage: []*table.LSUpsertMessage{
		{ThreadKey: 4, MessageId: id, TimestampMs: 10},
	}})
}

func shareTestItem(id, fbid string) *table.LSInsertAttachmentItem {
	return &table.LSInsertAttachmentItem{
		ThreadKey: 4, MessageId: id, AttachmentFbid: fbid,
		TitleText: "Synthetic share", PreviewUrl: "https://example.test/thumbnail.jpg",
		PreviewUrlMimeType: "image/jpeg", PreviewWidth: 240, PreviewHeight: 320,
		DefaultActionUrl: "https://example.test/shared-post",
	}
}

func shareTestMedia(t *testing.T, s *store, id string, count int) []viewMedia {
	t.Helper()
	for _, msg := range s.history(4, 0) {
		if msg.ID == id {
			if len(msg.Media) != count {
				t.Fatalf("share %q has %d attachments, want %d", id, len(msg.Media), count)
			}
			return msg.Media
		}
	}
	t.Fatalf("share %q is missing from history", id)
	return nil
}

func shareTestTouched(t *testing.T, touched *changes, id string) {
	t.Helper()
	if touched == nil || !slices.Contains(touched.Messages["4"], id) {
		t.Fatalf("the message gaining share content must be announced: %+v", touched)
	}
}

func TestShareItemFromPinnedLightspeedSchema(t *testing.T) {
	// Explicit argument positions are copied from v0.2608.1 attachments.go. This
	// exercises the real decoder before store.apply; it is not a live row capture.
	args := make([]any, 53)
	args[0] = "wire-item"
	args[1] = []any{float64(lightspeed.I64_FROM_STRING), "2"}
	args[2] = []any{float64(lightspeed.I64_FROM_STRING), "4"}
	args[4] = "wire-message"
	args[8] = "Synthetic wire share"
	args[18] = "https://example.test/wire.jpg"
	args[21] = "image/jpeg"
	args[22] = []any{float64(lightspeed.I64_FROM_STRING), "240"}
	args[23] = []any{float64(lightspeed.I64_FROM_STRING), "320"}
	args[30] = "https://example.test/wire-post"
	rows := &table.LSTable{}
	decoder := lightspeed.NewLightSpeedDecoder(lightspeed.DependencyMap{
		"insertAttachmentItem": "LSInsertAttachmentItem",
	}, rows)
	call := append([]any{float64(lightspeed.CALL_STORED_PROCEDURE), "insertAttachmentItem"}, args...)
	decoder.Decode(call)
	if len(rows.LSInsertAttachmentItem) != 1 {
		t.Fatalf("expected one decoded attachment item, got %d", len(rows.LSInsertAttachmentItem))
	}
	item := rows.LSInsertAttachmentItem[0]
	if item.ThreadKey != 4 || item.MessageId != "wire-message" || item.AttachmentFbid != "wire-item" || item.AttachmentIndex != 2 {
		t.Fatalf("pinned decoder did not preserve item ownership and identity: %+v", item)
	}
	s := newStore()
	shareTestMessage(s, "wire-message")
	touched := s.apply(rows)
	media := shareTestMedia(t, s, "wire-message", 1)[0]
	if media.Link != "https://example.test/wire-post" || media.Name != "Synthetic wire share" || media.Video {
		t.Fatalf("decoded item must draw as the linked thumbnail it describes: %+v", media)
	}
	shareTestTouched(t, touched, "wire-message")
}

func TestShareItemFollowsEmptyContainerByMessage(t *testing.T) {
	for _, containerFBID := range []string{"", "container-only", "item-id"} {
		t.Run("container_fbid_"+containerFBID, func(t *testing.T) {
			s := newStore()
			shareTestMessage(s, "share")
			s.apply(&table.LSTable{LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
				{ThreadKey: 4, MessageId: "share", AttachmentFbid: containerFBID,
					AttachmentType: table.AttachmentTypeXMA},
			}})
			touched := s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{
				shareTestItem("share", "item-id"),
			}})
			media := shareTestMedia(t, s, "share", 1)[0]
			if media.Link != "https://example.test/shared-post" || media.Video || media.Mime != "image/jpeg" {
				t.Fatalf("item should provide an image with its browser destination: %+v", media)
			}
			if media.Width != 240 || media.Height != 320 || media.Name != "Synthetic share" {
				t.Fatalf("item dimensions and title must survive: %+v", media)
			}
			shareTestTouched(t, touched, "share")
		})
	}
}

func TestShareItemDoesNotRequireAContainer(t *testing.T) {
	for _, itemFBID := range []string{"item-id", ""} {
		t.Run("item_fbid_"+itemFBID, func(t *testing.T) {
			s := newStore()
			// An item can precede even its message; that must not discard it.
			touched := s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{
				shareTestItem("share", itemFBID),
			}})
			shareTestTouched(t, touched, "share")
			shareTestMessage(s, "share")
			shareTestMedia(t, s, "share", 1)
			// A contentless container arriving afterwards must not erase or duplicate the item.
			s.apply(&table.LSTable{LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
				{ThreadKey: 4, MessageId: "share", AttachmentFbid: "container-only",
					AttachmentType: table.AttachmentTypeXMA},
			}})
			shareTestMedia(t, s, "share", 1)
		})
	}
}

func TestShareItemsKeepDistinctIndexesAndDeduplicateReplay(t *testing.T) {
	s := newStore()
	shareTestMessage(s, "carousel")
	first := shareTestItem("carousel", "shared-fbid")
	second := shareTestItem("carousel", "shared-fbid")
	second.AttachmentIndex = 1
	second.PreviewUrl = "https://example.test/second.jpg"
	second.DefaultActionUrl = "https://example.test/second-post"
	rows := &table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{first, second}}
	touched := s.apply(rows)
	shareTestTouched(t, touched, "carousel")
	media := shareTestMedia(t, s, "carousel", 2)
	if media[0].ID == media[1].ID {
		t.Fatal("distinct item indexes need distinct downloadable media ids")
	}
	links := []string{media[0].Link, media[1].Link}
	if !slices.Contains(links, first.DefaultActionUrl) || !slices.Contains(links, second.DefaultActionUrl) {
		t.Fatalf("both items must keep their own destinations: %v", links)
	}
	s.apply(rows)
	shareTestMedia(t, s, "carousel", 2)
}

func TestShareCTAWorksBeforeAndAfterItem(t *testing.T) {
	for _, reference := range []string{"cta_id", "attachment_fbid"} {
		for _, ctaFirst := range []bool{false, true} {
			name := reference + "/item_first"
			if ctaFirst {
				name = reference + "/cta_first"
			}
			t.Run(name, func(t *testing.T) {
				s := newStore()
				shareTestMessage(s, "share")
				item := shareTestItem("share", "item-id")
				item.DefaultActionUrl = ""
				cta := &table.LSInsertAttachmentCta{
					ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id",
					CtaId: 73, ActionUrl: "https://example.test/from-cta", Type_: "xma_web_url",
				}
				if reference == "cta_id" {
					item.DefaultCtaId = 73
					cta.AttachmentFbid = "cta-container-id"
				}
				itemRows := &table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}}
				ctaRows := &table.LSTable{LSInsertAttachmentCta: []*table.LSInsertAttachmentCta{cta}}
				var touched *changes
				if ctaFirst {
					s.apply(ctaRows)
					touched = s.apply(itemRows)
				} else {
					s.apply(itemRows)
					if got := shareTestMedia(t, s, "share", 1)[0].Link; got != "" {
						t.Fatalf("no destination has arrived yet: %q", got)
					}
					touched = s.apply(ctaRows)
				}
				media := shareTestMedia(t, s, "share", 1)[0]
				if media.Link != cta.ActionUrl {
					t.Fatalf("CTA destination was not joined: %+v", media)
				}
				shareTestTouched(t, touched, "share")
				s.apply(ctaRows)
				shareTestMedia(t, s, "share", 1)
			})
		}
	}
}

func TestShareCTACannotCrossMessages(t *testing.T) {
	s := newStore()
	shareTestMessage(s, "share")
	item := shareTestItem("share", "reused-fbid")
	item.DefaultActionUrl = ""
	item.DefaultCtaId = 73
	s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}})
	touched := s.apply(&table.LSTable{LSInsertAttachmentCta: []*table.LSInsertAttachmentCta{
		{ThreadKey: 4, MessageId: "another-message", AttachmentFbid: "reused-fbid",
			CtaId: 73, ActionUrl: "https://example.test/unrelated"},
	}})
	if got := shareTestMedia(t, s, "share", 1)[0].Link; got != "" {
		t.Fatalf("an unrelated message's CTA leaked onto the share: %q", got)
	}
	if slices.Contains(touched.Messages["4"], "share") {
		t.Fatalf("the unrelated CTA must not mark this share changed: %+v", touched)
	}
}

func TestShareThumbnailFallbacks(t *testing.T) {
	for _, field := range []string{"preview_fallback", "image", "preview_large"} {
		t.Run(field, func(t *testing.T) {
			s := newStore()
			shareTestMessage(s, "share")
			item := shareTestItem("share", "item-id")
			item.PreviewUrl = ""
			want := "https://example.test/fallback.jpg"
			switch field {
			case "preview_fallback":
				item.PreviewUrlFallback = want
			case "image":
				item.ImageUrl = want
			case "preview_large":
				item.PreviewUrlLarge = want
			}
			touched := s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}})
			media := shareTestMedia(t, s, "share", 1)[0]
			source, mime, ok := s.mediaSource(media.ID)
			if !ok || source != want || mime != "image/jpeg" || media.Video {
				t.Fatalf("thumbnail fallback was not registered as an image: source=%q mime=%q ok=%v", source, mime, ok)
			}
			shareTestTouched(t, touched, "share")
		})
	}
}

func TestShareVideoURLIsNeverUsedAsJPEGThumbnail(t *testing.T) {
	s := newStore()
	shareTestMessage(s, "share")
	item := shareTestItem("share", "item-id")
	item.PreviewUrl, item.PreviewUrlMimeType = "", ""
	item.PlayableUrl, item.PlayableUrlMimeType = "https://example.test/video.mp4", "video/mp4"
	s.apply(&table.LSTable{
		LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id", AttachmentType: table.AttachmentTypeXMA},
		},
	})
	// A renderer may omit a share without an image, or expose actual video. It must
	// never ask the image decoder to read an MP4 while labelling it image/jpeg.
	for _, msg := range s.history(4, 0) {
		for _, media := range msg.Media {
			source, mime, _ := s.mediaSource(media.ID)
			if source == item.PlayableUrl && (!media.Video || !strings.HasPrefix(mime, "video/")) {
				t.Fatalf("playable video masquerades as a thumbnail: %+v", media)
			}
		}
	}
}

func TestSharePartialItemKeepsThumbnailAndGainsLink(t *testing.T) {
	s := newStore()
	shareTestMessage(s, "share")
	item := shareTestItem("share", "item-id")
	item.DefaultActionUrl = ""
	s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}})
	before := shareTestMedia(t, s, "share", 1)[0]
	touched := s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{
		{ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id",
			DefaultActionUrl: "https://example.test/late-link"},
	}})
	media := shareTestMedia(t, s, "share", 1)[0]
	if media.Link != "https://example.test/late-link" {
		t.Fatalf("link-only item update did not enrich the visible card: %+v", media)
	}
	source, mime, ok := s.mediaSource(media.ID)
	if !ok || source != item.PreviewUrl || mime != "image/jpeg" {
		t.Fatalf("link-only update lost the existing picture: source=%q mime=%q ok=%v", source, mime, ok)
	}
	if media.ID != before.ID || media.Width != before.Width || media.Height != before.Height || media.Name != before.Name {
		t.Fatalf("partial update replaced established visual metadata: before=%+v after=%+v", before, media)
	}
	shareTestTouched(t, touched, "share")
}

func TestShareConflictingCTADestinationsStayUnresolved(t *testing.T) {
	for _, reverse := range []bool{false, true} {
		name := "forward"
		if reverse {
			name = "reverse"
		}
		t.Run(name, func(t *testing.T) {
			s := newStore()
			shareTestMessage(s, "share")
			item := shareTestItem("share", "item-id")
			item.DefaultActionUrl = ""
			item.DefaultCtaId = 73
			ctas := []*table.LSInsertAttachmentCta{
				{ThreadKey: 4, MessageId: "share", AttachmentFbid: "cta-a", CtaId: 73,
					ActionUrl: "https://example.test/first-destination"},
				{ThreadKey: 4, MessageId: "share", AttachmentFbid: "cta-b", CtaId: 73,
					ActionUrl: "https://example.test/conflicting-destination"},
			}
			if reverse {
				slices.Reverse(ctas)
			}
			s.apply(&table.LSTable{LSInsertAttachmentCta: ctas})
			// Repeated assembly must not make map iteration choose which destination wins.
			for attempt := 0; attempt < 8; attempt++ {
				s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}})
				if got := shareTestMedia(t, s, "share", 1)[0].Link; got != "" {
					t.Fatalf("conflicting explicit CTA id chose an arbitrary destination: %q", got)
				}
			}
		})
	}
}

// The shape a Marketplace listing arrived in (2026-10-31, counts only): a container and an item with a picture and a
// title, no CTA ids and no action of their own, and two CTAs with different actions.
func TestShareWithSeveralActionsAndNoIdIsStillACard(t *testing.T) {
	const button = "https://example.test/marketplace/item/1"
	ctas := []*table.LSInsertAttachmentCta{
		{ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id", CtaId: 71,
			ActionUrl: "https://example.test/untitled", Type_: "xma_web_url"},
		{ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id", CtaId: 72,
			ActionUrl: "fb://native/1", Type_: "xma_web_url", Title: "Native"},
		{ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id", CtaId: 73,
			ActionUrl: button, Type_: "xma_web_url", Title: "View listing"},
		{ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id", CtaId: 74,
			ActionUrl: "https://example.test/other-kind", Type_: "xma_other", Title: "Other"},
	}
	for _, reverse := range []bool{false, true} {
		s := newStore()
		shareTestMessage(s, "share")
		item := shareTestItem("share", "item-id")
		item.DefaultActionUrl = ""
		order := slices.Clone(ctas)
		if reverse {
			slices.Reverse(order)
		}
		s.apply(&table.LSTable{
			LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item},
			LSInsertAttachmentCta:  order,
			LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{{ThreadKey: 4, MessageId: "share", AttachmentFbid: "item-id",
				TitleText: item.TitleText, PreviewUrl: item.PreviewUrl, PreviewUrlMimeType: "image/jpeg"}},
		})
		// Repeated assembly must not make map iteration move the card to another destination.
		for attempt := 0; attempt < 8; attempt++ {
			media := shareTestMedia(t, s, "share", 1)[0]
			if media.Link != button || media.Name != item.TitleText || media.ActionTitle != "View listing" {
				t.Fatalf("the card lost its button's destination, its words or its title: %+v", media)
			}
			s.apply(&table.LSTable{LSInsertAttachmentCta: order})
		}
	}
}

// As a listing arrives (2026-11-01, counts only): the button is an xma_open_native with a label and a web address.
func TestShareLabelOutranksTheActionsType(t *testing.T) {
	const button = "https://example.test/marketplace/item/1"
	rows := &shareRows{ctas: map[shareCTAKey]*table.LSInsertAttachmentCta{}}
	key := shareRowKey{"item-id", 0}
	rows.ctas[shareCTAKey{key, 1}] = &table.LSInsertAttachmentCta{CtaId: 1, AttachmentFbid: "item-id", ActionUrl: "https://example.test/plain", Type_: "xma_web_url"}
	rows.ctas[shareCTAKey{key, 2}] = &table.LSInsertAttachmentCta{CtaId: 2, AttachmentFbid: "item-id", ActionUrl: button, Type_: "xma_open_native", Title: "View listing"}
	if link, title := rows.cardAction(key, "", ""); link != button || title != "View listing" {
		t.Fatalf("got %q %q", link, title)
	}
}

// A button's words name its action, so a card leading somewhere else does not take them.
func TestShareButtonWordsGoWithTheirAction(t *testing.T) {
	for _, test := range []struct {
		name, direct, rowTitle, ctaLink, ctaTitle, wantLink, wantTitle string
	}{
		{"from the cta", "", "", "https://example.test/a", " View listing ", "https://example.test/a", "View listing"},
		{"same place", "https://example.test/a", "", "https://example.test/a", "Open", "https://example.test/a", "Open"},
		{"elsewhere", "https://example.test/b", "", "https://example.test/a", "Open", "https://example.test/b", ""},
		{"the row's own", "https://example.test/b", "Watch", "", "", "https://example.test/b", "Watch"},
		{"no destination", "", "Watch", "", "", "", ""},
	} {
		t.Run(test.name, func(t *testing.T) {
			rows := &shareRows{ctas: map[shareCTAKey]*table.LSInsertAttachmentCta{}}
			key := shareRowKey{"item-id", 0}
			if test.ctaLink != "" {
				rows.ctas[shareCTAKey{key, 7}] = &table.LSInsertAttachmentCta{CtaId: 7, AttachmentFbid: "item-id", ActionUrl: test.ctaLink, Title: test.ctaTitle}
			}
			link, title := rows.cardAction(key, test.direct, test.rowTitle)
			if link != test.wantLink || title != test.wantTitle {
				t.Fatalf("got %q %q, want %q %q", link, title, test.wantLink, test.wantTitle)
			}
		})
	}
}

func TestShareLogWordsCarryNoAddress(t *testing.T) {
	for value, want := range map[string]string{"": "none", "xma_web_url": "xma_web_url", "https://example.test/a": "other", "Some Title": "other"} {
		if got := ctaTypeWord(value); got != want {
			t.Fatalf("ctaTypeWord(%q) = %q, want %q", value, got, want)
		}
	}
	for link, want := range map[string]string{
		"https://www.facebook.com/marketplace/item/1/":                            "marketplace",
		"https://l.facebook.com/l.php?u=https%3A%2F%2Fexample.test%2Fa":           "other",
		"https://www.facebook.com/share/p/1/":                                     "facebook",
		"https://www.messenger.com/t/1":                                           "messenger",
		"fb://marketplace_product_details_from_for_sale_item_id?for_sale_item_id": "native",
		"": "native",
	} {
		if got := linkForm(link); got != want {
			t.Fatalf("linkForm(%q) = %q, want %q", link, got, want)
		}
	}
}

func TestShareItemUpgradesLegacyAttachmentWithoutDuplicate(t *testing.T) {
	s := newStore()
	shareTestMessage(s, "share")
	item := shareTestItem("share", "legacy-fbid")
	// Older builds persisted an assembled card under the raw FBID, with no share
	// row state. This is an explicit identity match, not a guessed container join.
	s.attach("share", &attachment{
		id: "legacy-fbid", source: item.PreviewUrl, mime: "image/jpeg",
		name: item.TitleText, width: item.PreviewWidth, height: item.PreviewHeight,
	})
	shareTestMedia(t, s, "share", 1)
	touched := s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}})
	media := shareTestMedia(t, s, "share", 1)[0]
	if media.Link != item.DefaultActionUrl {
		t.Fatalf("refreshed item did not upgrade the restored card's destination: %+v", media)
	}
	source, _, ok := s.mediaSource(media.ID)
	if !ok || source != item.PreviewUrl {
		t.Fatalf("upgraded card's picture is not downloadable: source=%q ok=%v", source, ok)
	}
	shareTestTouched(t, touched, "share")
	s.apply(&table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}})
	shareTestMedia(t, s, "share", 1)
}

func TestShareEquivalentContainerAndItemDrawOnce(t *testing.T) {
	for _, itemFirst := range []bool{false, true} {
		name := "container_first"
		if itemFirst {
			name = "item_first"
		}
		t.Run(name, func(t *testing.T) {
			s := newStore()
			shareTestMessage(s, "share")
			item := shareTestItem("share", "independent-item-id")
			itemRows := &table.LSTable{LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{item}}
			containerRows := &table.LSTable{LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
				{ThreadKey: 4, MessageId: "share", AttachmentFbid: "independent-container-id",
					AttachmentType: table.AttachmentTypeXMA, PreviewUrl: item.PreviewUrl,
					PreviewUrlMimeType: item.PreviewUrlMimeType, ActionUrl: item.DefaultActionUrl,
					TitleText: item.TitleText, PreviewWidth: item.PreviewWidth, PreviewHeight: item.PreviewHeight},
			}}
			if itemFirst {
				s.apply(itemRows)
				shareTestMedia(t, s, "share", 1)
				s.apply(containerRows)
			} else {
				s.apply(containerRows)
				shareTestMedia(t, s, "share", 1)
				s.apply(itemRows)
			}
			// Exact picture and destination equality justifies suppressing this
			// redundant visual. Their different FBIDs do not establish a join.
			media := shareTestMedia(t, s, "share", 1)[0]
			if media.Link != item.DefaultActionUrl {
				t.Fatalf("the surviving card lost its destination: %+v", media)
			}
			s.apply(itemRows)
			s.apply(containerRows)
			shareTestMedia(t, s, "share", 1)
		})
	}
}

func TestShareDiagnosticsDoNotExposeContent(t *testing.T) {
	path := filepath.Join(t.TempDir(), "synthetic-share.log")
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	previous := diagnostics
	diagnostics = &logger{file: file}
	t.Cleanup(func() {
		closeLog()
		diagnostics = previous
	})
	const secret = "SYNTHETIC_PRIVATE_SHARE_SENTINEL"
	s := newStore()
	rows := &table.LSTable{
		LSUpsertMessage: []*table.LSUpsertMessage{{ThreadKey: 4, MessageId: secret + "-message", Text: secret + "-body"}},
		LSInsertXmaAttachment: []*table.LSInsertXmaAttachment{
			{ThreadKey: 4, MessageId: secret + "-message", AttachmentFbid: secret + "-container",
				AttachmentType: table.AttachmentTypeXMA, TitleText: secret + "-title",
				BodyText: secret + "-body-text", XmaDataclass: secret + "-dataclass", LoggingGenericXMAContentType: secret + "-content",
				Unrecognized: map[int]any{140: secret + "-unknown", 141: float64(7), 142: []any{secret}}},
		},
		LSInsertAttachmentItem: []*table.LSInsertAttachmentItem{
			{ThreadKey: 4, MessageId: secret + "-message", AttachmentFbid: secret + "-item",
				PreviewUrl: "https://example.test/" + secret + ".jpg", TitleText: secret + "-item-title",
				DefaultActionUrl: "https://example.test/" + secret},
		},
		LSInsertAttachmentCta: []*table.LSInsertAttachmentCta{
			{ThreadKey: 4, MessageId: secret + "-message", AttachmentFbid: secret + "-item",
				ActionUrl: "https://example.test/" + secret + "-cta", Title: secret + "-cta-title"},
		},
	}
	logRows("synthetic", rows, s.apply(rows))
	closeLog()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if len(data) == 0 {
		t.Fatal("expected diagnostics for the synthetic rows")
	}
	if strings.Contains(strings.ToLower(string(data)), strings.ToLower(secret)) || strings.Contains(string(data), "https://") {
		t.Fatal("share diagnostics exposed synthetic content or an address")
	}
	if !strings.Contains(string(data), "unknown=140:s40,141:n,142:l1 filled=body,dataclass") ||
		!strings.Contains(string(data), "dataclass=other content=other") || !strings.Contains(string(data), "body=true") {
		t.Fatalf("share diagnostics did not say where the card's unknown columns and words are:\n%s", data)
	}
}

// The badge says the picture stands for something that plays. Facebook marks that outright only on the encrypted
// connection; on the ordinary one the address is the only thing that says so.
func TestVideoAddressesAreMarkedPlayable(t *testing.T) {
	playable := []string{
		"https://www.facebook.com/share/v/1Dn7bbJzWJ/",
		"https://www.facebook.com/reel/123",
		"https://fb.watch/abc/",
		"https://www.youtube.com/watch?v=abc",
		"https://youtu.be/abc",
	}
	for _, link := range playable {
		if !playableLink(link) {
			t.Errorf("%s should be marked as playing", link)
		}
	}
	// An ordinary link is a link. A badge promising a video on a shopping page would be a lie.
	for _, link := range []string{"https://amazon.com", "https://www.amazon.com/NETGEAR-5-Port/dp/B07S98YLHM/?th=1", "https://example.test/watching"} {
		if playableLink(link) {
			t.Errorf("%s should not be marked as playing", link)
		}
	}
}
