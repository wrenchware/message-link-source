package main

import (
	"bytes"
	"context"
	"image"
	"image/color"
	"image/draw"
	_ "image/gif"
	_ "image/jpeg"
	"image/png"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	waTypes "go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
)

// How Messenger carries a file of a given kind. The upload type decides which key schedule encrypts it; the message
// shape decides how the other end draws it; the read name is what our own download path calls that shape, so a file
// this computer sends is described exactly the way one it receives is.
type mediaShape struct {
	upload whatsmeow.MediaType
	shape  string
	gif    bool
	// An audio message recorded in the app's composer, sent as a voice message (push-to-talk) rather than a file.
	voice bool
	// How long that audio runs, in whole seconds; zero when the app did not say. Messenger draws a voice message with its
	// length before it is played, and upstream's bridge fills this in for every audio message it sends.
	seconds uint32
}

func shapeOf(mime string) mediaShape {
	mime = strings.ToLower(mime)
	switch {
	// Facebook wants an animation in a video message, and not as video/mp4 either. Upstream's bridge does the same.
	case mime == "image/gif":
		return mediaShape{upload: whatsmeow.MediaVideo, shape: "video", gif: true}
	case strings.HasPrefix(mime, "image/"):
		return mediaShape{upload: whatsmeow.MediaImage, shape: "image"}
	case strings.HasPrefix(mime, "video/"):
		return mediaShape{upload: whatsmeow.MediaVideo, shape: "video"}
	case strings.HasPrefix(mime, "audio/"):
		return mediaShape{upload: whatsmeow.MediaAudio, shape: "audio"}
	default:
		return mediaShape{upload: whatsmeow.MediaDocument, shape: "document"}
	}
}

// Sends one file into an encrypted conversation. Nothing about this is shared with the ordinary connection: the bytes
// are encrypted here, uploaded to Meta's media servers, and the message carries only the key and the path to them.
func (a *adapter) sendEncryptedMedia(ctx context.Context, key int64, staged []byte, name, mime, text string, voice bool, seconds uint32) (any, string) {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	// A socket that was started but never came up refuses a file in a way that reads like Facebook rejecting it.
	if !a.encryptedReady() {
		logf("sendMedia chat=%s encrypted socket is not up", tag(key))
		return nil, "e2ee_offline"
	}
	to, ok := a.encryptedAddress(key)
	if !ok {
		logf("sendMedia chat=%s has no encrypted address", tag(key))
		return nil, "send_failed"
	}
	kind := shapeOf(mime)
	kind.voice = voice && kind.shape == "audio"
	if kind.shape == "audio" {
		kind.seconds = seconds
	}
	data, width, height, ok := preparePicture(staged, mime)
	if !ok {
		logf("sendMedia chat=%s encrypted picture too large to prepare", tag(key))
		return nil, "invalid_input"
	}
	logf("sendMedia chat=%s encrypted bytes=%d shape=%s dimensions=%v", tag(key), len(data), kind.shape, width != 0)
	uploaded, err := crypto.client.Upload(ctx, data, kind.upload)
	if err != nil {
		logf("sendMedia chat=%s encrypted upload failed: %s", tag(key), scrub(err.Error()))
		return nil, "send_failed"
	}
	content, err := wrapEncryptedMedia(kind, &uploaded, data, name, mime, text, width, height)
	if err != nil {
		logf("sendMedia chat=%s could not be described for Messenger", tag(key))
		return nil, "send_failed"
	}
	started := time.Now()
	id := strconv.FormatInt(methods.GenerateEpochID(), 10)
	message := &waConsumerApplication.ConsumerApplication{
		Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{Content: content},
		},
	}
	receipt, err := crypto.client.SendFBMessage(ctx, to, message, nil, whatsmeow.SendRequestExtra{ID: waTypes.MessageID(id)})
	if err != nil {
		logf("sendMedia chat=%s encrypted send failed: %s", tag(key), scrub(err.Error()))
		return nil, "send_failed"
	}
	at := encryptedSendTimestamp(receipt.Timestamp, started)
	// Described by the same reader that describes an arriving file, from the message that was actually sent. What the
	// conversation shows for it, and how it is fetched again after a restart, is then one path rather than two.
	item, held, _ := encryptedMedia(content, id)
	touched := a.chats.addEncrypted(key, id, a.chats.viewer(), text, "", at)
	if item != nil && held != nil {
		a.chats.attachSecure(key, id, at, item, held)
	}
	crypto.messages.remember(ctx, key, id, a.chats.viewer(), text, "", at)
	if item != nil && held != nil {
		crypto.messages.rememberMedia(ctx, key, id, at, item, held)
	}
	a.out.write(response{Event: "changed", Result: touched})
	a.readAfterSend(key, at)
	logf("sendMedia chat=%s encrypted accepted message=%s drawn=%v", tag(key), tagOf(id), item != nil && held != nil)
	return map[string]any{"outcome": "accepted", "id": id, "otid": id}, ""
}

