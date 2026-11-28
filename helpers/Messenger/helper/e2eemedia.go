package main

import (
	"bytes"
	"context"
	"image/jpeg"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"google.golang.org/protobuf/proto"
)

// A picture, video, voice note, file or sticker in an encrypted conversation. Most carry download directions: where
// the file sits on Meta's servers and the key that opens it. Some image previews include JPEG bytes directly. Both
// live inside the transport kept here, so the attachment remains available after restart without fetching on arrival.
type secureMedia struct {
	transport []byte // a marshalled waMediaTransport.WAMediaTransport
	kind      string
}

func mediaKindOf(kind string) whatsmeow.MediaType {
	switch kind {
	case "video":
		return whatsmeow.MediaVideo
	case "audio":
		return whatsmeow.MediaAudio
	case "document":
		return whatsmeow.MediaDocument
	default:
		return whatsmeow.MediaImage
	}
}

// The attachment an encrypted message carries, the directions to fetch it, and the text that came with it. Nothing is
// returned for a message that is only text, or for one whose media this build cannot address.
func encryptedMedia(content *waConsumerApplication.ConsumerApplication_Content, messageID string) (*attachment, *secureMedia, string) {
	id := "e2ee:" + messageID
	switch {
	case content.GetImageMessage() != nil:
		msg := content.GetImageMessage()
		decoded, err := msg.Decode()
		if err != nil {
			return nil, nil, ""
		}
		item := describe(id, "Photo", decoded.GetIntegral().GetTransport())
		item.width = int64(decoded.GetAncillary().GetWidth())
		item.height = int64(decoded.GetAncillary().GetHeight())
		return item, directions(decoded.GetIntegral().GetTransport(), "image"), msg.GetCaption().GetText()
	case content.GetVideoMessage() != nil:
		msg := content.GetVideoMessage()
		decoded, err := msg.Decode()
		if err != nil {
			return nil, nil, ""
		}
		item := describe(id, "Video", decoded.GetIntegral().GetTransport())
		item.width = int64(decoded.GetAncillary().GetWidth())
		item.height = int64(decoded.GetAncillary().GetHeight())
		// A gif arrives as a video that plays by itself; the app draws those without controls.
		item.animated = decoded.GetAncillary().GetGifPlayback()
		item.video = !item.animated
		if item.animated {
			item.name = "GIF"
		}
		return item, directions(decoded.GetIntegral().GetTransport(), "video"), msg.GetCaption().GetText()
	case content.GetStickerMessage() != nil:
		msg := content.GetStickerMessage()
		decoded, err := msg.Decode()
		if err != nil {
			return nil, nil, ""
		}
		// A sticker the sender's client has to fetch for itself is not addressable from here.
		if decoded.GetAncillary().GetReceiverFetchID() != "" {
			return nil, nil, ""
		}
		item := describe(id, "Sticker", decoded.GetIntegral().GetTransport())
		item.width = int64(decoded.GetAncillary().GetWidth())
		item.height = int64(decoded.GetAncillary().GetHeight())
		item.animated = item.mime == "image/webp"
		return item, directions(decoded.GetIntegral().GetTransport(), "image"), ""
	case content.GetAudioMessage() != nil:
		msg := content.GetAudioMessage()
		decoded, err := msg.Decode()
		if err != nil {
			return nil, nil, ""
		}
		name := "Audio"
		if msg.GetPTT() {
			name = "Voice message"
		}
		return describe(id, name, decoded.GetIntegral().GetTransport()), directions(decoded.GetIntegral().GetTransport(), "audio"), ""
	case content.GetExtendedTextMessage() != nil:
		// Consumer link previews carry a thumbnail subprotocol. Armadillo reel/post shares are decoded separately.
		msg := content.GetExtendedTextMessage()
		link := shareWebLink(msg.GetCanonicalURL())
		if link == "" {
			link = shareWebLink(msg.GetMatchedText())
		}
		decoded, err := decodeLinkThumbnail(msg)
		logf("e2ee link preview id=%s thumbnail=%v decoded=%v linked=%v",
			tagOf(messageID), msg.GetThumbnail() != nil, err == nil, link != "")
		if err != nil || link == "" || decoded == nil {
			return nil, nil, link
		}
		name := msg.GetTitle()
		if name == "" {
			name = "Shared link"
		}
		item := describe(id, name, decoded.GetIntegral().GetTransport())
		if item.mime == "" {
			item.mime = "image/jpeg"
		}
		item.width = int64(decoded.GetAncillary().GetWidth())
		item.height = int64(decoded.GetAncillary().GetHeight())
		item.link = link
		// Facebook says outright whether the preview stands for a video, which is what the play badge is for.
		item.play = msg.GetPreviewType() == waConsumerApplication.ConsumerApplication_ExtendedTextMessage_VIDEO
		return item, directions(decoded.GetIntegral().GetTransport(), "image"), msg.GetText().GetText()
	case content.GetDocumentMessage() != nil:
		msg := content.GetDocumentMessage()
		decoded, err := msg.Decode()
		if err != nil {
			return nil, nil, ""
		}
		name := msg.GetFileName()
		if name == "" {
			name = "File"
		}
		return describe(id, name, decoded.GetIntegral().GetTransport()), directions(decoded.GetIntegral().GetTransport(), "document"), ""
	}
	// Nothing here recognised it. The kind is a Go type name from the library — no content of any sort — and it is the
	// only way to find out what an encrypted message that draws as nothing actually contained.
	if content != nil && content.Content != nil {
		logf("e2ee content not understood: %T", content.Content)
	}
	return nil, nil, ""
}

