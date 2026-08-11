namespace WindowsClientCenter.Shared.Diagnostics;

public sealed class SlidingWindowBuffer<T>
{
    private readonly List<T> _items = [];

    public SlidingWindowBuffer(int maxBufferedItems)
    {
        if (maxBufferedItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBufferedItems));
        }

        MaxBufferedItems = maxBufferedItems;
    }

    public int MaxBufferedItems { get; }

    public int Count => _items.Count;

    public void Clear()
    {
        _items.Clear();
    }

    public int Add(T item)
    {
        _items.Add(item);
        return TrimIfNeeded();
    }

    public int AddRange(IEnumerable<T> items)
    {
        _items.AddRange(items);
        return TrimIfNeeded();
    }

    public IReadOnlyList<T> GetWindow(int maxItems)
    {
        if (maxItems <= 0 || _items.Count == 0)
        {
            return [];
        }

        var startIndex = Math.Max(0, _items.Count - maxItems);
        var count = _items.Count - startIndex;
        return _items.GetRange(startIndex, count);
    }

    private int TrimIfNeeded()
    {
        if (_items.Count <= MaxBufferedItems)
        {
            return 0;
        }

        var overflow = _items.Count - MaxBufferedItems;
        _items.RemoveRange(0, overflow);
        return overflow;
    }
}
