package main

import (
	"sort"
	"strconv"
	"strings"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

type shareMessageKey struct {
	thread  int64
	message string
}

type shareRowKey struct {
	fbid  string
	index int64
}

type shareCTAKey struct {
	shareRowKey
	id int64
}

// Retain partial rows so a CTA or picture arriving later updates the same message. Items have their own MessageId
// (attachments.go index 4) and ThreadKey (index 2). Upstream does not define an item-to-container join; requiring an
// item FBID to match a container (or our synthesized container ID) is unnecessary and loses otherwise complete items.
type shareRows struct {
	items      map[shareRowKey]*table.LSInsertAttachmentItem
	containers map[shareRowKey]*table.LSInsertXmaAttachment
	ctas       map[shareCTAKey]*table.LSInsertAttachmentCta
}

type shareThumbnail struct {
	*attachment
	legacyID string
}

// Call with the store lock held. Logs contain only presence, counts and one-way tags, including whether the actual
// container FBID was present; earlier diagnostics could not answer that question.
func (s *store) applyShares(tbl *table.LSTable, note func(int64, string)) {
	dirty := map[shareMessageKey]*shareRows{}
	rowsFor := func(thread int64, message string) *shareRows {
		key := shareMessageKey{thread, message}
		rows := s.shares[key]
		if rows == nil {
			rows = &shareRows{items: map[shareRowKey]*table.LSInsertAttachmentItem{},
				containers: map[shareRowKey]*table.LSInsertXmaAttachment{}, ctas: map[shareCTAKey]*table.LSInsertAttachmentCta{}}
			s.shares[key] = rows
		}
		dirty[key] = rows
		return rows
	}
	// Attachment rows may precede their message. Reassemble retained cards when the body row arrives later.
	for _, row := range tbl.LSInsertMessage {
		key := shareMessageKey{row.ThreadKey, row.MessageId}
		if rows := s.shares[key]; rows != nil {
			dirty[key] = rows
		}
	}
	for _, row := range tbl.LSUpsertMessage {
		key := shareMessageKey{row.ThreadKey, row.MessageId}
		if rows := s.shares[key]; rows != nil {
			dirty[key] = rows
		}
	}
	for _, row := range tbl.LSInsertXmaAttachment {
		if row.PlayableUrl != "" {
			continue
		}
		logf("share container chat=%s message=%s fbid=%v attachment=%s index=%d preview=%v fallback=%v large=%v image=%v action=%v cta=%v unknown=%d",
			tag(row.ThreadKey), tagOf(row.MessageId), row.AttachmentFbid != "", tagOf(row.AttachmentFbid), row.AttachmentIndex,
			row.PreviewUrl != "", row.PreviewUrlFallback != "", row.PreviewUrlLarge != "", row.ImageUrl != "" || row.ImageUrlFallback != "",
			row.ActionUrl != "", row.DefaultCtaId != 0 || row.AttachmentCta1Id != 0, len(row.Unrecognized))
		if row.MessageId == "" {
			continue
		}
		logf("share text shape connection=lightspeed id=%s kind=container title=%v picture=%v playable=%v",
			tagOf(row.MessageId), strings.TrimSpace(row.TitleText) != "",
			firstOf(row.PreviewUrl, row.PreviewUrlFallback, row.PreviewUrlLarge, row.ImageUrl, row.ImageUrlFallback) != "", row.PlayableUrl != "")
		rows := rowsFor(row.ThreadKey, row.MessageId)
		key := shareRowKey{row.AttachmentFbid, row.AttachmentIndex}
		merged := *row
		if previous := rows.containers[key]; previous != nil {
			merged.TitleText = firstOf(row.TitleText, previous.TitleText)
		}
		rows.containers[key] = &merged
	}
	for _, row := range tbl.LSInsertAttachmentItem {
		logf("share item chat=%s message=%s fbid=%v attachment=%s index=%d preview=%v fallback=%v large=%v image=%v action=%v cta=%v unknown=%d",
			tag(row.ThreadKey), tagOf(row.MessageId), row.AttachmentFbid != "", tagOf(row.AttachmentFbid), row.AttachmentIndex,
			row.PreviewUrl != "", row.PreviewUrlFallback != "", row.PreviewUrlLarge != "", row.ImageUrl != "",
			row.DefaultActionUrl != "", row.DefaultCtaId != 0 || row.AttachmentCta1Id != 0, len(row.Unrecognized))
		if row.MessageId == "" {
			continue
		}
		rows := rowsFor(row.ThreadKey, row.MessageId)
		logf("share text shape connection=lightspeed id=%s kind=item title=%v picture=%v playable=%v",
			tagOf(row.MessageId), strings.TrimSpace(row.TitleText) != "",
			firstOf(row.PreviewUrl, row.PreviewUrlFallback, row.PreviewUrlLarge, row.ImageUrl) != "", row.PlayableUrl != "")
		key := shareRowKey{row.AttachmentFbid, row.AttachmentIndex}
		merged := *row
		if previous := rows.items[key]; previous != nil {
			// A link/title-only row cannot erase the picture that already arrived. When any new picture field is
			// supplied, use that new group as a whole instead of preferring an older large/primary URL over it.
			if firstOf(row.PreviewUrl, row.PreviewUrlFallback, row.PreviewUrlLarge, row.ImageUrl) == "" {
				merged.PreviewUrl, merged.PreviewUrlFallback = previous.PreviewUrl, previous.PreviewUrlFallback
				merged.PreviewUrlLarge, merged.ImageUrl = previous.PreviewUrlLarge, previous.ImageUrl
				merged.PreviewUrlMimeType = previous.PreviewUrlMimeType
				merged.PreviewWidth, merged.PreviewHeight = previous.PreviewWidth, previous.PreviewHeight
			}
			merged.TitleText, merged.SubtitleText = firstOf(row.TitleText, previous.TitleText), firstOf(row.SubtitleText, previous.SubtitleText)
			if row.DefaultActionUrl == "" && row.DefaultCtaId == 0 && row.AttachmentCta1Id == 0 && row.AttachmentCta2Id == 0 && row.AttachmentCta3Id == 0 {
				merged.DefaultActionUrl, merged.DefaultCtaId = previous.DefaultActionUrl, previous.DefaultCtaId
				merged.AttachmentCta1Id, merged.AttachmentCta2Id, merged.AttachmentCta3Id = previous.AttachmentCta1Id, previous.AttachmentCta2Id, previous.AttachmentCta3Id
			}
		}
		rows.items[key] = &merged
	}
	for _, row := range tbl.LSInsertAttachmentCta {
		logf("share cta chat=%s message=%s fbid=%v attachment=%s index=%d id=%v action=%v type=%s titled=%v form=%s unknown=%d",
			tag(row.ThreadKey), tagOf(row.MessageId), row.AttachmentFbid != "", tagOf(row.AttachmentFbid), row.AttachmentIndex,
			row.CtaId != 0, row.ActionUrl != "", ctaTypeWord(row.Type_), strings.TrimSpace(row.Title) != "", linkForm(row.ActionUrl), len(row.Unrecognized))
		if row.MessageId == "" {
			continue
		}
		rowsFor(row.ThreadKey, row.MessageId).ctas[shareCTAKey{shareRowKey{row.AttachmentFbid, row.AttachmentIndex}, row.CtaId}] = row
	}
	for key, rows := range dirty {
		current := s.messages[key.thread][key.message]
		if current != nil && current.sender == metaAIMessengerID && current.sender != s.self &&
			!current.encrypted && !current.unsent && !s.revoked[key.message] {
			text := rows.textOnlyTitles()
			if current.cardText != text {
				current.cardText = text
				note(key.thread, key.message)
			}
		}
		drawn := map[string]bool{}
		for _, candidate := range rows.thumbnails(key) {
			if candidate.source == "" || !strings.HasPrefix(candidate.mime, "image/") {
				continue
			}
			// Identical visuals from a container/item are one card. Older builds stored a sole item under its FBID;
			// reuse that cached ID only when its actual picture agrees, never merely because two FBIDs match.
			for _, old := range s.attached[key.message] {
				if old.id == candidate.id {
					break
				}
				if old.source == candidate.source && !old.video && !old.animated &&
					(sameLink(old.link, candidate.link) || (old.id == candidate.legacyID && old.link == "")) {
					candidate.id = old.id
					break
				}
			}
			// A repeated refresh must not duplicate cards; late CTA rows must update even an already pictured card.
			if old := s.media[candidate.id]; old == nil || *old != *candidate.attachment {
				s.attach(key.message, candidate.attachment)
				note(key.thread, key.message)
			}
			drawn[candidate.id] = candidate.link != ""
		}
		links := 0
		for _, linked := range drawn {
			if linked {
				links++
			}
		}
		logf("share assembled chat=%s message=%s containers=%d items=%d ctas=%d thumbnails=%d linked=%d",
			tag(key.thread), tagOf(key.message), len(rows.containers), len(rows.items), len(rows.ctas), len(drawn), links)
	}
}

// The pinned upstream xmaAttachmentToMatrix renders TitleText when no media URL exists.
// Keep only explicit title fields; do not decode metadata or infer joins between differing cards.
func (rows *shareRows) textOnlyTitles() string {
	var titles []string
	seen := map[string]bool{}
	add := func(title string) {
		if strings.TrimSpace(title) != "" && !seen[title] {
			seen[title] = true
			titles = append(titles, title)
		}
	}
	for _, key := range orderedShareKeys(rows.items) {
		row := rows.items[key]
		if firstOf(row.PreviewUrlLarge, row.PreviewUrl, row.PreviewUrlFallback, row.ImageUrl, row.PlayableUrl, row.PlayableUrlFallback) == "" {
			add(row.TitleText)
		}
	}
	for _, key := range orderedShareKeys(rows.containers) {
		row := rows.containers[key]
		if firstOf(row.PreviewUrlLarge, row.PreviewUrl, row.PreviewUrlFallback, row.ImageUrl, row.ImageUrlFallback, row.PlayableUrl, row.PlayableUrlFallback) == "" {
			add(row.TitleText)
		}
	}
	return strings.Join(titles, "\n\n")
}

func orderedShareKeys[T any](rows map[shareRowKey]T) []shareRowKey {
	keys := make([]shareRowKey, 0, len(rows))
	for key := range rows {
		keys = append(keys, key)
	}
	sort.Slice(keys, func(i, j int) bool {
		if keys[i].index != keys[j].index {
			return keys[i].index < keys[j].index
		}
		return keys[i].fbid < keys[j].fbid
	})
	return keys
}

func (rows *shareRows) thumbnails(message shareMessageKey) []shareThumbnail {
	var result []shareThumbnail
	for _, key := range orderedShareKeys(rows.items) {
		row := rows.items[key]
		// These fields explicitly describe pictures. PlayableUrl may be an MP4 and is never a JPEG fallback.
		source := firstOf(row.PreviewUrlLarge, row.PreviewUrl, row.PreviewUrlFallback, row.ImageUrl)
		if source == "" {
			continue
		}
		result = append(result, shareThumbnail{attachment: &attachment{
			id:   strconv.FormatInt(message.thread, 10) + ":" + message.message + ":xma-item:" + key.fbid + ":" + strconv.FormatInt(key.index, 10),
			name: firstOf(row.TitleText, row.SubtitleText, "Shared link"), mime: previewType(row.PreviewUrlMimeType), source: source,
			width: row.PreviewWidth, height: row.PreviewHeight,
			link: linkDestination(firstOf(row.DefaultActionUrl, rows.ctaLink(key, row.DefaultCtaId, row.AttachmentCta1Id, row.AttachmentCta2Id, row.AttachmentCta3Id))),
			// A share with something to play behind it is a reel or a video; the picture gets a play badge for it.
			play: row.PlayableUrl != "" || playableLink(linkDestination(row.DefaultActionUrl)),
		}, legacyID: row.AttachmentFbid})
	}
	for _, key := range orderedShareKeys(rows.containers) {
		row := rows.containers[key]
		source := firstOf(row.PreviewUrlLarge, row.PreviewUrl, row.PreviewUrlFallback, row.ImageUrl, row.ImageUrlFallback)
		if source == "" {
			continue
		}
		id := row.AttachmentFbid
		if id == "" {
			id = row.MessageId + ":xma:" + strconv.FormatInt(row.AttachmentIndex, 10)
		}
		result = append(result, shareThumbnail{attachment: &attachment{
			id: id, name: firstOf(row.TitleText, row.SubtitleText, shareName(row, nil)),
			mime: previewType(row.PreviewUrlMimeType), source: source, width: row.PreviewWidth, height: row.PreviewHeight,
			link: linkDestination(firstOf(row.ActionUrl, rows.ctaLink(key, row.DefaultCtaId, row.AttachmentCta1Id, row.AttachmentCta2Id, row.AttachmentCta3Id))),
			play: row.PlayableUrl != "" || playableLink(linkDestination(row.ActionUrl)),
		}, legacyID: id})
	}
	return result
}

// Explicit CTA IDs select the intended action when several buttons exist. The upstream WrapMessages fallback joins
// CTA to XMA by AttachmentFbid; scope that lookup to the same message and never match empty FBIDs or invented IDs.
func (rows *shareRows) ctaLink(attachment shareRowKey, ids ...int64) string {
	for _, id := range ids {
		if id == 0 {
			continue
		}
		var found string
		for key, row := range rows.ctas {
			if key.id != id || row.ActionUrl == "" {
				continue
			}
			if found != "" && found != row.ActionUrl {
				return ""
			}
			found = row.ActionUrl
		}
		if found != "" {
			return found
		}
	}
	if attachment.fbid == "" {
		return ""
	}
	// A Marketplace listing names no CTA and carries two different actions (2026-10-31), and declining to choose drew
	// it as a bare photo. All of them belong to this card, so take the one most like the button Messenger draws; the
	// encrypted connection's cards already take their first web link.
	var chosen *table.LSInsertAttachmentCta
	actions := 0
	for key, row := range rows.ctas {
		if key.fbid != attachment.fbid || row.ActionUrl == "" {
			continue
		}
		actions++
		if chosen == nil || betterCTA(row, chosen) {
			chosen = row
		}
	}
	if chosen == nil {
		return ""
	}
	if actions > 1 {
		logf("share cta chosen attachment=%s actions=%d id=%s type=%s titled=%v form=%s", tagOf(attachment.fbid), actions,
			tag(chosen.CtaId), ctaTypeWord(chosen.Type_), strings.TrimSpace(chosen.Title) != "", linkForm(chosen.ActionUrl))
	}
	return chosen.ActionUrl
}

func ctaRank(row *table.LSInsertAttachmentCta) int {
	rank := 0
	if shareWebLink(row.ActionUrl) != "" {
		rank += 4
	}
	if row.Type_ == "xma_web_url" {
		rank += 2
	}
	if strings.TrimSpace(row.Title) != "" {
		rank++
	}
	return rank
}

// The rows live in a map, so ties break on the rows themselves: a card must not change destination between assemblies.
func betterCTA(row, than *table.LSInsertAttachmentCta) bool {
	if a, b := ctaRank(row), ctaRank(than); a != b {
		return a > b
	}
	if row.CtaId != than.CtaId {
		return row.CtaId < than.CtaId
	}
	return row.ActionUrl < than.ActionUrl
}

// Facebook's own word for a CTA, for the log. Anything but such a word is not repeated.
func ctaTypeWord(value string) string {
	if value == "" {
		return "none"
	}
	if len(value) > 40 || strings.Trim(value, "abcdefghijklmnopqrstuvwxyz0123456789_") != "" {
		return "other"
	}
	return value
}

// What kind of place an address is, for the log, which never holds the address.
func linkForm(link string) string {
	target := linkDestination(link)
	if shareWebLink(target) == "" {
		return "native"
	}
	lowered := strings.ToLower(target)
	switch {
	case strings.Contains(lowered, "facebook.com/marketplace/"):
		return "marketplace"
	case strings.Contains(lowered, "facebook.com/"):
		return "facebook"
	case strings.Contains(lowered, "messenger.com/"):
		return "messenger"
	}
	return "other"
}

// Whether an address is somewhere that plays. Facebook marks this outright on the encrypted connection, and not at
// all on the ordinary one: a shared reel arrives with a picture, a title and an address, and nothing that says the
// address is a video. The address itself is the only thing left to go on, so these are the forms Facebook and the
// sites people share from use for a video, and nothing is inferred beyond them.
func playableLink(link string) bool {
	lowered := strings.ToLower(link)
	for _, mark := range []string{
		"facebook.com/share/v/", "facebook.com/reel/", "facebook.com/watch", "fb.watch/",
		"instagram.com/reel", "youtube.com/watch", "youtu.be/", "tiktok.com/",
	} {
		if strings.Contains(lowered, mark) {
			return true
		}
	}
	return false
}
