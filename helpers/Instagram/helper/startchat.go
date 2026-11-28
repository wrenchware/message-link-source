package main

import (
	"context"
	"encoding/json"
	"errors"
	"strconv"
	"strings"
	"unicode/utf8"

	"go.mau.fi/mautrix-meta/pkg/instameow"
	"go.mau.fi/mautrix-meta/pkg/instameow/slidetypes"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
)

// Finding somebody to write to, and opening the conversation with them.
//
// Instagram has no call that creates an empty conversation. What it has is a route lookup, FetchThreadID, that
// answers with the thread two accounts already share, and that thread exists before either side has said anything.
// So starting a chat here is a lookup rather than a creation: the conversation is opened, never invented, and it
// arrives carrying Instagram's own two ids the same way every other conversation in this helper does.
//
// The other path upstream keeps is a send addressed to recipient_igids with no thread id, which brings the thread
// into being as a side effect of the first message. It is deliberately not taken here, for two reasons. It would
// hand the app a conversation with no Instagram id to name it by, and conversation ids here are Instagram's own, so
// there would be nothing to call it until the message landed. And upstream's own note on that field asks whether it
// should carry Instagram ids rather than messaging ids — the two-id confusion stage 3 went looking for — which is
// not a question to answer by guessing against somebody's account. A lookup that finds no thread says so.
type directoryClient interface {
	GetOwnFBID() int64
	SearchUsers(context.Context, string) (*slidetypes.SearchResponse, error)
	FetchThreadID(context.Context, int64) (*instameow.ThreadIGIDs, error)
	GetThread(context.Context, *slidetypes.GetThreadInfoRequest) (*slidetypes.ThreadInfoResponse, error)
	SendMessage(context.Context, *slidetypes.SendTextRequest) (*slidetypes.SendTextResponse, error)
	CreateGroup(context.Context, *slidetypes.CreateGroupRequest) (*slidetypes.CreateGroupResponse, error)
	EditGroupTitle(context.Context, string, string) error
}

// Somebody who could be written to. The id is the messaging FBID, because that is the number a message's sender
// carries and the number a thread's participants carry; Instagram's own user id ("pk") is a different number for the
// same person and is deliberately not returned, so it cannot be mistaken for this one later.
type uiCandidate struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Username string `json:"username,omitempty"`
	Avatar   string `json:"avatar,omitempty"`
	Verified bool   `json:"verified,omitempty"`
}

// Long enough for any name or handle, short enough that a runaway caller cannot use search as a tunnel.
const maxSearchLength = 200

// Fewer characters match too much to be worth a request on the real account. The application asks for three or more (its own
// rules, and the backend holds them too); the helper refuses fewer whoever sent them, so no client can use search to sweep the
// directory a letter at a time.
const minSearchLength = 3

// Instagram returns far more than a picker can use, and every extra row costs an avatar download later.
const maxSearchResults = 25

// Both of these exist only to serve the app's compose flow, so they are gated on the same capability the sends are
// rather than on being reads. Neither changes the account, but the diagnostics have no business searching Instagram
// for people, and a mode that merely never asks is not the same as one that would refuse.
func (a *readOnlyAdapter) directoryClientFor() (directoryClient, string) {
	a.mu.Lock()
	client := a.client
	verified := a.snapshot.IdentityVerified
	allowed := a.canMutate
	// Search hands back avatars and starting a chat maps a whole conversation, so both stores have to exist by here
	// the same way every conversation read makes sure of the chat store.
	if a.chats == nil {
		a.chats = newChatStore()
	}
	if a.media == nil {
		a.media = newMediaStore()
	}
	a.mu.Unlock()
	if !allowed {
		return nil, "read_only"
	}
	if client == nil || !verified {
		return nil, "login_required"
	}
	directory, ok := client.(directoryClient)
	if !ok {
		return nil, "unsupported"
	}
	return directory, ""
}

