package main

import (
	"bytes"
	"errors"
	"image/jpeg"
	"net/url"
	"strings"

	"go.mau.fi/whatsmeow/proto/armadilloutil"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"google.golang.org/protobuf/proto"
)

// The pinned library decodes this field as ImageTransport. Meta's published subprotocols.json instead names
// WAMediaTransport for ConsumerApplication.ExtendedTextMessage.thumbnail. Accept those two documented encodings,
// normalizing the latter to the image shape used by our media mapper. See docs/research/MESSENGER_SHARED_LINKS.md.
// Protobuf decoding alone is insufficient: the wrong nesting can unmarshal successfully into unrelated fields.
func decodeLinkThumbnail(msg *waConsumerApplication.ConsumerApplication_ExtendedTextMessage) (*waMediaTransport.ImageTransport, error) {
	sub := msg.GetThumbnail()
	if !supportedPictureVersion(sub.GetVersion()) {
		return nil, armadilloutil.ErrUnsupportedVersion
	}
	image, imageErr := decodePicture(&waMediaTransport.ImageTransport{}, sub)
	imageValid := imageErr == nil && linkThumbnailSource(image.GetIntegral().GetTransport())
	transport, transportErr := decodePicture(&waMediaTransport.WAMediaTransport{}, sub)
	transportValid := transportErr == nil && linkThumbnailSource(transport)
	if imageValid && transportValid {
		return nil, errors.New("ambiguous link thumbnail transport")
	}
	if imageValid {
		return image, nil
	}
	if !transportValid {
		return nil, errors.New("unsupported link thumbnail transport")
	}
	thumbnail := transport.GetAncillary().GetThumbnail()
	width, height := thumbnail.GetThumbnailWidth(), thumbnail.GetThumbnailHeight()
	if width == 0 || height == 0 {
		if data := inlineJPEG(transport); data != nil {
			config, _ := jpeg.DecodeConfig(bytes.NewReader(data)) // inlineJPEG already validated the JPEG header.
			if width == 0 {
				width = uint32(config.Width)
			}
			if height == 0 {
				height = uint32(config.Height)
			}
		}
	}
	return &waMediaTransport.ImageTransport{
		Integral: &waMediaTransport.ImageTransport_Integral{Transport: transport},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{
			Width: proto.Uint32(width), Height: proto.Uint32(height),
		},
	}, nil
}

// Require image bytes or a path at the schema's exact transport nesting. An image name, length or hash alone must
// not make the other schema look valid. Hash/key integrity is checked by DownloadFB, not by this shape decoder.
func linkThumbnailSource(transport *waMediaTransport.WAMediaTransport) bool {
	if transport == nil {
		return false
	}
	path := transport.GetIntegral().GetDirectPath()
	if path == "" {
		return inlineJPEG(transport) != nil
	}
	mime := strings.ToLower(transport.GetAncillary().GetMimetype())
	if mime != "" && !strings.HasPrefix(mime, "image/") {
		return false
	}
	if !strings.HasPrefix(path, "/") || strings.HasPrefix(path, "//") || strings.ContainsAny(path, "\\\x00\r\n") {
		return false
	}
	parsed, err := url.ParseRequestURI(path)
	return err == nil && parsed.Scheme == "" && parsed.Host == ""
}

// The subprotocol versions of a picture transport that this build will read. The pinned library declares 1, and
// Facebook is sending 2 for shared reels — seen in the log as "version=2 wanted=1", which is why a reel's own cover
// frame was refused and a picture fetched from the web page stood in for it. Both are listed rather than the check
// being dropped: an unknown version still gets no benefit of the doubt, because a payload that unmarshals cleanly
// can have been read at the wrong nesting.
var pictureVersions = []int32{1, 2}

func supportedPictureVersion(version int32) bool {
	for _, known := range pictureVersions {
		if version == known {
			return true
		}
	}
	return false
}

// The same payload read as the given shape, for a version this build knows. armadilloutil insists on one version, so
// the check is made here and the decode done directly.
func decodePicture[T proto.Message](into T, sub *waCommon.SubProtocol) (T, error) {
	if !supportedPictureVersion(sub.GetVersion()) {
		return into, armadilloutil.ErrUnsupportedVersion
	}
	return into, proto.Unmarshal(sub.GetPayload(), into)
}