// Messenger's native clients send a multi-photo selection as a tight sequence of ordinary ConsumerApplication
// image messages. The Armadillo ImageGalleryMessage schema is accepted by the server but silently discarded before
// delivery to Messenger clients. Upload every file first, then submit the prepared image messages as one helper-level
// operation so an upload failure cannot leave a partial set.
func (a *adapter) sendEncryptedMediaBatch(ctx context.Context, key int64, staged [][]byte, files []mediaBatchFile) (any, string) {
	a.mu.Lock()
	crypto := a.crypto
	a.mu.Unlock()
	if crypto == nil {
		return nil, "e2ee_off"
	}
	if !a.encryptedReady() {
		return nil, "e2ee_offline"
	}
	to, ok := a.encryptedAddress(key)
	if !ok {
		return nil, "send_failed"
	}
	type preparedImage struct {
		data          []byte
		width, height int
		mime          string
		uploaded      whatsmeow.UploadResponse
	}
	prepared := make([]preparedImage, 0, len(staged))
	for index, original := range staged {
		data, width, height, ok := preparePicture(original, files[index].Mime)
		if !ok {
			logf("sendMediaBatch chat=%s encrypted picture too large to prepare index=%d", tag(key), index)
			return nil, "invalid_input"
		}
		uploaded, err := crypto.client.Upload(ctx, data, whatsmeow.MediaImage)
		if err != nil {
			logf("sendMediaBatch chat=%s encrypted upload failed index=%d: %s", tag(key), index, scrub(err.Error()))
			return nil, "upload_failed"
		}
		prepared = append(prepared, preparedImage{data: data, width: width, height: height, mime: files[index].Mime, uploaded: uploaded})
	}
	ids := make([]string, 0, len(prepared))
	var last int64
	chat := strconv.FormatInt(key, 10)
	groupID := strconv.FormatInt(methods.GenerateEpochID(), 10)
	for index, image := range prepared {
		content, err := wrapEncryptedMedia(shapeOf(image.mime), &image.uploaded, image.data, files[index].Name, image.mime, "", image.width, image.height)
		if err != nil {
			logf("sendMediaBatch chat=%s encrypted describe failed index=%d", tag(key), index)
			return nil, "send_failed"
		}
		message := &waConsumerApplication.ConsumerApplication{Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{Content: content},
		}}
		started := time.Now()
		id := strconv.FormatInt(methods.GenerateEpochID(), 10)
		receipt, err := crypto.client.SendFBMessage(ctx, to, message,
			mediaBatchMetadata(groupID, len(prepared), index), whatsmeow.SendRequestExtra{ID: waTypes.MessageID(id)})
		if err != nil {
			logf("sendMediaBatch chat=%s encrypted send failed index=%d submitted=%d: %s", tag(key), index, len(ids), scrub(err.Error()))
			if len(ids) > 0 {
				return map[string]any{"outcome": "unknown", "id": ids[0], "ids": ids}, ""
			}
			return nil, "send_failed"
		}
		at := encryptedSendTimestamp(receipt.Timestamp, started)
		a.chats.addEncrypted(key, id, a.chats.viewer(), "", "", at)
		item, held, _ := encryptedMedia(content, id)
		if item != nil && held != nil {
			a.chats.attachSecure(key, id, at, item, held)
		}
		crypto.messages.remember(ctx, key, id, a.chats.viewer(), "", "", at)
		if item != nil && held != nil {
			crypto.messages.rememberMedia(ctx, key, id, at, item, held)
		}
		last = at
		ids = append(ids, id)
	}
	a.out.write(response{Event: "changed", Result: &changes{Chats: []string{chat}, Messages: map[string][]string{chat: ids}}})
	a.readAfterSend(key, last)
	logf("sendMediaBatch chat=%s encrypted accepted message=%s count=%d nativeSequence=true", tag(key), tagOf(ids[0]), len(ids))
	return map[string]any{"outcome": "accepted", "id": ids[0], "otid": ids[0], "ids": ids}, ""
}