func (a *readOnlyAdapter) uiSearchUsers(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.directoryClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		Query string `json:"query"`
	}
	if json.Unmarshal(raw, &params) != nil || len(params.Query) > maxSearchLength || utf8.RuneCountInString(strings.TrimSpace(params.Query)) < minSearchLength {
		return nil, "invalid_params"
	}
	response, err := client.SearchUsers(ctx, params.Query)
	a.requests.note(ctx, &a.requests.directory, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	if response == nil {
		return nil, "invalid_response"
	}
	self := client.GetOwnFBID()
	results := make([]uiCandidate, 0, len(response.Data.Results))
	for _, result := range response.Data.Results {
		// A row with no messaging id cannot be written to and cannot be addressed; it is dropped rather than
		// offered as somebody the app will fail to open later.
		if result == nil || result.InteropMessagingUserFBID <= 0 || result.InteropMessagingUserFBID == self {
			continue
		}
		// An account with no full name set is common on Instagram, and a nameless row is unusable, so the handle
		// stands in — the same fallback a direct conversation's title uses.
		name := result.FullName
		if name == "" {
			name = result.Username
		}
		if name == "" {
			continue
		}
		results = append(results, uiCandidate{
			ID:       strconv.FormatInt(result.InteropMessagingUserFBID, 10),
			Name:     name,
			Username: result.Username,
			// Named by an id the helper issued. The app never receives an Instagram address and never fetches one.
			Avatar:   a.media.remember("", result.ProfilePicURL),
			Verified: result.IsVerified,
		})
		if len(results) == maxSearchResults {
			break
		}
	}
	return results, ""
}

// Opens the conversation this account already shares with one person. The thread is Instagram's, so what comes back
// is an ordinary conversation with ordinary ids, indistinguishable from one the list loaded.
func (a *readOnlyAdapter) uiStartDirect(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.directoryClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		PersonID string `json:"personId"`
	}
	if json.Unmarshal(raw, &params) != nil || params.PersonID == "" {
		return nil, "invalid_params"
	}
	person, err := strconv.ParseInt(params.PersonID, 10, 64)
	if err != nil || person <= 0 {
		return nil, "invalid_params"
	}
	if person == client.GetOwnFBID() {
		return nil, "chat_is_self"
	}
	// Instagram has no thread for this pair yet when this refuses. Creating one means sending into it, which is a
	// different decision and a different call; saying so is better than sending something nobody asked to send.
	chat, code := a.resolveDirect(ctx, client, person, params.PersonID, nil)
	if code != "" {
		return nil, code
	}
	return chat, ""
}

// What a first message produced: the conversation it now lives in, and the message itself. The two are reported
// separately because they can genuinely disagree — Instagram's send answer carries a message id and a time and no
// thread id at all, so the conversation has to be looked up afterwards, and that lookup can fail on its own. A
// message that was sent is never reported as a failure just because the conversation could not be drawn yet.
type uiStartedChat struct {
	Chat      *uiChat `json:"chat,omitempty"`
	MessageID string  `json:"messageId"`
	At        int64   `json:"at"`
}

// Writing to somebody this account has no conversation with yet.
//
// This is the one path that addresses a person rather than a thread. Instagram has no call that creates an empty
// conversation, so the thread comes into being as a side effect of the first message: the send carries
// recipient_igids and no thread id, and the conversation only exists, and only has an id, once that message lands.
//
// Two things about it are worth stating plainly, because neither is settled. The field is named for Instagram ids
// but upstream fills it with the messaging FBID and leaves a note asking whether that is right
// (third_party/mautrix-meta/pkg/msgconv/igconv/from-matrix.go); the messaging id is used here for the same reason
// upstream uses it — it is the id the thread's participants and the route lookup both carry — and if Instagram
// disagrees, it disagrees loudly, by refusing the send rather than by delivering to the wrong person. And the send
// is never retried, whatever happens after it: the message may already be in somebody's conversation.
func (a *readOnlyAdapter) uiStartDirectWithText(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.directoryClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		PersonID string `json:"personId"`
		Text     string `json:"text"`
	}
	if json.Unmarshal(raw, &params) != nil || params.PersonID == "" ||
		params.Text == "" || len(params.Text) > maxSendLength {
		return nil, "invalid_params"
	}
	person, err := strconv.ParseInt(params.PersonID, 10, 64)
	if err != nil || person <= 0 {
		return nil, "invalid_params"
	}
	if person == client.GetOwnFBID() {
		return nil, "chat_is_self"
	}
	// Whether a conversation already exists decides how the message is addressed, so it is resolved in full before
	// anything is sent rather than assumed from what the picker believed a moment ago. Resolving rather than trusting
	// the route lookup's own short id matters twice over: the id a send addresses is the one the thread itself
	// carried, never one derived from another call, and a conversation that turns out to be a message request must
	// refuse this message exactly as an ordinary reply to it would.
	existing, code := a.resolveDirect(ctx, client, person, params.PersonID, nil)
	if code != "" && code != "chat_not_started" {
		return nil, code
	}
	request := &slidetypes.SendTextRequest{
		OfflineThreadingID: strconv.FormatInt(methods.GenerateEpochID(), 10),
		Text:               slidetypes.SensitiveString{Value: params.Text},
		SendAttribution:    ptr(sendAttribution),
	}
	if existing != nil {
		// The conversation turned out to exist after all, so nothing about this call is special any more: it is
		// addressed the ordinary way, held to the same standing, and the recipient field stays empty.
		if existing.Folder != "inbox" {
			return nil, "chat_not_writable"
		}
		if existing.shortID == "" {
			return nil, "chat_unknown"
		}
		short := existing.shortID
		request.IGThreadIGID = &short
	} else {
		request.RecipientIGIDs = []string{params.PersonID}
	}
	response, err := client.SendMessage(httpclient.SendOnce(ctx), request)
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	if response == nil {
		// Instagram answered with nothing recognizable. The message may or may not have been sent, so this is
		// reported as uncertain and never retried here.
		return nil, "send_uncertain"
	}
	sent := response.GetMessage()
	a.chats.stale()
	started := &uiStartedChat{MessageID: sent.MessageID, At: sent.TimestampMS.UnixMilli(), Chat: existing}
	// A conversation that did not exist before does now, and Instagram's send answer names no thread, so it is
	// looked up. Everything past this point is only about drawing it: the message has already gone, and no failure
	// here may say otherwise.
	if existing == nil {
		// Only a send Instagram confirmed with a message id can show which thread it went into.
		var first *firstMessage
		if sent.MessageID != "" {
			first = &firstMessage{id: sent.MessageID, offlineID: request.OfflineThreadingID, self: client.GetOwnFBID()}
		}
		if chat, code := a.resolveDirect(ctx, client, person, params.PersonID, first); code == "" {
			started.Chat = chat
		}
	}
	return started, ""
}

