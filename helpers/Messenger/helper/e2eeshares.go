package main

import (
	"fmt"
	"net/url"
	"strings"

	"go.mau.fi/whatsmeow/proto/waArmadilloApplication"
	"go.mau.fi/whatsmeow/proto/waArmadilloXMA"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"go.mau.fi/whatsmeow/types/events"
)

type encryptedShareMedia struct {
	item *attachment
	held *secureMedia
}

// ConsumerApplication and Armadillo are separate applications on the encrypted connection.
// GetConsumerApplication returns nil for Armadillo, including its reel/post shares.
func encryptedContents(evt *events.FBMessage) ([]encryptedShareMedia, string) {
	consumer := evt.GetConsumerApplication()
	armadillo := evt.GetArmadillo()
	content := armadillo.GetPayload().GetContent()
	xma := content.GetExtendedContentMessage()
	sear := content.GetExtendedMessageContentWithSear()
	ai := armadillo.GetPayload().GetApplicationData().GetAiBotResponse()
	if ai != nil {
		logf("meta ai shape connection=encrypted id=%s applicationData=true text=%v extras=%v",
			tagOf(evt.Info.ID), strings.TrimSpace(ai.GetMessageText()) != "", ai.GetSerializedExtras() != "")
		return nil, ai.GetMessageText()
	}
	metadata := evt.FBApplication.GetMetadata()
	logf("e2ee shape id=%s consumer=%v armadillo=%v extended=%v sear=%v applicationData=%v mediaGroup=%v groupSize=%d groupIndex=%d",
		tagOf(evt.Info.ID), consumer != nil, armadillo != nil, xma != nil, sear != nil, armadillo.GetPayload().GetApplicationData() != nil,
		metadata.GetGroupID() != "", metadata.GetGroupSize(), metadata.GetGroupIndex())
	if xma != nil {
		return encryptedShare(xma, evt.Info.ID)
	}
	if gallery := content.GetImageGalleryMessage(); gallery != nil {
		return encryptedGallery(gallery, evt.Info.ID), ""
	}
	if armadillo != nil {
		// The Sear schema names a destination, but does not define its opaque payload as an image.
		// Preserve the usable link without guessing at those bytes.
		if link := shareWebLink(sear.GetNativeURL()); link != "" {
			return nil, link
		}
		if sear != nil {
			return nil, "Shared message — open in Messenger"
		}
		return nil, ""
	}
	text := encryptedText(evt)
	item, held, caption := encryptedMedia(consumer.GetPayload().GetContent(), evt.Info.ID)
	if text == "" {
		text = caption
	}
	if item != nil && held != nil {
		return []encryptedShareMedia{{item: item, held: held}}, text
	}
	return nil, text
}

func encryptedGallery(gallery *waArmadilloApplication.Armadillo_Content_ImageGalleryMessage, messageID string) (items []encryptedShareMedia) {
	images, err := gallery.Decode()
	if err != nil {
		return nil
	}
	for index, image := range images {
		transport := image.GetIntegral().GetTransport()
		held := directions(transport, "image")
		if held == nil {
			continue
		}
		item := describe(fmt.Sprintf("e2ee:%s:gallery:%d", messageID, index), "Photo", transport)
		if item.mime == "" {
			item.mime = "image/jpeg"
		}
		if !strings.HasPrefix(strings.ToLower(item.mime), "image/") {
			continue
		}
		item.width = int64(image.GetAncillary().GetWidth())
		item.height = int64(image.GetAncillary().GetHeight())
		items = append(items, encryptedShareMedia{item: item, held: held})
	}
	return items
}

