using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AssetStudioMobile;

/// <summary>
/// An ObservableCollection that can replace its entire contents with one
/// CollectionChanged notification. Rebuilding thousands of rows one at a time
/// on the UI thread was the main source of loading and filtering freezes.
/// </summary>
internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ResetWith(IEnumerable<T> items)
    {
        var snapshot = items as IList<T> ?? items.ToList();
        Items.Clear();
        foreach (var item in snapshot)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
