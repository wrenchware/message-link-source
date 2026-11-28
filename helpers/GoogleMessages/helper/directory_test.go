package main

import "testing"

func TestDirectRecipient(t *testing.T) {
	for _, input := range []string{"", "12", "123 ext 4", "123;456", "a5551234567", "123+456", "++123", "1234567890123456"} {
		if directRequest(input) != nil {
			t.Fatalf("accepted ambiguous recipient %q", input)
		}
	}
	for input, want := range map[string]string{"+1 (202) 555-0100": "+12025550100", "202.555.0100": "2025550100", "611": "611"} {
		req := directRequest(input)
		if req == nil || len(req.Numbers) != 1 || req.Numbers[0].Number != want || req.Numbers[0].Number2 != want || req.CreateRCSGroup != nil {
			t.Fatalf("wrong direct payload for %q", input)
		}
	}
}
