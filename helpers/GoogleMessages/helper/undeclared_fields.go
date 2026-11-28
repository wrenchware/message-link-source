package main

// What a protobuf message carries that the pinned protocol does not declare, for diagnostics that must hold no names,
// numbers or text: undeclared byte fields give only their length, and their bytes only when there are four or fewer.
// The avatar probe logs it (avatars.go). It is also how Google's per-message encryption mark was found
// (docs/research/google-messages-encryption-mark.md).

import (
	"encoding/hex"
	"sort"
	"strconv"
	"strings"

	"google.golang.org/protobuf/encoding/protowire"
)

func bit(value bool) string {
	if value {
		return "1"
	}
	return "0"
}

// Every field the pinned protocol does not declare, by number, in order: a varint as its value (one of a million or more
// as "L": timestamps and ids, which would make every summary its own), a byte field as its length with the bytes in hex
// when there are four or fewer, and, when it starts the way an encoded message does, what it holds in braces
// (f39=b4:08001000{1=0;2=0}). Fixed-width values in hex. A field seen more than once lists each value.
func undeclaredFields(raw []byte) []string {
	values := map[protowire.Number][]string{}
	for len(raw) > 0 {
		number, value, n := consumeField(raw, 0)
		if n < 0 {
			return append(listFields(values), "unparsed")
		}
		raw = raw[n:]
		values[number] = append(values[number], value)
	}
	return listFields(values)
}

func consumeField(raw []byte, depth int) (protowire.Number, string, int) {
	number, kind, n := protowire.ConsumeTag(raw)
	if n < 0 {
		return 0, "", n
	}
	var value string
	var m int
	switch kind {
	case protowire.VarintType:
		var v uint64
		v, m = protowire.ConsumeVarint(raw[n:])
		value = varintText(v)
	case protowire.BytesType:
		var b []byte
		b, m = protowire.ConsumeBytes(raw[n:])
		value = "b" + strconv.Itoa(len(b))
		if depth == 0 && len(b) > 0 && len(b) <= 4 {
			value += ":" + hex.EncodeToString(b)
		}
		value += nestedFields(b, depth+1)
	case protowire.Fixed32Type:
		var v uint32
		v, m = protowire.ConsumeFixed32(raw[n:])
		value = "x32:" + strconv.FormatUint(uint64(v), 16)
	case protowire.Fixed64Type:
		var v uint64
		v, m = protowire.ConsumeFixed64(raw[n:])
		value = "x64:" + strconv.FormatUint(v, 16)
	default:
		m = protowire.ConsumeFieldValue(number, kind, raw[n:])
		value = "group"
	}
	if m < 0 {
		return 0, "", m
	}
	return number, value, n + m
}

func varintText(v uint64) string {
	if v >= 1_000_000 {
		return "L"
	}
	return strconv.FormatUint(v, 10)
}

// What an undeclared byte field holds, when it reads cleanly as an encoded message that opens with field 1 (0x08 or
// 0x0a). Text never starts with either, so a name or a number is never taken apart into digits here; inside, a byte
// field gives only its length (and its own fields, by the same rule), never its bytes. Three levels at most.
func nestedFields(b []byte, depth int) string {
	if depth > 3 || len(b) == 0 || b[0] != 0x08 && b[0] != 0x0a {
		return ""
	}
	parts := []string{}
	for len(b) > 0 {
		number, value, n := consumeField(b, depth)
		if n < 0 || number > 99 {
			return ""
		}
		b = b[n:]
		parts = append(parts, strconv.Itoa(int(number))+"="+value)
	}
	return "{" + strings.Join(parts, ";") + "}"
}

func listFields(values map[protowire.Number][]string) []string {
	numbers := make([]int, 0, len(values))
	for number := range values {
		numbers = append(numbers, int(number))
	}
	sort.Ints(numbers)
	fields := make([]string, 0, len(numbers))
	for _, number := range numbers {
		fields = append(fields, "f"+strconv.Itoa(number)+"="+strings.Join(values[protowire.Number(number)], ","))
	}
	return fields
}
