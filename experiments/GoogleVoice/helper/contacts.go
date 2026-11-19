package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Contacts are read again after this long, so a name added in Google Contacts reaches the app without a restart.
const contactsRefresh = 6 * time.Hour

// Numbers looked up in one request.
const lookupBatch = 50

// How long one contacts request may take, retries included. libgv retries a 5xx for about two minutes, and every
// conversation list and avatar used to wait that long behind the book.
const contactsTimeout = 30 * time.Second

// A whole-list read or a lookup that failed is not asked again for this long, so an outage of Google's contacts service
// costs a request every few minutes rather than one per conversation list.
const contactsRetry = 5 * time.Minute

// Names from the account's Google contacts, by E.164 number, as upstream's bridge reads them: the whole list once,
// then each number that is not in it. A number with no contact is remembered as such, so it is not asked again.
// Names and numbers are shown in the app's own window and never written to a log. Nothing waits on Google with mu
// held: requests run outside it and their answers are laid in under it.
type contactBook struct {
	mu       sync.Mutex
	contacts map[string]contactInfo
	// Avatar URLs remain in the helper. The app receives only a SHA-256 handle, which it can safely keep in its
	// encrypted media cache and ask back for after the conversation list has repopulated this book.
	avatarURLs map[string]string
	loadedAt   time.Time
	// The whole-list read in flight, closed when it ends; one read serves every caller that arrives meanwhile.
	listing chan struct{}
	// Counts from the last whole-list read, for the next caller's diagnostic log.
	listStats              map[string]any
	lookingUp              bool
	listFailed, lookFailed time.Time
	// Injectable for tests.
	now          func() time.Time
	autocomplete func(ctx context.Context, client *libgv.Client) (*gvproto.RespAutocompleteContacts, error)
	lookup       func(ctx context.Context, client *libgv.Client, phones []string) (map[string]*gvproto.RespLookupContacts_Match, error)
}

func (book *contactBook) clock() time.Time {
	if book.now != nil {
		return book.now()
	}
	return time.Now()
}

func (book *contactBook) backingOff(failed time.Time) bool {
	return !failed.IsZero() && book.clock().Sub(failed) < contactsRetry
}

type contactInfo struct {
	Name   string
	Avatar string
}

// The contact names for these numbers that are known. A failed request leaves the numbers unnamed rather than
// failing the conversation list, and is tried again on a later list.
func (book *contactBook) infoFor(ctx context.Context, client *libgv.Client, numbers []string) (map[string]contactInfo, map[string]any) {
	book.readList(ctx, client)
	book.mu.Lock()
	// Counts for the diagnostic log, only when a request was made: never names or numbers.
	stats := book.listStats
	book.listStats = nil
	if book.contacts == nil {
		book.contacts = map[string]contactInfo{}
	}
	var missing []string
	// One lookup at a time: a list that arrives during one leaves its numbers for a later list rather than asking twice.
	if !book.lookingUp && !book.backingOff(book.lookFailed) {
		for _, number := range numbers {
			if _, known := book.contacts[number]; !known && number != "" && len(missing) < lookupBatch {
				missing = append(missing, number)
			}
		}
	}
	lookup := book.lookup
	book.lookingUp = len(missing) > 0
	book.mu.Unlock()
	if len(missing) > 0 {
		if lookup == nil {
			lookup = func(ctx context.Context, client *libgv.Client, phones []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
				return client.LookupContact(ctx, phones...)
			}
		}
		if stats == nil {
			stats = map[string]any{}
		}
		stats["lookedUp"] = len(missing)
		asking, cancel := context.WithTimeout(ctx, contactsTimeout)
		matches, err := lookup(asking, client, missing)
		cancel()
		book.mu.Lock()
		book.lookingUp = false
		if err == nil {
			book.lookFailed = time.Time{}
			stats["matched"] = len(matches)
			for _, number := range missing {
				// A whole-list read that finished meanwhile named it.
				if held := book.contacts[number]; held.Name != "" {
					continue
				}
				match, found := matches[number]
				switch {
				case !found:
					book.contacts[number] = contactInfo{}
				case match.GetFailureType() == gvproto.RespLookupContacts_Match_NO_FAILURE:
					book.contacts[number] = book.process(match.GetAutocompletion().GetPerson())
				case match.GetFailureType() == gvproto.RespLookupContacts_Match_PERMANENT:
					book.contacts[number] = contactInfo{}
				}
			}
		} else {
			// A list the app gave up on is not Google failing.
			if ctx.Err() == nil {
				book.lookFailed = book.clock()
			}
			stats["lookup"], stats["status"] = "failed", statusOf(err)
		}
		book.mu.Unlock()
	}
	book.mu.Lock()
	defer book.mu.Unlock()
	named := map[string]contactInfo{}
	for _, number := range numbers {
		if info := book.contacts[number]; info.Name != "" {
			named[number] = info
		}
	}
	if stats != nil {
		stats["unnamed"], stats["named"] = len(numbers), len(named)
	}
	return named, stats
}

// The whole list, read when the book has none or it is six hours old. The read is shared and is not the caller's: a
// list the app gives up on does not end it for the others. Only a book that has never been read waits for it; one
// already read answers from what it holds while the read goes on.
func (book *contactBook) readList(ctx context.Context, client *libgv.Client) {
	book.mu.Lock()
	first := book.loadedAt.IsZero()
	due := first || book.clock().Sub(book.loadedAt) > contactsRefresh
	if !due || (book.listing == nil && book.backingOff(book.listFailed)) {
		book.mu.Unlock()
		return
	}
	done := book.listing
	if done == nil {
		done = make(chan struct{})
		book.listing = done
		autocomplete := book.autocomplete
		if autocomplete == nil {
			autocomplete = func(ctx context.Context, client *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
				return client.AutocompleteContacts(ctx, "")
			}
		}
		go book.loadList(context.WithoutCancel(ctx), client, autocomplete, done)
	}
	book.mu.Unlock()
	if first {
		select {
		case <-done:
		case <-ctx.Done():
		}
	}
}

