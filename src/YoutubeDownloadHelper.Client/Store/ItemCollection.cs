using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Client.Store;

/// <summary>The queue in order (by position, then ID), with minimal change notifications.</summary>
internal sealed class ItemCollection : ObservableCollection<ItemInfo>
{
    private readonly Dictionary<long, ItemInfo> _byId = [];

    public ItemInfo? Find(long id) => _byId.GetValueOrDefault(id);

    /// <summary>Replaces everything with one <see cref="NotifyCollectionChangedAction.Reset"/>.</summary>
    public void ReplaceAll(IEnumerable<ItemInfo> items)
    {
        CheckReentrancy();
        Items.Clear();
        _byId.Clear();
        foreach (var item in items.Where(i => i.Position > 0).OrderBy(i => i.Position).ThenBy(i => i.Id))
        {
            if (_byId.TryAdd(item.Id, item))
            {
                Items.Add(item);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Adds the item or replaces the one with its ID, and moves it to its place.</summary>
    public void Upsert(ItemInfo item)
    {
        if (_byId.ContainsKey(item.Id))
        {
            var index = IndexOfId(item.Id);
            _byId[item.Id] = item;
            this[index] = item;
            Reposition(index);
            return;
        }

        _byId[item.Id] = item;
        var at = 0;
        while (at < Count && Compare(this[at], item) < 0)
        {
            at++;
        }

        Insert(at, item);
    }

    public bool RemoveId(long id)
    {
        if (!_byId.Remove(id))
        {
            return false;
        }

        RemoveAt(IndexOfId(id));
        return true;
    }

    /// <summary>Gives items new positions and restores the order. False if one of them is unknown (nothing changes then).</summary>
    public bool SetPositions(IReadOnlyList<(long Id, long Position)> positions)
    {
        if (positions.Any(p => !_byId.ContainsKey(p.Id)))
        {
            return false;
        }

        foreach (var (id, position) in positions)
        {
            var item = _byId[id] with { Position = position };
            _byId[id] = item;
            this[IndexOfId(id)] = item;
        }

        var sorted = Items.OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (this[i].Id != sorted[i].Id)
            {
                Move(IndexOfId(sorted[i].Id, from: i), i);
            }
        }

        return true;
    }

    private static int Compare(ItemInfo a, ItemInfo b)
    {
        var byPosition = a.Position.CompareTo(b.Position);
        return byPosition != 0 ? byPosition : a.Id.CompareTo(b.Id);
    }

    private void Reposition(int index)
    {
        var item = this[index];
        var target = index;
        while (target > 0 && Compare(this[target - 1], item) > 0)
        {
            target--;
        }

        while (target < Count - 1 && Compare(this[target + 1], item) < 0)
        {
            target++;
        }

        if (target != index)
        {
            Move(index, target);
        }
    }

    private int IndexOfId(long id, int from = 0)
    {
        for (var i = from; i < Count; i++)
        {
            if (this[i].Id == id)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Item {id} is not in the collection.");
    }
}
