package main

import (
	"encoding/json"
	"io"
	"testing"
	"time"

	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	waTypes "go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

func TestConsumerLinkPreviewWithoutMimeCrossesHistoryAsImage(t *testing.T) {
	for _, wrapped := range []bool{false, true} {
		transport := carrier("/v/synthetic-thumbnail", "", 900)
		var payload proto.Message = transport
		if wrapped {
			payload = &waMediaTransport.ImageTransport{
				Integral: &waMediaTransport.ImageTransport_Integral{Transport: transport},
			}
		}
		preview := linkThumbnailMessage(t, payload)
		preview.CanonicalURL = proto.String("https://example.test/share")
		consumer := &waConsumerApplication.ConsumerApplication{
			Payload: &waConsumerApplication.ConsumerApplication_Payload{
				Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{
					Content: contentOf(&waConsumerApplication.ConsumerApplication_Content_ExtendedTextMessage{
						ExtendedTextMessage: preview,
					}),
				},
			},
		}
		// Use serialized consumer content through the real event handler, so the assertion covers application
		// dispatch, thumbnail schema selection, attachment assembly and the exact MIME sent across the UI boundary.
		raw, err := proto.Marshal(consumer)
		if err != nil {
			t.Fatal(err)
		}
		var decoded waConsumerApplication.ConsumerApplication
		if err := proto.Unmarshal(raw, &decoded); err != nil {
			t.Fatal(err)
		}
		evt := &events.FBMessage{Info: waTypes.MessageInfo{
			MessageSource: waTypes.MessageSource{Chat: waTypes.JID{User: "7"}, Sender: waTypes.JID{User: "42"}},
			ID:            "consumer-preview", Timestamp: time.UnixMilli(1700),
		}, Message: &decoded}
		a := &adapter{chats: newStore(), out: &output{encoder: json.NewEncoder(io.Discard)}}
		a.applyEncrypted(evt)
		page := a.chats.historyBefore(7, 0, 10)
		if len(page) != 1 || len(page[0].Media) != 1 {
			t.Fatalf("supported consumer preview must remain visible (wrapped=%v)", wrapped)
		}
		media := page[0].Media[0]
		if media.Mime != "image/jpeg" || media.Link != "https://example.test/share" || media.Video || media.Animated {
			t.Fatalf("consumer preview must cross the UI boundary as a still image with its browser link (wrapped=%v)", wrapped)
		}
		if held, ok := a.chats.secureFor(media.ID); !ok || held.kind != "image" {
			t.Fatalf("consumer preview must retain its download directions (wrapped=%v)", wrapped)
		}
	}
}