func (book *contactBook) loadList(ctx context.Context, client *libgv.Client, autocomplete func(context.Context, *libgv.Client) (*gvproto.RespAutocompleteContacts, error), done chan struct{}) {
	asking, cancel := context.WithTimeout(ctx, contactsTimeout)
	resp, err := autocomplete(asking, client)
	cancel()
	book.mu.Lock()
	defer book.mu.Unlock()
	defer close(done)
	book.listing = nil
	if book.listStats == nil {
		book.listStats = map[string]any{}
	}
	if err != nil {
		book.listFailed = book.clock()
		book.listStats["contactList"], book.listStats["status"] = "failed", statusOf(err)
		return
	}
	contacts := map[string]contactInfo{}
	for _, result := range resp.GetResults() {
		info := book.process(result.GetPerson())
		for _, method := range result.GetPerson().GetContactMethods() {
			if number := method.GetPhone().GetCanonicalValue(); number != "" && info.Name != "" {
				if _, known := contacts[number]; !known || method.GetDisplayInfo().GetPrimary() {
					contacts[number] = info
				}
			}
		}
	}
	book.contacts, book.loadedAt, book.listFailed = contacts, book.clock(), time.Time{}
	book.listStats["contactList"], book.listStats["listedNumbers"] = len(resp.GetResults()), len(contacts)
}

// process returns what this app needs from a Google Contact. A contact-provided photo is represented by an opaque
// stable handle, not its URL. Google Voice uses MONOGRAM when there is no person photo; preserve the native initials
// avatar in that case.
func (book *contactBook) process(person *gvproto.Person) contactInfo {
	info := contactInfo{Name: contactName(person)}
	for _, method := range person.GetContactMethods() {
		photo := method.GetDisplayInfo().GetPhoto()
		if photo.GetType() == gvproto.ContactDisplayInfo_Photo_MONOGRAM || photo.GetURL() == "" || (info.Avatar != "" && !method.GetDisplayInfo().GetPrimary()) {
			continue
		}
		if book.avatarURLs == nil {
			book.avatarURLs = map[string]string{}
		}
		sum := sha256.Sum256([]byte(photo.GetURL()))
		info.Avatar = "avatar:" + hex.EncodeToString(sum[:])
		book.avatarURLs[info.Avatar] = photo.GetURL()
	}
	return info
}

// avatarURL resolves a handle the helper itself issued. It never exposes the original URL across the pipe.
func (book *contactBook) avatarURL(id string) string {
	book.mu.Lock()
	defer book.mu.Unlock()
	return book.avatarURLs[id]
}

// A person's display name: the primary contact method's name, or the first one given.
func contactName(person *gvproto.Person) string {
	name := ""
	for _, method := range person.GetContactMethods() {
		if value := method.GetDisplayInfo().GetName().GetValue(); value != "" && (name == "" || method.GetDisplayInfo().GetPrimary()) {
			name = value
		}
	}
	return name
}

// The numbers in these threads that the threads themselves do not name.
func unnamedNumbers(threads []*gvproto.Thread) []string {
	seen := map[string]bool{}
	var numbers []string
	add := func(number string) {
		if number != "" && !seen[number] {
			seen[number] = true
			numbers = append(numbers, number)
		}
	}
	for _, thread := range threads {
		named := map[string]bool{}
		for _, contact := range thread.GetContacts() {
			if !numberOnly(contact.GetName()) {
				named[contact.GetPhoneNumber()] = true
			} else {
				add(contact.GetPhoneNumber())
			}
		}
		for _, number := range thread.GetPhoneNumbers() {
			if !named[number] {
				add(number)
			}
		}
	}
	return numbers
}

// Every participant is eligible for a contact photo, even when Voice supplied a display name on the thread itself.
// `convertThreadNamed` still gives that thread name precedence; contact data only fills a number-only name.
func participantNumbers(threads []*gvproto.Thread) []string {
	seen := map[string]bool{}
	numbers := make([]string, 0)
	add := func(number string) {
		if number != "" && !seen[number] {
			seen[number] = true
			numbers = append(numbers, number)
		}
	}
	for _, thread := range threads {
		for _, contact := range thread.GetContacts() {
			add(contact.GetPhoneNumber())
		}
		for _, number := range thread.GetPhoneNumbers() {
			add(number)
		}
	}
	return numbers
}

// Voice gives a caller with no contact their number as a name. A name without a single letter is only a number.
func numberOnly(name string) bool {
	return !strings.ContainsFunc(name, unicode.IsLetter)
}

type voiceContact struct {
	Number string `json:"number"`
	Name   string `json:"name"`
	Avatar string `json:"avatar,omitempty"`
}

// The account's named contacts, for New chat to search. Read from the same book the conversation list uses.
func (book *contactBook) all(ctx context.Context, client *libgv.Client) []voiceContact {
	_, _ = book.infoFor(ctx, client, nil)
	book.mu.Lock()
	defer book.mu.Unlock()
	contacts := make([]voiceContact, 0, len(book.contacts))
	for number, info := range book.contacts {
		if info.Name != "" {
			contacts = append(contacts, voiceContact{Number: number, Name: info.Name, Avatar: info.Avatar})
		}
	}
	slices.SortFunc(contacts, func(a, b voiceContact) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
	return contacts
}
