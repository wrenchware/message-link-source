package main

import "testing"

func TestGroupRequest(t *testing.T) {
	req := groupRequest([]string{"(202) 555-0100", "+1 202 555 0101"}, "  Weekend  ")
	if req == nil || len(req.Numbers) != 2 || req.Numbers[0].GetNumber() != "2025550100" || req.Numbers[1].GetNumber2() != "+12025550101" ||
		req.GetRCSGroupName() != "Weekend" || req.CreateRCSGroup != nil {
		t.Fatal("unexpected group request")
	}
	if req := groupRequest([]string{"2025550100", "2025550101"}, " "); req == nil || req.RCSGroupName != nil {
		t.Fatal("a blank group name must be omitted")
	}
	for _, phones := range [][]string{{"2025550100"}, {"2025550100", "202-555-0100"}, {"2025550100", "not a number"}} {
		if groupRequest(phones, "") != nil {
			t.Fatalf("invalid group accepted: %v", phones)
		}
	}
}
