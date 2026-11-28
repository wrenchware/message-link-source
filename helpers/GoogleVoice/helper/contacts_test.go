package main

import (
	"context"
	"errors"
	"testing"
	"time"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

func person(name string, primary bool, numbers ...string) *gvproto.PersonWrapper {
	wrapper := &gvproto.PersonWrapper{Person: &gvproto.Person{}}
	for _, number := range numbers {
		wrapper.Person.ContactMethods = append(wrapper.Person.ContactMethods, &gvproto.ContactMethod{
			DisplayInfo: &gvproto.ContactDisplayInfo{Name: &gvproto.ContactDisplayInfo_Name{Value: name}, Primary: primary},
			Phone:       &gvproto.ContactPhone{CanonicalValue: number},
		})
	}
	return wrapper
}

func TestContactBookNamesFromTheListThenLooksUpTheRestOnce(t *testing.T) {
	lists, lookups := 0, [][]string{}
	book := &contactBook{
		autocomplete: func(context.Context, *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
			lists++
			return &gvproto.RespAutocompleteContacts{Results: []*gvproto.PersonWrapper{person("Listed Person", true, "+15550001")}}, nil
		},
		lookup: func(_ context.Context, _ *libgv.Client, phones []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
			lookups = append(lookups, phones)
			return map[string]*gvproto.RespLookupContacts_Match{
				"+15550002": {Autocompletion: person("Looked Up", false, "+15550002")},
				"+15550003": {FailureType: gvproto.RespLookupContacts_Match_PERMANENT},
			}, nil
		},
	}
	contacts, _ := book.infoFor(context.Background(), nil, []string{"+15550001", "+15550002", "+15550003", "898287"})
	if contacts["+15550001"].Name != "Listed Person" || contacts["+15550002"].Name != "Looked Up" || len(contacts) != 2 {
		t.Fatalf("unexpected contacts: %v", contacts)
	}
	if lists != 1 || len(lookups) != 1 || len(lookups[0]) != 3 {
		t.Fatalf("expected the list once and one lookup of the three unlisted numbers: %d %v", lists, lookups)
	}
	again, _ := book.infoFor(context.Background(), nil, []string{"+15550001", "+15550002", "+15550003", "898287"})
	if len(again) != 2 || lists != 1 || len(lookups) != 1 {
		t.Fatalf("numbers already answered must not be asked again: %v %d %v", again, lists, lookups)
	}
}

func TestContactBookFailuresLeaveNumbersUnnamedAndAreRetried(t *testing.T) {
	fail := true
	lookups := 0
	book := &contactBook{
		autocomplete: func(context.Context, *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
			return nil, errors.New("unavailable")
		},
		lookup: func(_ context.Context, _ *libgv.Client, phones []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
			lookups++
			if fail {
				return nil, errors.New("unavailable")
			}
			return map[string]*gvproto.RespLookupContacts_Match{"+15550004": {Autocompletion: person("Later", true, "+15550004")}}, nil
		},
	}
	clock := &voiceClock{at: time.Date(2026, 11, 18, 12, 0, 0, 0, time.UTC)}
	book.now = clock.now
	if contacts, _ := book.infoFor(context.Background(), nil, []string{"+15550004"}); len(contacts) != 0 {
		t.Fatalf("a failed lookup names nobody: %v", contacts)
	}
	fail = false
	// Not at once: a list a moment later would only ask a failing service again.
	if contacts, _ := book.infoFor(context.Background(), nil, []string{"+15550004"}); len(contacts) != 0 || lookups != 1 {
		t.Fatalf("a failed lookup was asked again at once: %v %d", contacts, lookups)
	}
	clock.advance(contactsRetry)
	if contacts, _ := book.infoFor(context.Background(), nil, []string{"+15550004"}); contacts["+15550004"].Name != "Later" || lookups != 2 {
		t.Fatalf("a failed lookup must be tried again: %v %d", contacts, lookups)
	}
}

func TestThreadsAreNamedFromContactsOnlyWhereTheyGiveANumber(t *testing.T) {
	thread := &gvproto.Thread{ID: "g", PhoneNumbers: []string{"+15550002", "+15550003"},
		Contacts: []*gvproto.Contact{{Name: "Named In Thread", PhoneNumber: "+15550002"}}}
	if numbers := unnamedNumbers([]*gvproto.Thread{thread, thread}); len(numbers) != 1 || numbers[0] != "+15550003" {
		t.Fatalf("only numbers the thread does not name are looked up, once: %v", numbers)
	}
	chat := convertThreadNamed(thread, map[string]contactInfo{"+15550002": {Name: "Contact Name"}, "+15550003": {Name: "From Contacts"}})
	if chat.Title != "Named In Thread, From Contacts" || chat.Participants[1].Name != "From Contacts" {
		t.Fatalf("the thread's own name wins, contacts fill the rest: %+v", chat)
	}
	numbered := &gvproto.Thread{ID: "n", Contacts: []*gvproto.Contact{{Name: "+1 (555) 000-0005", PhoneNumber: "+15550005"}, {Name: "Car Port Auto", PhoneNumber: "+15550006"}}}
	if numbers := unnamedNumbers([]*gvproto.Thread{numbered}); len(numbers) != 1 || numbers[0] != "+15550005" {
		t.Fatalf("a name that is only a number is no name: %v", numbers)
	}
	if chat := convertThreadNamed(numbered, map[string]contactInfo{"+15550005": {Name: "From Contacts"}, "+15550006": {Name: "Other"}}); chat.Title != "From Contacts, Car Port Auto" {
		t.Fatalf("contacts replace a number-only name and nothing else: %q", chat.Title)
	}
	if contactName(person("", true, "+1").GetPerson()) != "" {
		t.Fatal("a person without a name has no name")
	}
	if numbers := participantNumbers([]*gvproto.Thread{thread, thread}); len(numbers) != 2 || numbers[0] != "+15550002" || numbers[1] != "+15550003" {
		t.Fatalf("every participant is requested for avatar enrichment, once: %v", numbers)
	}
	withAvatar := convertThreadNamed(thread, map[string]contactInfo{
		"+15550002": {Name: "Different Contact Name", Avatar: "avatar:synthetic"},
	})
	if withAvatar.Participants[0].Name != "Named In Thread" || withAvatar.Participants[0].Avatar != "avatar:synthetic" {
		t.Fatalf("thread name should win while its contact avatar is retained: %+v", withAvatar.Participants[0])
	}
}

func TestContactBookKeepsOnlyOpaqueNonMonogramAvatarHandles(t *testing.T) {
	book := &contactBook{}
	person := &gvproto.Person{ContactMethods: []*gvproto.ContactMethod{{
		DisplayInfo: &gvproto.ContactDisplayInfo{
			Name:  &gvproto.ContactDisplayInfo_Name{Value: "Synthetic"},
			Photo: &gvproto.ContactDisplayInfo_Photo{URL: "https://lh3.googleusercontent.com/synthetic", Type: gvproto.ContactDisplayInfo_Photo_USER_SPECIFIED},
		},
		Phone: &gvproto.ContactPhone{CanonicalValue: "+15550005"},
	}}}
	info := book.process(person)
	if info.Name != "Synthetic" || len(info.Avatar) != len("avatar:")+64 || book.avatarURL(info.Avatar) == "" {
		t.Fatalf("expected an opaque avatar handle: %+v", info)
	}
	monogram := book.process(&gvproto.Person{ContactMethods: []*gvproto.ContactMethod{{
		DisplayInfo: &gvproto.ContactDisplayInfo{Photo: &gvproto.ContactDisplayInfo_Photo{URL: "https://lh3.googleusercontent.com/ignored", Type: gvproto.ContactDisplayInfo_Photo_MONOGRAM}},
	}}})
	if monogram.Avatar != "" {
		t.Fatalf("monogram must keep the native initials fallback: %+v", monogram)
	}
}
