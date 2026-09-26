using System.Collections;
using System.Collections.Specialized;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// A list that's only ever replaced wholesale, with one Reset notification. The grid keeps the same
/// list and gets a Reset rather than a new ItemsSource: on a Reset, ItemsRepeater clears every tile
/// itself, which a custom layout can rely on (a new ItemsSource leaves that to the layout).
/// </summary>
public sealed class ResettableList<T> : IReadOnlyList<T>, IList, INotifyCollectionChanged
{
    private List<T> _items = [];

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _items.Count;
    public T this[int index] => _items[index];

    public void Reset(IEnumerable<T> items)
    {
        _items = [.. items];
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    // Read-only IList, so item sources can index it directly.
    object? IList.this[int index]
    {
        get => _items[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is T item && _items.Contains(item);
    int IList.IndexOf(object? value) => value is T item ? _items.IndexOf(item) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
}