// The conversation this account shares with one person, mapped and stored. Shared by opening a chat and by the
// lookup that follows a first message, so both agree about what a conversation with somebody is.
// first is this account's first message to the person, when the lookup follows one: a thread holding it is the thread
// that message made or went into, which files it in this account's inbox.
func (a *readOnlyAdapter) resolveDirect(ctx context.Context, client directoryClient, person int64, personID string, first *firstMessage) (*uiChat, string) {
	ids, err := client.FetchThreadID(ctx, person)
	// No thread for the pair is an answer, not a failure.
	a.requests.note(ctx, &a.requests.directory, err != nil && !errors.Is(err, instameow.ErrThreadNotFound))
	if err != nil {
		if errors.Is(err, instameow.ErrThreadNotFound) {
			return nil, "chat_not_started"
		}
		return nil, failureCode(err)
	}
	if ids == nil || ids.ShortID == "" {
		return nil, "invalid_response"
	}
	response, err := client.GetThread(ctx, slidetypes.MakeGetThreadInfoRequest(ids.ShortID))
	a.requests.note(ctx, &a.requests.threads, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	if response == nil || response.ThreadInfo.AsIGDirectThread == nil {
		return nil, "invalid_response"
	}
	thread := response.ThreadInfo.AsIGDirectThread
	if thread.ID != ids.ShortID {
		return nil, "invalid_response"
	}
	chat := mapThread(thread, client.GetOwnFBID(), "", a.media)
	// The route lookup is addressed by one person, so the thread it answers with has to be that person's direct
	// conversation. A group, or a thread this person is not in, means the lookup and the answer disagree about who
	// is being written to, and the one thing this helper never does is address a guess.
	if chat.Group || !hasParticipant(chat, personID) {
		return nil, "invalid_response"
	}
	known, found := a.chats.get(chat.ID)
	detailFolder(chat, known, found)
	// A thread that appeared between the two lookups without this message in it (a request arriving meanwhile) is
	// not this send's, and keeps the unknown standing that refuses a reply.
	if chat.Folder == "" && first.in(thread) {
		chat.Folder = "inbox"
	}
	a.chats.put(chat)
	return chat, ""
}

// This account's first message to somebody, as Instagram confirmed it, the threading id it was sent with, and the
// account that sent it.
type firstMessage struct {
	id, offlineID string
	self          int64
}

// Whether the thread's messages, as the detail read carries them, include this one. The threading id is chosen by
// whoever sends, so it only identifies this message when this account is the sender.
func (m *firstMessage) in(thread *slidetypes.ThreadInfo) bool {
	if m == nil || m.id == "" || thread.SlideMessages == nil {
		return false
	}
	for _, edge := range thread.SlideMessages.Edges {
		n := edge.Node
		if n == nil {
			continue
		}
		if n.MessageID == m.id || (m.offlineID != "" && m.self > 0 && n.SenderFBID == m.self && n.OfflineThreadingID == m.offlineID) {
			return true
		}
	}
	return false
}

// What creating a group produced. The thread id is reported separately from the conversation for the same reason a
// first message's id is: the group exists the moment Instagram answers, and a conversation that could not be drawn
// afterwards must not be reported as a group that was never made.
type uiCreatedGroup struct {
	Chat     *uiChat `json:"chat,omitempty"`
	ThreadID string  `json:"threadId"`
	// Whether the name asked for was actually applied. Instagram names a group in a second call, so it can refuse
	// the name while keeping the group, and the app should say which of those happened.
	Named bool `json:"named"`
}

// Instagram's own ceiling is not documented anywhere this project can check, so this is the app's guardrail against
// a runaway caller rather than a claim about Instagram. Instagram refuses what it will not accept, by name.
const maxGroupMembers = 50

// The longest group name to forward. Instagram decides what it will actually take.
const maxGroupTitle = 256

// Creating a group conversation. Unlike a first message to one person, this needs no guessing: the recipients field
// is named for the messaging FBIDs outright (`recipient_eimu_fbids`), which is the id search already returns, and
// Instagram answers with the new thread's own id rather than leaving it to be looked up afterwards.
//
// Naming is a second call, because Instagram has no name field on the create. A group that is made and then cannot
// be named is still a group, so the name failing is reported rather than treated as the whole thing failing.
func (a *readOnlyAdapter) uiCreateGroup(ctx context.Context, raw json.RawMessage) (any, string) {
	client, code := a.directoryClientFor()
	if code != "" {
		return nil, code
	}
	var params struct {
		PersonIDs []string `json:"personIds"`
		Title     string   `json:"title"`
	}
	if json.Unmarshal(raw, &params) != nil || len(params.Title) > maxGroupTitle {
		return nil, "invalid_params"
	}
	// A group is more than two people talking, and every one of them has to be somebody Instagram can address.
	if len(params.PersonIDs) < 2 || len(params.PersonIDs) > maxGroupMembers {
		return nil, "invalid_params"
	}
	self := client.GetOwnFBID()
	seen := make(map[string]bool, len(params.PersonIDs))
	for _, id := range params.PersonIDs {
		person, err := strconv.ParseInt(id, 10, 64)
		if err != nil || person <= 0 || seen[id] {
			return nil, "invalid_params"
		}
		if person == self {
			// This account is in every group it creates; naming it as a member as well is a different group than
			// the one asked for.
			return nil, "chat_is_self"
		}
		seen[id] = true
	}
	// Never twice: a second attempt after a lost answer would make a second group (httpclient.SendOnce).
	response, err := client.CreateGroup(httpclient.SendOnce(ctx), &slidetypes.CreateGroupRequest{
		RecipientFBIDs:     params.PersonIDs,
		OfflineThreadingID: strconv.FormatInt(methods.GenerateEpochID(), 10),
	})
	a.requests.note(ctx, &a.requests.mutations, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	// Instagram answered with nothing recognizable. The group may or may not exist, so this is reported as uncertain
	// and never retried here: retrying would make a second group.
	if response == nil || response.Data.ID == "" {
		return nil, "group_uncertain"
	}
	a.chats.stale()
	created := &uiCreatedGroup{ThreadID: response.Data.ID}
	// Everything past this point is about a group that already exists. No failure here may say it does not.
	if params.Title != "" {
		created.Named = client.EditGroupTitle(httpclient.SendOnce(ctx), response.Data.ID, params.Title) == nil
		a.requests.note(ctx, &a.requests.mutations, !created.Named)
	}
	if chat, code := a.resolveGroup(ctx, client, response.Data.ID); code == "" {
		created.Chat = chat
	}
	return created, ""
}

// The group just made, read back under its own id so it arrives the same shape as any other conversation.
func (a *readOnlyAdapter) resolveGroup(ctx context.Context, client directoryClient, threadID string) (*uiChat, string) {
	response, err := client.GetThread(ctx, slidetypes.MakeGetThreadInfoRequest(threadID))
	a.requests.note(ctx, &a.requests.threads, err != nil)
	if err != nil {
		return nil, failureCode(err)
	}
	if response == nil || response.ThreadInfo.AsIGDirectThread == nil {
		return nil, "invalid_response"
	}
	thread := response.ThreadInfo.AsIGDirectThread
	if thread.ID != threadID {
		return nil, "invalid_response"
	}
	chat := mapThread(thread, client.GetOwnFBID(), "", a.media)
	// This account made the group, which files it in this account's inbox.
	if chat.Folder == "" {
		chat.Folder = "inbox"
	}
	a.chats.put(chat)
	return chat, ""
}

func hasParticipant(chat *uiChat, id string) bool {
	for _, person := range chat.Participants {
		if !person.Self && person.ID == id {
			return true
		}
	}
	return false
}
