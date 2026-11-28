package main

import "testing"

func TestMetaAIRepliesNeverBecomeBlankHistoryRows(t *testing.T) {
	for _, encrypted := range []bool{false, true} {
		s := newStore()
		s.setSelf(100)
		s.contacts[metaAIMessengerID] = "Meta AI"
		s.contacts[200] = "Meta AI" // An ordinary contact with the same name is not a bot.
		s.messages[7] = map[string]*message{
			"blank":    {id: "blank", sender: metaAIMessengerID, encrypted: encrypted, timestamp: 1000},
			"spaces":   {id: "spaces", sender: metaAIMessengerID, text: " \n\t", encrypted: encrypted, timestamp: 2000},
			"text":     {id: "text", sender: metaAIMessengerID, text: "Synthetic AI answer", encrypted: encrypted, timestamp: 3000},
			"ordinary": {id: "ordinary", sender: 200, timestamp: 4000},
			"deleted":  {id: "deleted", sender: metaAIMessengerID, unsent: true, timestamp: 5000},
		}
		page := s.history(7, 0)
		if len(page) != 4 || page[0].Text != metaAIUnavailableReply || page[1].Text != metaAIUnavailableReply ||
			page[2].Text != "Synthetic AI answer" || !page[3].Deleted || page[3].Text != "" {
			t.Fatal("known AI replies need a fallback without changing actual text, deleted rows or ordinary empty stubs")
		}
		if s.messages[7]["blank"].text != "" {
			t.Fatal("fallback must be a projection, not replace archived provider content")
		}
		if earlier := s.historyBefore(7, 2500, 1); len(earlier) != 1 || earlier[0].ID != "spaces" {
			t.Fatal("AI fallback rows must retain pagination and identity")
		}
		s.attached["blank"] = []*attachment{{id: "photo", mime: "image/jpeg"}}
		if media := s.viewOf(7, s.messages[7]["blank"]); media.Text != "" || len(media.Media) != 1 {
			t.Fatal("readable media should not acquire a fallback caption")
		}
		s.setSelf(metaAIMessengerID)
		if own := s.viewOf(7, s.messages[7]["spaces"]); own.Text != " \n\t" || !own.Outgoing {
			t.Fatal("outgoing messages must not receive the AI fallback")
		}
	}
}
