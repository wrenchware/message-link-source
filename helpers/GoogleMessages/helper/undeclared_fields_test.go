package main

import (
	"strings"
	"testing"

	"google.golang.org/protobuf/encoding/protowire"
)

func hexOf(text string) string {
	const digits = "0123456789abcdef"
	var out strings.Builder
	for _, b := range []byte(text) {
		out.WriteByte(digits[b>>4])
		out.WriteByte(digits[b&15])
	}
	return out.String()
}

// A byte field that is itself an encoded message shows what it holds, as the conversations' field 39 does; text,
// including a string of digits, is never taken apart; and a large number is "L", so timestamps do not make every
// summary unique.
func TestUndeclaredFieldsOpenNestedFieldsButNeverText(t *testing.T) {
	inner := protowire.AppendTag(nil, 1, protowire.VarintType)
	inner = protowire.AppendVarint(inner, 1)
	inner = protowire.AppendTag(inner, 2, protowire.VarintType)
	inner = protowire.AppendVarint(inner, 0)
	inner = protowire.AppendTag(inner, 3, protowire.BytesType)
	inner = protowire.AppendString(inner, "Synthetic")
	wire := protowire.AppendTag(nil, 39, protowire.BytesType)
	wire = protowire.AppendBytes(wire, inner)
	wire = protowire.AppendTag(wire, 40, protowire.BytesType)
	wire = protowire.AppendString(wire, "0555123")
	wire = protowire.AppendTag(wire, 41, protowire.VarintType)
	wire = protowire.AppendVarint(wire, 1727000000000000)
	wire = protowire.AppendTag(wire, 42, protowire.BytesType)
	wire = protowire.AppendBytes(wire, []byte{0x08, 0x00, 0x10, 0x00})
	got := strings.Join(undeclaredFields(wire), " ")
	want := "f39=b15{1=1;2=0;3=b9} f40=b7 f41=L f42=b4:08001000{1=0;2=0}"
	if got != want {
		t.Fatalf("fields %q, want %q", got, want)
	}
	if strings.Contains(got, "555") || strings.Contains(got, hexOf("Synthetic")) {
		t.Fatal("text reached the summary")
	}
}

// Damaged unknown bytes say so rather than guessing at the rest.
func TestUndeclaredFieldsOfDamagedBytes(t *testing.T) {
	damaged := protowire.AppendTag(nil, 30, protowire.VarintType)
	damaged = protowire.AppendVarint(damaged, 1)
	damaged = protowire.AppendTag(damaged, 37, protowire.BytesType)
	damaged = append(damaged, 9, 1) // claims nine bytes, has one
	if got := undeclaredFields(damaged); strings.Join(got, " ") != "f30=1 unparsed" {
		t.Fatalf("damaged fields %q", got)
	}
}
