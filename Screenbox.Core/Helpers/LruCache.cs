using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Screenbox.Core.Helpers;

/// <summary>
/// A small least-recently-used cache. Reading an entry counts as using it. A null value is a valid
/// entry, which lets a caller remember a negative result.
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _nodes = new();

    /// <summary>Creates a cache that holds at most <paramref name="capacity"/> entries.</summary>
    public LruCache(int capacity)
    {
        _capacity = capacity;
    }

    /// <summary>Gets the number of entries.</summary>
    public int Count => _nodes.Count;

    /// <summary>Looks up an entry and marks it as the most recently used.</summary>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (!_nodes.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue>>? node))
        {
            value = default;
            return false;
        }

        _order.Remove(node);
        _order.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    /// <summary>Adds or replaces an entry, evicting the least recently used one if the cache is full.</summary>
    public void Add(TKey key, TValue value)
    {
        if (_nodes.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue>>? existing))
        {
            _order.Remove(existing);
        }

        _nodes[key] = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));

        if (_nodes.Count > _capacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _nodes.Remove(oldest.Value.Key);
        }
    }
}
