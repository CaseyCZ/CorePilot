namespace CorePilot.Infrastructure;

/// <summary>
/// A small, thread-safe bounded LRU cache. Values are released when evicted;
/// callers must not retain large entries elsewhere if the budget should hold.
/// </summary>
public sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private sealed record Entry(TValue Value, LinkedListNode<TKey> Node);

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly LinkedList<TKey> _order = new();

    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _entries = new Dictionary<TKey, Entry>(comparer);
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool TryGetValue(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (_entries.Remove(key, out var existing))
                _order.Remove(existing.Node);

            var node = _order.AddFirst(key);
            _entries.Add(key, new Entry(value, node));

            while (_entries.Count > _capacity)
            {
                var oldest = _order.Last;
                if (oldest is null) break;
                _entries.Remove(oldest.Value);
                _order.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }
}
