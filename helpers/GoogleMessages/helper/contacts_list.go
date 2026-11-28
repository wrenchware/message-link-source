package main

import (
	"context"
	"fmt"
	"strings"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"google.golang.org/protobuf/encoding/protowire"
)

// The phone's whole contact list. Upstream's request (1, 350, 50) answered exactly 50 contacts for my phone of
// about 410 (2026-10-05), so the third number looks like a page size, but nothing upstream says what any of the three
// mean. This asks as upstream does first and goes further only when that answer was full: a bigger page, then the next
// pages by the first number. Every request is a read, at most contactTryLimit of them, and the app asks at most once
// per connection and then every six hours. Each attempt is reported back as counts so the app's log says which of
// these the phone honours.

const (
	upstreamContactPage = 50
	contactPageLimit    = 20
	// Upstream's request, one bigger page that was not honoured, then pages 2 to contactPageLimit.
	contactTryLimit = 1 + 1 + (contactPageLimit - 1)
)

type contactLister func(ctx context.Context, i1, i2, i3 int32) (*gmproto.ListContactsResponse, error)

// One request and what came back: counts and the numbers of response fields the pinned schema does not name, never
// names or numbers of people.
type contactTry struct {
	I1      int32  `json:"i1"`
	I2      int32  `json:"i2"`
	I3      int32  `json:"i3"`
	Rows    int    `json:"rows"`
	Fresh   int    `json:"fresh"`
	Unknown string `json:"unknown,omitempty"`
	Failed  bool   `json:"failed,omitempty"`
}

func contactKey(c *gmproto.Contact) string {
	if id := c.GetContactID(); id != "" {
		return "c:" + id
	}
	return "n:" + c.GetNumber().GetNumber() + "|" + c.GetName()
}

// Field numbers and sizes the pinned ListContactsResponse does not declare, as "3:24,4:8". A cursor would show here.
func unknownFields(resp *gmproto.ListContactsResponse) string {
	raw := resp.ProtoReflect().GetUnknown()
	var parts []string
	for len(raw) > 0 {
		num, typ, n := protowire.ConsumeTag(raw)
		if n < 0 {
			return "unreadable"
		}
		raw = raw[n:]
		m := protowire.ConsumeFieldValue(num, typ, raw)
		if m < 0 {
			return "unreadable"
		}
		parts = append(parts, fmt.Sprintf("%d:%d", num, m))
		raw = raw[m:]
	}
	return strings.Join(parts, ",")
}

func readAllContacts(ctx context.Context, list contactLister) ([]*gmproto.Contact, []contactTry, error) {
	seen := map[string]bool{}
	var all []*gmproto.Contact
	var tries []contactTry
	var failure error
	ask := func(i1, i2, i3 int32) (int, bool) {
		resp, err := list(ctx, i1, i2, i3)
		try := contactTry{I1: i1, I2: i2, I3: i3}
		if err != nil {
			failure = err
			try.Failed = true
			tries = append(tries, try)
			return 0, false
		}
		contacts := resp.GetContacts()
		try.Rows, try.Unknown = len(contacts), unknownFields(resp)
		for _, c := range contacts {
			if key := contactKey(c); !seen[key] {
				seen[key] = true
				all = append(all, c)
				try.Fresh++
			}
		}
		tries = append(tries, try)
		return len(contacts), true
	}
	// Exactly upstream's request first, so a phone that refuses anything else still answers as it always has.
	rows, ok := ask(1, 350, upstreamContactPage)
	if !ok {
		return nil, tries, failure
	}
	if rows < upstreamContactPage {
		return all, tries, nil
	}
	// A bigger page. If the phone honours it, everybody is in this answer or the next size up.
	for _, size := range []int32{350, 2000} {
		rows, ok = ask(1, size, size)
		if !ok || rows < int(size) || ctx.Err() != nil {
			break
		}
	}
	if len(all) > upstreamContactPage || ctx.Err() != nil {
		return all, tries, nil
	}
	// The page size was not honoured. Try the first number as a page number, stopping as soon as a page brings
	// nobody new (it is being ignored too) or comes back short (the last page).
	for page := int32(2); page <= contactPageLimit && ctx.Err() == nil; page++ {
		before := len(all)
		rows, ok = ask(page, 350, upstreamContactPage)
		if !ok || len(all) == before || rows < upstreamContactPage {
			break
		}
	}
	return all, tries, nil
}