// What every kind of attachment has in common: its type and how big it is.
func describe(id, name string, transport *waMediaTransport.WAMediaTransport) *attachment {
	item := &attachment{
		id:   id,
		name: name,
		mime: transport.GetAncillary().GetMimetype(),
		size: int64(transport.GetAncillary().GetFileLength()),
	}
	if transport.GetIntegral().GetDirectPath() == "" {
		if thumbnail := inlineJPEG(transport); thumbnail != nil {
			item.mime = "image/jpeg"
			item.size = int64(len(thumbnail))
		}
	}
	return item
}

func directions(transport *waMediaTransport.WAMediaTransport, kind string) *secureMedia {
	if transport.GetIntegral().GetDirectPath() == "" && (kind != "image" || inlineJPEG(transport) == nil) {
		return nil
	}
	raw, err := proto.Marshal(transport)
	if err != nil {
		return nil
	}
	return &secureMedia{transport: raw, kind: kind}
}

// WAMediaTransport.Ancillary.Thumbnail.JPEGThumbnail is the image itself, even when there are no directions to a
// separate file. Keep it inside the existing serialized transport so a preview survives restart without a download.
// This is only offered for image media: a document or video's cover cannot stand in for the file it describes.
func inlineJPEG(transport *waMediaTransport.WAMediaTransport) []byte {
	data := transport.GetAncillary().GetThumbnail().GetJPEGThumbnail()
	if len(data) == 0 || len(data) > mediaLimit {
		return nil
	}
	if _, err := jpeg.DecodeConfig(bytes.NewReader(data)); err != nil {
		return nil
	}
	return data
}

// The file itself, fetched and decrypted. This is the only path that touches the network for an encrypted
// conversation's media, and it runs when the app asks to read it, not when the message arrives.
func (a *adapter) fetchSecure(ctx context.Context, held *secureMedia) ([]byte, string, bool) {
	if held == nil {
		return nil, "", false
	}
	var transport waMediaTransport.WAMediaTransport
	if proto.Unmarshal(held.transport, &transport) != nil {
		return nil, "", false
	}
	if transport.GetIntegral().GetDirectPath() == "" {
		if held.kind == "image" {
			if thumbnail := inlineJPEG(&transport); thumbnail != nil {
				return thumbnail, "image/jpeg", true
			}
		}
		return nil, "", false
	}
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil || crypto.client == nil {
		return nil, "", false
	}
	data, err := crypto.client.DownloadFB(ctx, transport.GetIntegral(), mediaKindOf(held.kind))
	if err != nil || len(data) == 0 {
		return nil, "", false
	}
	return data, transport.GetAncillary().GetMimetype(), true
}