// Meta's messaging_schemas/subprotocols.json explicitly maps ExtendedContentMessage.previews
// to ImageTransport. This is an embedded picture transport, not a Lightspeed attachment join.
// See docs/research/MESSENGER_SHARED_LINKS.md for source/version evidence.
func encryptedShare(content *waArmadilloXMA.ExtendedContentMessage, messageID string) (items []encryptedShareMedia, text string) {
	link, action := "", ""
	for _, cta := range content.GetCtas() {
		if link = linkDestination(firstOf(shareWebLink(cta.GetNativeURL()), shareWebLink(cta.GetActionURL()))); link != "" {
			action = strings.TrimSpace(cta.GetTitle())
			break
		}
	}
	name := content.GetTitleText()
	if name == "" {
		name = "Shared link"
	}
	for index, preview := range content.GetPreviews() {
		image, err := decodePicture(&waMediaTransport.ImageTransport{}, preview)
		transport := image.GetIntegral().GetTransport()
		var held *secureMedia
		if err == nil {
			held = directions(transport, "image")
		}
		logf("e2ee share preview id=%s index=%d version=%d wanted=%v decoded=%v direct=%v inline=%v usable=%v bytes=%d",
			tagOf(messageID), index, preview.GetVersion(), pictureVersions,
			err == nil, transport.GetIntegral().GetDirectPath() != "",
			len(transport.GetAncillary().GetThumbnail().GetJPEGThumbnail()) > 0, held != nil, len(preview.GetPayload()))
		if held == nil {
			continue
		}
		item := describe(fmt.Sprintf("e2ee:%s:share:%d", messageID, index), name, transport)
		if item.mime == "" {
			item.mime = "image/jpeg"
		}
		if !strings.HasPrefix(item.mime, "image/") {
			continue
		}
		item.width = int64(image.GetAncillary().GetWidth())
		item.height = int64(image.GetAncillary().GetHeight())
		item.link, item.actionTitle = link, action
		items = append(items, encryptedShareMedia{item: item, held: held})
	}
	text = content.GetMessageText()
	pictures := len(items)
	// If Facebook supplies no usable picture, keep its actual destination visible instead of a blank row.
	carded := false
	if len(items) == 0 {
		if link != "" && !strings.Contains(text, link) {
			if text != "" {
				text += "\n"
			}
			text += link
		}
		if text == "" {
			text = "Shared message — open in Messenger"
		}
		// A post shared from Facebook came with no picture at all, and Messenger still drew it as a card from its words
		// alone: the author, the post's opening line, "Facebook" (2026-11-12). Kept as a bare link, it read as one here.
		// The card carries no mime, which is how a card with no picture is told apart (pictureless).
		if title, description := shareCardWords(content); link != "" && title != "" {
			items = append(items, encryptedShareMedia{item: &attachment{id: fmt.Sprintf("e2ee:%s:share:card", messageID),
				name: title, description: description, link: link, actionTitle: action}})
			carded = true
		}
	}
	logf("e2ee share id=%s previews=%d thumbnails=%d ctas=%d linked=%v text=%v carded=%v title=%v subtitle=%v header=%v headerSubtitle=%v headerImage=%v favicon=%v overlay=%v",
		tagOf(messageID), len(content.GetPreviews()), pictures, len(content.GetCtas()), link != "", text != "", carded,
		strings.TrimSpace(content.GetTitleText()) != "", strings.TrimSpace(content.GetSubtitleText()) != "",
		strings.TrimSpace(content.GetHeaderTitle()) != "", strings.TrimSpace(content.GetHeaderSubtitle()) != "",
		content.GetHeaderImage() != nil, content.GetFavicon() != nil,
		strings.TrimSpace(content.GetOverlayTitle()) != "" || strings.TrimSpace(content.GetOverlayDescription()) != "")
	return
}

// The words of a share's card, for one with no picture: its title, then the first other line that says something
// different (for a Facebook post, its author). Which field Facebook fills for which part is not documented; the
// "e2ee share" line records which were present.
func shareCardWords(content *waArmadilloXMA.ExtendedContentMessage) (title, description string) {
	// The subtitle last: Messenger's own card ended in "Facebook", which as the title would name only the site.
	title = firstOf(strings.TrimSpace(content.GetTitleText()), strings.TrimSpace(content.GetHeaderTitle()),
		strings.TrimSpace(content.GetSubtitleText()))
	for _, candidate := range []string{content.GetHeaderTitle(), content.GetSubtitleText(), content.GetHeaderSubtitle()} {
		if candidate = strings.TrimSpace(candidate); candidate != "" && candidate != title {
			return title, candidate
		}
	}
	return title, ""
}

func shareWebLink(value string) string {
	parsed, err := url.Parse(value)
	if err != nil || parsed.Host == "" || (parsed.Scheme != "https" && parsed.Scheme != "http") {
		return ""
	}
	return value
}

// Facebook hands a shared web page's card its click-tracking redirect (l.facebook.com/l.php?u=<the page>) where the
// message's own words hold the page itself. Kept as is, the card names l.facebook.com, and the "this link already has
// a card" checks miss it, so the helper fetched the page and drew a second card under Facebook's (2026-10-18).
func linkDestination(value string) string {
	parsed, err := url.Parse(value)
	if err != nil {
		return value
	}
	switch strings.ToLower(parsed.Hostname()) {
	case "l.facebook.com", "lm.facebook.com", "l.messenger.com", "l.instagram.com":
		if target := shareWebLink(parsed.Query().Get("u")); target != "" {
			return target
		}
	}
	return value
}

// Two cards' links name the same page: one may still be a redirect stored before linkDestination existed.
func sameLink(a, b string) bool {
	return a == b || (a != "" && b != "" && linkDestination(a) == linkDestination(b))
}
