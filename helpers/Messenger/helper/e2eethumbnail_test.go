package main

import (
	"errors"
	"testing"

	"go.mau.fi/whatsmeow/proto/armadilloutil"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"google.golang.org/protobuf/proto"
)

func linkThumbnailMessage(t *testing.T, payload proto.Message) *waConsumerApplication.ConsumerApplication_ExtendedTextMessage {
	t.Helper()
	sub, err := armadilloutil.Marshal(payload, waConsumerApplication.ImageTransportVersion)
	if err != nil {
		t.Fatal(err)
	}
	return &waConsumerApplication.ConsumerApplication_ExtendedTextMessage{Thumbnail: sub}
}

func TestDecodeLinkThumbnailPreservesPinnedImageTransport(t *testing.T) {
	transport := carrier("/v/thumbnail?token=synthetic", "image/jpeg", 900)
	original := &waMediaTransport.ImageTransport{
		Integral: &waMediaTransport.ImageTransport_Integral{Transport: transport},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{
			Width: proto.Uint32(640), Height: proto.Uint32(360),
		},
	}
	msg := linkThumbnailMessage(t, original)
	// The wrong schema still accepts these bytes. It incorrectly treats the inner transport as a hash.
	var wrong waMediaTransport.WAMediaTransport
	if err := proto.Unmarshal(msg.Thumbnail.Payload, &wrong); err != nil {
		t.Fatal("fixture must exercise successful protobuf decoding at the wrong nesting")
	}
	if len(wrong.GetIntegral().GetFileSHA256()) == 0 || linkThumbnailSource(&wrong) {
		t.Fatal("the wrong nesting must contain fields but no usable image source")
	}
	actual, err := decodeLinkThumbnail(msg)
	if err != nil || !proto.Equal(actual, original) {
		t.Fatal("the pinned image encoding must retain its original transport and dimensions")
	}
}

func TestDecodeLinkThumbnailNormalizesMetaMediaTransport(t *testing.T) {
	transport := carrier("/v/thumbnail?token=synthetic", "image/jpeg", 900)
	transport.Ancillary.Thumbnail = &waMediaTransport.WAMediaTransport_Ancillary_Thumbnail{
		ThumbnailWidth: proto.Uint32(320), ThumbnailHeight: proto.Uint32(180),
	}
	actual, err := decodeLinkThumbnail(linkThumbnailMessage(t, transport))
	if err != nil || !proto.Equal(actual.GetIntegral().GetTransport(), transport) || actual.GetAncillary().GetWidth() != 320 || actual.GetAncillary().GetHeight() != 180 {
		t.Fatal("Meta's direct media encoding must retain download metadata and thumbnail dimensions")
	}
}

func TestDecodeLinkThumbnailAcceptsBothInlineImageEncodings(t *testing.T) {
	_, transport := inlineThumbnail(t)
	for _, wrapped := range []bool{false, true} {
		var payload proto.Message = transport
		if wrapped {
			payload = &waMediaTransport.ImageTransport{
				Integral: &waMediaTransport.ImageTransport_Integral{Transport: transport},
				Ancillary: &waMediaTransport.ImageTransport_Ancillary{
					Width: proto.Uint32(4), Height: proto.Uint32(3),
				},
			}
		}
		actual, err := decodeLinkThumbnail(linkThumbnailMessage(t, payload))
		if err != nil || !proto.Equal(actual.GetIntegral().GetTransport(), transport) || actual.GetAncillary().GetWidth() != 4 || actual.GetAncillary().GetHeight() != 3 {
			t.Fatalf("inline image transport must retain bytes and useful dimensions (wrapped=%v)", wrapped)
		}
	}
}

