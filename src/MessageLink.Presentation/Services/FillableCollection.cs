using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MessageLink.Presentation.Services;

// An ObservableCollection that can be filled from empty with one change notification. At launch the cached inbox went
// in as 246 separate inserts, each handled by the list control on the interface thread. They sat inside a 240 ms stall,
// but were only about 50 ms of it; the rest was the shell's first layout. A reset tells the list once that the contents
// are new, and it lays them out once.
// Only an empty collection is filled this way, so no selection, scroll position or row identity is replaced by it.
public sealed class FillableCollection<T> : ObservableCollection<T>
{
    public void Fill(IEnumerable<T> items)
    {
        if (Count != 0) throw new InvalidOperationException("Only an empty collection is filled at once.");
        CheckReentrancy();
        foreach (var item in items) Items.Add(item);
        if (Count == 0) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
