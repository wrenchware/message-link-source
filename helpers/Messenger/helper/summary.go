package main

import "go.mau.fi/mautrix-meta/pkg/messagix/table"

// Counts only. The spike proves the connection works; it never reports conversation names or message text.
type summary struct {
	Threads  int `json:"threads"`
	Messages int `json:"messages"`
	Contacts int `json:"contacts"`
}

func summarize(tbl *table.LSTable) summary {
	if tbl == nil {
		return summary{}
	}
	return summary{
		Threads:  len(tbl.LSDeleteThenInsertThread),
		Messages: len(tbl.LSUpsertMessage),
		Contacts: len(tbl.LSDeleteThenInsertContact),
	}
}

func (s summary) add(other summary) summary {
	return summary{
		Threads:  s.Threads + other.Threads,
		Messages: s.Messages + other.Messages,
		Contacts: s.Contacts + other.Contacts,
	}
}
