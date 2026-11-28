using System.Collections.ObjectModel;

namespace MessageLink.Presentation.Services;

public static class CollectionReordering
{
    // A single row moving down should not remove/reinsert every intervening row.
    public static bool TrySingleMove<T>(ObservableCollection<T> current, IReadOnlyList<T> desired)
    {
        if (current.Count != desired.Count) return false;
        var comparer=EqualityComparer<T>.Default;
        var first=0;
        while(first<current.Count && comparer.Equals(current[first],desired[first])) first++;
        if(first==current.Count) return true;
        var from=current.IndexOf(desired[first]);
        var to=-1;
        for(var i=first+1;i<desired.Count;i++) if(comparer.Equals(desired[i],current[first])) {to=i;break;}
        // Whether moving one row would produce the wanted order, walked rather than materialized: index i of the moved
        // list maps back to an index of this one, which is all the old copy-and-compare was for. It ran twice on every
        // rebuild, each time copying the whole list.
        bool Matches(int source,int destination)
        {
            if(source<0 || destination<0) return false;
            for(var i=0;i<desired.Count;i++)
            {
                int origin;
                if(i==destination) origin=source;
                else if(source<destination) origin=i<source?i:i<=destination?i+1:i;
                else origin=i<destination?i:i<=source?i-1:i;
                if(!comparer.Equals(current[origin],desired[i])) return false;
            }
            return true;
        }
        if(Matches(from,first)) {current.Move(from,first);return true;}
        if(Matches(first,to)) {current.Move(first,to);return true;}
        return false;
    }
}
