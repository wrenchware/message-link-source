package main

import (
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func TestPreviewOnlyMediaRequestsFullSizeOnce(t *testing.T) {
	if needsFullSize(&gmproto.MediaContent{MediaID: "full", ThumbnailMediaID: "thumb"}) {
		t.Fatal("media with a full-size ID must not be re-requested")
	}
	if !needsFullSize(&gmproto.MediaContent{ThumbnailMediaID: "thumb"}) || !needsFullSize(&gmproto.MediaContent{MediaData: []byte{1}}) {
		t.Fatal("thumbnail or inline preview only needs the full size")
	}
	if needsFullSize(&gmproto.MediaContent{}) {
		t.Fatal("in-progress media with nothing to show waits for the phone's download")
	}

	part := "part1"
	m := &gmproto.Message{MessageID: "m1", ConversationID: "c1", MessageInfo: []*gmproto.MessageInfo{{
		ActionMessageID: &part,
		Data:            &gmproto.MessageInfo_MediaContent{MediaContent: &gmproto.MediaContent{ThumbnailMediaID: "thumb", MimeType: "image/jpeg"}},
	}}}
	a := &adapter{}
	v := a.viewMessage(m)
	if len(v.Media) != 1 || v.Media[0].FullSize {
		t.Fatal("a preview-only descriptor must not claim full size")
	}
	target := a.fullSizeTargets[v.Media[0].ID]
	if target.messageID != "m1" || target.actionID != "part1" {
		t.Fatal("full-size request target not recorded")
	}
	if !a.claimFullSizeRequest(target) || a.claimFullSizeRequest(target) {
		t.Fatal("full size must be requested exactly once per part")
	}
	if a.claimFullSizeRequest(fullSizeTarget{messageID: "m2"}) {
		t.Fatal("a part without an action message ID cannot be requested")
	}

	full := &gmproto.Message{MessageID: "m3", ConversationID: "c1", MessageInfo: []*gmproto.MessageInfo{{
		Data: &gmproto.MessageInfo_MediaContent{MediaContent: &gmproto.MediaContent{MediaID: "full", MimeType: "image/jpeg"}},
	}}}
	if v := a.viewMessage(full); !v.Media[0].FullSize {
		t.Fatal("full-size media must be reported as such")
	}
}
