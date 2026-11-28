package main

import (
	"context"
	"encoding/json"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

func permittedAvatarURL(value string) bool {
	u, err := url.Parse(value)
	if err != nil || u.Scheme != "https" || u.User != nil || (u.Port() != "" && u.Port() != "443") {
		return false
	}
	host := strings.ToLower(u.Hostname())
	return host == "googleusercontent.com" || strings.HasSuffix(host, ".googleusercontent.com") || host == "ggpht.com" || strings.HasSuffix(host, ".ggpht.com")
}

const avatarCallTimeout = 5 * time.Second

// Every avatar answer carries "probe": how the picture was looked for and what shape each answer had, in a fixed
// vocabulary of counts, lengths and undeclared field numbers, never an identifier or a picture. On 2026-09-23, after the
// phone's Messages data was rebuilt, the phone and Messages for web both showed contact photos while this helper found
// none: directPartner and personAvatar record the two reasons the probe gave.
func (a *adapter) avatarCall(ctx context.Context, r request) (any, string) {
	if a.client == nil {
		return nil, "pairing_required"
	}
	var p struct {
		ChatID        string `json:"chatId"`
		ParticipantID string `json:"participantId"`
	}
	if json.Unmarshal(r.Params, &p) != nil || p.ChatID == "" {
		return nil, "invalid_input"
	}
	// Supplied only from this paired account's already-loaded conversation data.
	// This is still a normal authenticated thumbnail request to the phone.
	if p.ParticipantID != "" {
		data, probe, code := a.personAvatar(ctx, nil, p.ChatID, p.ParticipantID)
		if code != "" {
			return nil, code
		}
		return avatarAnswer(data, "via=person "+probe)
	}
	c, code := a.avatarConversation(ctx, p.ChatID)
	if code != "" {
		return nil, code
	}
	if c.GetIsGroupChat() {
		if !permittedAvatarURL(c.GetGroupAvatarURL()) {
			return avatarAnswer(nil, "via=group url=0")
		}
		call, cancel := context.WithTimeout(ctx, avatarCallTimeout)
		defer cancel()
		data, err := a.client.DownloadAvatar(call, c.GetGroupAvatarURL())
		if err != nil {
			return nil, readErrorCode(err)
		}
		return avatarAnswer(data, "via=group url=1")
	}
	partner, others, candidates := directPartner(c)
	shape := "via=chat others=" + strconv.Itoa(others) + " counterparts=" + strconv.Itoa(candidates)
	if partner == nil {
		return avatarAnswer(nil, shape)
	}
	data, probe, code := a.personAvatar(ctx, c, p.ChatID, partner.GetID().GetParticipantID())
	if code != "" {
		return nil, code
	}
	return avatarAnswer(data, shape+" "+probe)
}

func avatarAnswer(data []byte, probe string) (any, string) {
	if len(data) > 2*1024*1024 {
		return nil, "unexpected_response"
	}
	if data == nil {
		data = []byte{}
	}
	return map[string]any{"data": data, "probe": probe}, ""
}

func (a *adapter) avatarConversation(ctx context.Context, chatID string) (*gmproto.Conversation, string) {
	call, cancel := context.WithTimeout(ctx, avatarCallTimeout)
	defer cancel()
	c, err := a.client.GetConversation(call, chatID)
	if err != nil {
		return nil, readErrorCode(err)
	}
	if c == nil || c.GetConversationID() != chatID {
		return nil, "history_failed"
	}
	a.conversations.note(c)
	return c, ""
}

// The one other person in a direct conversation, by the same rule the conversation's header uses (counterparts): an RCS
// conversation lists the same person a second time under another identity, so "not me" alone found two people in ten of
// the user's conversations and no picture was asked for. Where that rule still leaves two, the one linked to a phone
// contact is the one whose photo the phone keeps.
func directPartner(c *gmproto.Conversation) (*gmproto.Participant, int, int) {
	counterpart := counterparts(c)
	others, candidates := 0, []*gmproto.Participant{}
	for _, person := range c.GetParticipants() {
		if person.GetIsMe() {
			continue
		}
		others++
		if counterpart(person) && person.GetID().GetParticipantID() != "" {
			candidates = append(candidates, person)
		}
	}
	if len(candidates) == 1 {
		return candidates[0], others, len(candidates)
	}
	var linked *gmproto.Participant
	for _, person := range candidates {
		if person.GetContactID() != "" {
			if linked != nil {
				return nil, others, len(candidates)
			}
			linked = person
		}
	}
	return linked, others, len(candidates)
}

// The phone contact each participant is linked to, from every conversation the helper maps, so a member's picture can be
// asked for by contact without loading the conversation again. One helper serves one paired account. Never logged.
type participantContacts struct {
	mu      sync.Mutex
	contact map[string]string
}

var knownContacts = &participantContacts{contact: map[string]string{}}

func (k *participantContacts) note(c *gmproto.Conversation) {
	k.mu.Lock()
	defer k.mu.Unlock()
	if len(k.contact) > 20000 {
		clear(k.contact)
	}
	for _, person := range c.GetParticipants() {
		if id := person.GetID().GetParticipantID(); id != "" {
			k.contact[id] = person.GetContactID()
		}
	}
}

func (k *participantContacts) of(participantID string) (string, bool) {
	k.mu.Lock()
	defer k.mu.Unlock()
	contact, ok := k.contact[participantID]
	return contact, ok
}

// A person's picture. Since 2026-09-23 the phone answers the participant thumbnail, which is what Google's own bridge asks
// for, with no entry at all, and the linked contact's thumbnail with the photo; so the contact is asked first and the
// participant after it, for a phone that still answers the older way. A failure with no picture found is returned as a
// failure, never as "no photo", so the app asks again rather than remembering an absence for days.
func (a *adapter) personAvatar(ctx context.Context, c *gmproto.Conversation, chatID, participantID string) ([]byte, string, string) {
	if c != nil {
		knownContacts.note(c)
	}
	contactID, known := knownContacts.of(participantID)
	probe := []string{}
	failure := ""
	askContact := func() []byte {
		call, cancel := context.WithTimeout(ctx, avatarCallTimeout)
		defer cancel()
		answer, err := a.client.GetContactThumbnail(call, contactID)
		if err != nil {
			failure = readErrorCode(err)
			probe = append(probe, "contact_error="+failure)
			return nil
		}
		data, shape := thumbnailPicture(answer, contactID)
		probe = append(probe, prefixed("contact_", shape))
		return data
	}
	if contactID != "" {
		if data := askContact(); len(data) > 0 {
			return data, "contact=1 " + strings.Join(probe, " "), ""
		}
	}
	call, cancel := context.WithTimeout(ctx, avatarCallTimeout)
	answer, err := a.client.GetParticipantThumbnail(call, participantID)
	cancel()
	if err != nil {
		failure = readErrorCode(err)
		probe = append(probe, "participant_error="+failure)
	} else {
		data, shape := thumbnailPicture(answer, participantID)
		probe = append(probe, prefixed("participant_", shape))
		if len(data) > 0 {
			return data, "contact=" + bit(contactID != "") + " " + strings.Join(probe, " "), ""
		}
	}
	if !known && c == nil {
		// Not seen in any conversation this helper has mapped yet: load this one to find the contact.
		if loaded, code := a.avatarConversation(ctx, chatID); code == "" {
			knownContacts.note(loaded)
			if contactID, _ = knownContacts.of(participantID); contactID != "" {
				if data := askContact(); len(data) > 0 {
					return data, "contact=1 loaded=1 " + strings.Join(probe, " "), ""
				}
			}
		}
	}
	if failure != "" {
		return nil, "", failure
	}
	return nil, "contact=" + bit(contactID != "") + " " + strings.Join(probe, " "), ""
}

func prefixed(prefix, shape string) string {
	return prefix + strings.ReplaceAll(shape, " ", " "+prefix)
}

// Whether a picture letterTile takes for Google's letter tile is dropped. It was off in 0.1.284 while real tiles and
// photos were measured; every picture is still scored in the probe.
const hideLetterTiles = true

// The picture answered for the identifier asked about, and the answer's shape for the log. An entry named for someone
// else is never used: it would be another person's face. A contact can come back with several pictures (up to four
// seen); each is scored as pic1_, pic2_ and so on, with its own declared size.
func thumbnailPicture(resp *gmproto.GetThumbnailResponse, asked string) ([]byte, string) {
	return choosePicture(resp, asked, hideLetterTiles)
}

func choosePicture(resp *gmproto.GetThumbnailResponse, asked string, skipTiles bool) ([]byte, string) {
	entries := resp.GetThumbnail()
	var data []byte
	matched, pictures, tiles, looks := 0, 0, 0, []string{}
	for _, entry := range entries {
		if entry.GetIdentifier() != asked && entry.GetIdentifier() != "" {
			continue
		}
		matched++
		picture := entry.GetData().GetImageBuffer()
		if len(picture) == 0 {
			continue
		}
		pictures++
		tile, shape := letterTile(picture)
		if pictures <= 4 {
			size := entry.GetData().GetDimensions()
			looks = append(looks, prefixed("pic"+strconv.Itoa(pictures)+"_", "bytes="+strconv.Itoa(len(picture))+
				" declared="+strconv.FormatInt(size.GetWidth(), 10)+"x"+strconv.FormatInt(size.GetHeight(), 10)+" "+shape))
		}
		if tile {
			tiles++
			if skipTiles {
				continue
			}
		}
		if data == nil {
			data = picture
		}
	}
	parts := []string{"entries=" + strconv.Itoa(len(entries)), "matched=" + strconv.Itoa(matched), "pictures=" + strconv.Itoa(pictures),
		"tiles=" + strconv.Itoa(tiles), "bytes=" + strconv.Itoa(len(data))}
	parts = append(parts, looks...)
	if len(entries) > 0 {
		first := entries[0].GetData()
		parts = append(parts, "int="+strconv.Itoa(int(first.GetSomeInt())), "dims="+bit(first.GetDimensions() != nil), "hash="+bit(first.GetMysteriousData() != nil))
		if unknown := undeclaredFields(entries[0].ProtoReflect().GetUnknown()); len(unknown) > 0 {
			parts = append(parts, "entry_extra="+strings.Join(unknown, "."))
		}
		if first != nil {
			if unknown := undeclaredFields(first.ProtoReflect().GetUnknown()); len(unknown) > 0 {
				parts = append(parts, "data_extra="+strings.Join(unknown, "."))
			}
		}
	}
	if unknown := undeclaredFields(resp.ProtoReflect().GetUnknown()); len(unknown) > 0 {
		parts = append(parts, "extra="+strings.Join(unknown, "."))
	}
	return data, strings.Join(parts, " ")
}
