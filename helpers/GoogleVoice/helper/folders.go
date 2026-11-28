package main

import (
	"context"
	"slices"
	"sync"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Voice keeps archived and spam conversations out of its main list; each has a folder of its own. The conversation list
// is the main list with both folders, each conversation once. A folder that cannot be listed joins on a later list.
func (a *adapter) listWithArchived(ctx context.Context) (*gvproto.RespListThreads, error) {
	return a.listWithArchivedFor(ctx, a.current())
}

func (a *adapter) listWithArchivedFor(ctx context.Context, client *libgv.Client) (*gvproto.RespListThreads, error) {
	extras := []struct {
		folder gvproto.ThreadFolder
		name   string
	}{{gvproto.ThreadFolder_ALL_ARCHIVED_THREADS, "archived"}, {gvproto.ThreadFolder_ALL_SPAM_THREADS, "spam"}}
	// The two folders are listed alongside the main list rather than after it: one after another, the first page of
	// conversations waited for three listings in turn. They are merged in the same order as before.
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	type listing struct {
		threads *gvproto.RespListThreads
		err     error
	}
	listed := make([]listing, len(extras))
	var folders sync.WaitGroup
	for i, extra := range extras {
		folders.Add(1)
		go func() {
			defer folders.Done()
			threads, err := a.listFolderFor(ctx, client, extra.folder)
			listed[i] = listing{threads, err}
		}()
	}
	main, err := a.listFor(ctx, client)
	if err != nil {
		// As before, a main list that fails answers nothing, and the folders, not needed, are neither waited for nor reported.
		cancel()
		folders.Wait()
		return nil, err
	}
	folders.Wait()
	seen := map[string]bool{}
	merged := &gvproto.RespListThreads{VersionToken: main.GetVersionToken(), PaginationToken: main.GetPaginationToken()}
	for _, thread := range main.GetThreads() {
		if !seen[thread.GetID()] {
			seen[thread.GetID()] = true
			merged.Threads = append(merged.Threads, thread)
		}
	}
	for i, extra := range extras {
		if err := listed[i].err; err != nil {
			a.diagnose(map[string]any{extra.name: "failed", "status": statusOf(err)})
			continue
		}
		for _, thread := range listed[i].threads.GetThreads() {
			if seen[thread.GetID()] {
				continue
			}
			seen[thread.GetID()] = true
			if !inFolder(thread, extra.folder) {
				thread.Folders = append(thread.Folders, extra.folder)
			}
			merged.Threads = append(merged.Threads, thread)
		}
	}
	return merged, nil
}

func inFolder(thread *gvproto.Thread, folder gvproto.ThreadFolder) bool {
	return slices.Contains(thread.GetFolders(), folder)
}

func (a *adapter) listFolder(ctx context.Context, folder gvproto.ThreadFolder) (*gvproto.RespListThreads, error) {
	return a.listFolderFor(ctx, a.current(), folder)
}

func (a *adapter) listFolderFor(ctx context.Context, client *libgv.Client, folder gvproto.ThreadFolder) (*gvproto.RespListThreads, error) {
	if a.listFolders != nil {
		return a.listFolders(ctx, folder)
	}
	if a.listThreads != nil {
		// A test that lists only the main folder has nothing archived.
		return &gvproto.RespListThreads{}, nil
	}
	if client == nil {
		return nil, errLoggedOut
	}
	return client.ListThreads(ctx, folder, "", "")
}

// Archives or unarchives one conversation in this account, with the same thread-attributes request that marks it read.
// Nothing reaches the other people.
func (a *adapter) setArchived(ctx context.Context, client *libgv.Client, chatID string, archived bool) (any, string) {
	update := a.updateAttributes
	if update == nil {
		update = func(ctx context.Context, client *libgv.Client, req *gvproto.ReqUpdateAttributes) error {
			_, err := client.UpdateThreadAttributes(ctx, req)
			return err
		}
	}
	err := update(ctx, client, &gvproto.ReqUpdateAttributes{
		Attributes:      &gvproto.ThreadAttributes{ThreadID: chatID, Archived: archived},
		OtherAttributes: &gvproto.ThreadAttributes{Archived: true},
		UnknownInt:      1,
	})
	if err != nil {
		if libgv.IsAuthError(err) {
			return nil, "auth_required"
		}
		return nil, "update_failed"
	}
	a.wake()
	return map[string]any{"archived": archived}, ""
}

// Removes one conversation from Google's spam folder. OtherAttributes is the field mask used by Voice's thread
// attributes endpoint; the false value in Attributes is therefore intentional. The app does not expose marking spam.
func (a *adapter) removeSpam(ctx context.Context, client *libgv.Client, chatID string) (any, string) {
	update := a.updateAttributes
	if update == nil {
		update = func(ctx context.Context, client *libgv.Client, req *gvproto.ReqUpdateAttributes) error {
			_, err := client.UpdateThreadAttributes(ctx, req)
			return err
		}
	}
	err := update(ctx, client, &gvproto.ReqUpdateAttributes{
		Attributes:      &gvproto.ThreadAttributes{ThreadID: chatID, IsSpammer: false},
		OtherAttributes: &gvproto.ThreadAttributes{IsSpammer: true},
		UnknownInt:      1,
	})
	if err != nil {
		if libgv.IsAuthError(err) {
			return nil, "auth_required"
		}
		return nil, "update_failed"
	}
	a.wake()
	return map[string]any{"spam": false}, ""
}