func TestDecodeLinkThumbnailRejectsUnavailableOrMalformedSources(t *testing.T) {
	for _, path := range []string{"", "https://example.test/image", "//example.test/image", "relative", "/invalid\\path", "/invalid\npath", "/invalid%zz"} {
		if actual, err := decodeLinkThumbnail(linkThumbnailMessage(t, carrier(path, "image/jpeg", 900))); err == nil || actual != nil {
			t.Fatal("an invalid encrypted media path must not produce an image transport")
		}
	}
	for _, payload := range []proto.Message{
		&waMediaTransport.WAMediaTransport{},
		&waMediaTransport.ImageTransport{},
		&waMediaTransport.WAMediaTransport_Integral{DirectPath: proto.String("/one-level-too-high")},
		carrier("/video", "video/mp4", 123),
		&waMediaTransport.WAMediaTransport{Ancillary: &waMediaTransport.WAMediaTransport_Ancillary{
			Thumbnail: &waMediaTransport.WAMediaTransport_Ancillary_Thumbnail{JPEGThumbnail: []byte("not an image")},
		}},
	} {
		if actual, err := decodeLinkThumbnail(linkThumbnailMessage(t, payload)); err == nil || actual != nil {
			t.Fatal("empty, non-image or wrong-nesting payload must not become a thumbnail")
		}
	}
	malformed := &waConsumerApplication.ConsumerApplication_ExtendedTextMessage{
		Thumbnail: &waCommon.SubProtocol{Version: proto.Int32(1), Payload: []byte{0x0a, 0xff}},
	}
	if actual, err := decodeLinkThumbnail(malformed); err == nil || actual != nil {
		t.Fatal("malformed payload must remain rejected by both documented decoders")
	}
	_, withInline := inlineThumbnail(t)
	withInline.Integral = &waMediaTransport.WAMediaTransport_Integral{DirectPath: proto.String("/video")}
	withInline.Ancillary.Mimetype = proto.String("video/mp4")
	if actual, err := decodeLinkThumbnail(linkThumbnailMessage(t, withInline)); err == nil || actual != nil {
		t.Fatal("a cover image must not cause video download metadata to be returned as an image")
	}
}

// Versions 1 and 2 are the ones Facebook has been seen to send; anything else still gets no benefit of the doubt.
func TestDecodeLinkThumbnailRejectsUnsupportedVersion(t *testing.T) {
	msg := linkThumbnailMessage(t, carrier("/thumbnail", "image/jpeg", 10))
	for _, version := range []int32{0, 3, 99} {
		msg.Thumbnail.Version = proto.Int32(version)
		if actual, err := decodeLinkThumbnail(msg); !errors.Is(err, armadilloutil.ErrUnsupportedVersion) || actual != nil {
			t.Fatal("unsupported versions must not fall through to another schema")
		}
	}
	if actual, err := decodeLinkThumbnail(nil); !errors.Is(err, armadilloutil.ErrUnsupportedVersion) || actual != nil {
		t.Fatal("missing thumbnail must be rejected without panicking")
	}
}

func TestDecodeLinkThumbnailRejectsAmbiguousNesting(t *testing.T) {
	// WAMediaTransport.Integral.fileSHA256 and ImageTransport.Integral.transport both occupy bytes field 1.
	// Make the hash bytes a valid transport to prove a parser cannot safely choose whichever unmarshal succeeds first.
	inner := carrier("/inner-thumbnail", "image/jpeg", 10)
	innerBytes, err := proto.Marshal(inner)
	if err != nil {
		t.Fatal(err)
	}
	outer := carrier("/outer-thumbnail", "image/jpeg", 10)
	outer.Integral.FileSHA256 = innerBytes
	msg := linkThumbnailMessage(t, outer)
	wrong, err := msg.DecodeThumbnail()
	if err != nil || !linkThumbnailSource(wrong.GetIntegral().GetTransport()) {
		t.Fatal("fixture must present two viable protobuf interpretations")
	}
	if actual, err := decodeLinkThumbnail(msg); err == nil || actual != nil {
		t.Fatal("ambiguous nesting must be rejected rather than selecting the wrong file")
	}
}
