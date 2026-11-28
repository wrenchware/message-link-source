package main

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
	"google.golang.org/protobuf/encoding/protowire"
)

func phoneBook(n int) []*gmproto.Contact {
	book := make([]*gmproto.Contact, n)
	for i := range book {
		number := fmt.Sprintf("+1202555%04d", i)
		book[i] = &gmproto.Contact{ContactID: fmt.Sprint(i), Name: fmt.Sprint("P", i), Number: &gmproto.ContactNumber{Number: number}}
	}
	return book
}

// A phone that answers by its own rule, counting requests.
func fakePhone(book []*gmproto.Contact, answer func(i1, i2, i3 int32) (from, count int, err error)) (contactLister, *int) {
	calls := 0
	return func(_ context.Context, i1, i2, i3 int32) (*gmproto.ListContactsResponse, error) {
		calls++
		from, count, err := answer(i1, i2, i3)
		if err != nil {
			return nil, err
		}
		from, end := min(from, len(book)), min(from+count, len(book))
		return &gmproto.ListContactsResponse{Contacts: book[from:end]}, nil
	}, &calls
}

func TestContactsSmallBookIsOneUpstreamRequest(t *testing.T) {
	var asked [][3]int32
	list, calls := fakePhone(phoneBook(30), func(i1, i2, i3 int32) (int, int, error) {
		asked = append(asked, [3]int32{i1, i2, i3})
		return 0, int(i3), nil
	})
	all, tries, err := readAllContacts(context.Background(), list)
	if err != nil || len(all) != 30 || *calls != 1 || len(tries) != 1 || asked[0] != [3]int32{1, 350, 50} {
		t.Fatalf("small book: %d contacts, %d calls, %v", len(all), *calls, asked)
	}
}

func TestContactsBiggerPageWhenHonoured(t *testing.T) {
	list, calls := fakePhone(phoneBook(410), func(_, _, i3 int32) (int, int, error) { return 0, int(i3), nil })
	all, tries, err := readAllContacts(context.Background(), list)
	if err != nil || len(all) != 410 || *calls != 3 || tries[2].Fresh != 60 {
		t.Fatalf("page size: %d contacts, %d calls, %+v", len(all), *calls, tries)
	}
}

func TestContactsPagesWhenOnlyThePageNumberIsHonoured(t *testing.T) {
	list, calls := fakePhone(phoneBook(410), func(i1, _, _ int32) (int, int, error) { return int(i1-1) * 50, 50, nil })
	all, _, err := readAllContacts(context.Background(), list)
	// Upstream's page, one bigger page that changes nothing, then pages 2 to 9 (the ninth is short).
	if err != nil || len(all) != 410 || *calls != 10 {
		t.Fatalf("pages: %d contacts, %d calls", len(all), *calls)
	}
}

func TestContactsStopWhenThePhoneIgnoresBoth(t *testing.T) {
	list, calls := fakePhone(phoneBook(410), func(_, _, _ int32) (int, int, error) { return 0, 50, nil })
	all, tries, err := readAllContacts(context.Background(), list)
	if err != nil || len(all) != 50 || *calls != 3 || tries[2].Fresh != 0 {
		t.Fatalf("ignored: %d contacts, %d calls, %+v", len(all), *calls, tries)
	}
}

func TestContactsNeverExceedTheRequestLimit(t *testing.T) {
	// Every page full and new, forever: the pages stop at the limit.
	list, calls := fakePhone(phoneBook(5000), func(i1, _, _ int32) (int, int, error) { return int(i1-1) * 50, 50, nil })
	_, _, err := readAllContacts(context.Background(), list)
	if err != nil || *calls != contactTryLimit {
		t.Fatalf("limit: %d calls", *calls)
	}
}

func TestContactsFailures(t *testing.T) {
	list, _ := fakePhone(phoneBook(410), func(_, _, _ int32) (int, int, error) { return 0, 0, errors.New("down") })
	if all, _, err := readAllContacts(context.Background(), list); err == nil || all != nil {
		t.Fatal("a failed first request must fail the read, as before")
	}
	// A refused bigger request keeps what upstream's request brought.
	list, _ = fakePhone(phoneBook(410), func(i1, _, i3 int32) (int, int, error) {
		if i1 == 1 && i3 == 50 {
			return 0, 50, nil
		}
		return 0, 0, errors.New("refused")
	})
	all, tries, err := readAllContacts(context.Background(), list)
	if err != nil || len(all) != 50 || !tries[1].Failed {
		t.Fatalf("refused: %d contacts, %+v", len(all), tries)
	}
}

func TestContactsReportUndeclaredResponseFields(t *testing.T) {
	resp := &gmproto.ListContactsResponse{}
	var raw []byte
	raw = protowire.AppendTag(raw, 3, protowire.BytesType)
	raw = protowire.AppendBytes(raw, []byte("cursor"))
	raw = protowire.AppendTag(raw, 4, protowire.VarintType)
	raw = protowire.AppendVarint(raw, 1)
	resp.ProtoReflect().SetUnknown(raw)
	if got := unknownFields(resp); got != "3:7,4:1" {
		t.Fatalf("unknown fields %q", got)
	}
	if got := unknownFields(&gmproto.ListContactsResponse{}); got != "" {
		t.Fatalf("no unknown fields, got %q", got)
	}
}