func mediaBatchMetadata(groupID string, size, index int) *waMsgApplication.MessageApplication_Metadata {
	return &waMsgApplication.MessageApplication_Metadata{
		GroupID:    proto.String(groupID),
		GroupSize:  proto.Uint32(uint32(size)),
		GroupIndex: proto.Uint32(uint32(index)),
	}
}

// The largest picture decoded here to be re-encoded, the bounds a link preview's image is held to
// (linkpreview_fetch.go). A header can claim any size, and decoding allocates for what it claims.
const (
	pictureMaxSide   = 16384
	pictureMaxPixels = 40_000_000
)

// A picture's size, and the one encoding Messenger refuses. Anything that is not an image is handed back untouched.
// ok is false only for a PNG that needs re-encoding and is too large to decode: it is refused before anything is
// uploaded, rather than sent in a colour model Messenger draws as a blank box.
func preparePicture(data []byte, mime string) (_ []byte, width, height int, ok bool) {
	if !strings.HasPrefix(strings.ToLower(mime), "image/") {
		return data, 0, 0, true
	}
	config, _, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil {
		return data, 0, 0, true
	}
	// Messenger will not display a PNG in certain colour models. Re-encoding alone is not enough: the encoder keeps
	// a paletted or grey picture in the model it found, so the pixels are copied into an NRGBA image first. A picture
	// that arrives as a blank box is worse than one sent a few kilobytes larger.
	if strings.EqualFold(mime, "image/png") && config.ColorModel != color.NRGBAModel {
		if config.Width < 1 || config.Height < 1 || config.Width > pictureMaxSide || config.Height > pictureMaxSide ||
			int64(config.Width)*int64(config.Height) > pictureMaxPixels {
			return data, 0, 0, false
		}
		if decoded, err := png.Decode(bytes.NewReader(data)); err == nil {
			bounds := decoded.Bounds()
			plain := image.NewNRGBA(image.Rect(0, 0, bounds.Dx(), bounds.Dy()))
			draw.Draw(plain, plain.Bounds(), decoded, bounds.Min, draw.Src)
			var buffer bytes.Buffer
			if png.Encode(&buffer, plain) == nil {
				data = buffer.Bytes()
			}
		}
	}
	return data, config.Width, config.Height, true
}

// 400 is the size Messenger's own clients expect a cover to be described as. The field is not optional in practice:
// iOS and Android refuse to draw media whose transport carries no thumbnail dimensions at all, which is why a file
// whose size this build cannot read is still described as something rather than as nothing.
func clampToCover(width, height int) (uint32, uint32) {
	if width > 400 {
		height = height * 400 / width
		width = 400
	}
	if height > 400 {
		width = width * 400 / height
		height = 400
	}
	if width <= 0 || height <= 0 {
		return 400, 400
	}
	return uint32(width), uint32(height)
}

