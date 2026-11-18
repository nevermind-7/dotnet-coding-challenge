using System.Collections.Concurrent;

namespace dotnet.challenge.data.Cache;

/// <summary>
/// Thread-safe in-memory store. Individual operations are atomic; callers that need
/// a read-modify-write across several operations must provide their own coordination.
/// </summary>
public class SimpleObjectCache<TKey, TValue> : ISimpleObjectCache<TKey, TValue>
    where TKey : struct
    where TValue : class
{
    // Defect #1: a plain Dictionary is not safe for concurrent writers; parallel inserts can corrupt
    // its buckets (lost entries or infinite loops). ConcurrentDictionary makes each operation atomic.
    private readonly ConcurrentDictionary<TKey, TValue> _store = new();

    public Task AddAsync(TKey key, TValue value)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TKey key)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<TValue?> GetAsync(TKey key)
    {
        _store.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task<IEnumerable<TValue>> GetAllAsync()
    {
        IEnumerable<TValue> values = _store.Values.ToList();
        return Task.FromResult(values);
    }

    public Task UpdateAsync(TKey key, TValue value)
    {
        // Update only if the key still exists, without resurrecting a concurrently deleted entry
        // (a ContainsKey + indexer pair would be a check-then-act race).
        while (_store.TryGetValue(key, out var existing))
        {
            if (_store.TryUpdate(key, value, existing))
            {
                break;
            }
        }
        return Task.CompletedTask;
    }
}