// The message that names an uploaded file: where it sits, the key that opens it, and what kind of thing it is.
func wrapEncryptedMedia(kind mediaShape, uploaded *whatsmeow.UploadResponse, data []byte, name, mime, text string, width, height int) (*waConsumerApplication.ConsumerApplication_Content, error) {
	transport := encryptedTransport(uploaded, data, mime, width, height)
	caption := &waCommon.MessageText{}
	if text != "" {
		caption = &waCommon.MessageText{Text: proto.String(text)}
	}
	content := &waConsumerApplication.ConsumerApplication_Content{}
	switch kind.shape {
	case "image":
		message := &waConsumerApplication.ConsumerApplication_ImageMessage{Caption: caption}
		if err := message.Set(wrapEncryptedImageTransportWithCarrier(transport, width, height)); err != nil {
			return nil, err
		}
		content.Content = &waConsumerApplication.ConsumerApplication_Content_ImageMessage{ImageMessage: message}
	case "video":
		message := &waConsumerApplication.ConsumerApplication_VideoMessage{Caption: caption}
		ancillary := &waMediaTransport.VideoTransport_Ancillary{
			Width: proto.Uint32(uint32(width)), Height: proto.Uint32(uint32(height)),
		}
		if kind.gif {
			ancillary.GifPlayback = proto.Bool(true)
		}
		if err := message.Set(&waMediaTransport.VideoTransport{
			Integral:  &waMediaTransport.VideoTransport_Integral{Transport: transport},
			Ancillary: ancillary,
		}); err != nil {
			return nil, err
		}
		content.Content = &waConsumerApplication.ConsumerApplication_Content_VideoMessage{VideoMessage: message}
	case "audio":
		// A voice note only when it was recorded as one in the app's composer. A file the person chose is not, and saying
		// otherwise would draw it as one they recorded.
		message := &waConsumerApplication.ConsumerApplication_AudioMessage{PTT: proto.Bool(kind.voice)}
		ancillary := &waMediaTransport.AudioTransport_Ancillary{}
		if kind.seconds > 0 {
			ancillary.Seconds = proto.Uint32(kind.seconds)
		}
		if err := message.Set(&waMediaTransport.AudioTransport{
			Integral:  &waMediaTransport.AudioTransport_Integral{Transport: transport},
			Ancillary: ancillary,
		}); err != nil {
			return nil, err
		}
		content.Content = &waConsumerApplication.ConsumerApplication_Content_AudioMessage{AudioMessage: message}
	default:
		message := &waConsumerApplication.ConsumerApplication_DocumentMessage{FileName: proto.String(name)}
		if err := message.Set(&waMediaTransport.DocumentTransport{
			Integral:  &waMediaTransport.DocumentTransport_Integral{Transport: transport},
			Ancillary: &waMediaTransport.DocumentTransport_Ancillary{},
		}); err != nil {
			return nil, err
		}
		content.Content = &waConsumerApplication.ConsumerApplication_Content_DocumentMessage{DocumentMessage: message}
	}
	return content, nil
}

func encryptedTransport(uploaded *whatsmeow.UploadResponse, data []byte, mime string, width, height int) *waMediaTransport.WAMediaTransport {
	coverWidth, coverHeight := clampToCover(width, height)
	return &waMediaTransport.WAMediaTransport{
		Integral: &waMediaTransport.WAMediaTransport_Integral{
			FileSHA256:        uploaded.FileSHA256,
			MediaKey:          uploaded.MediaKey,
			FileEncSHA256:     uploaded.FileEncSHA256,
			DirectPath:        proto.String(uploaded.DirectPath),
			MediaKeyTimestamp: proto.Int64(time.Now().Unix()),
		},
		Ancillary: &waMediaTransport.WAMediaTransport_Ancillary{
			FileLength: proto.Uint64(uint64(len(data))),
			Mimetype:   proto.String(mime),
			Thumbnail: &waMediaTransport.WAMediaTransport_Ancillary_Thumbnail{
				ThumbnailWidth:  proto.Uint32(coverWidth),
				ThumbnailHeight: proto.Uint32(coverHeight),
			},
			ObjectID: proto.String(uploaded.ObjectID),
		},
	}
}

func wrapEncryptedImageTransport(uploaded *whatsmeow.UploadResponse, data []byte, mime string, width, height int) *waMediaTransport.ImageTransport {
	return wrapEncryptedImageTransportWithCarrier(encryptedTransport(uploaded, data, mime, width, height), width, height)
}

func wrapEncryptedImageTransportWithCarrier(transport *waMediaTransport.WAMediaTransport, width, height int) *waMediaTransport.ImageTransport {
	return &waMediaTransport.ImageTransport{
		Integral:  &waMediaTransport.ImageTransport_Integral{Transport: transport},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{Width: proto.Uint32(uint32(width)), Height: proto.Uint32(uint32(height))},
	}
}
