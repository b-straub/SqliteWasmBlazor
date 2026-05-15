using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using R3;
using SqliteWasmBlazor.Crypto.Configuration;
using SqliteWasmBlazor.Crypto.Models;

namespace SqliteWasmBlazor.Crypto.Services;

/// <summary>
/// Secure key cache storing keys in unmanaged memory outside .NET GC control.
/// Keys are stored with configurable TTL and cryptographically zero-filled on disposal/expiration.
/// Keys trigger their own expiration via one-shot timers for immediate cleanup.
/// </summary>
internal sealed class SecureKeyCache : ISecureKeyCache
{
    private readonly ConcurrentDictionary<string, SecureKeyEntry> _cache = new();
    private readonly KeyCacheOptions _options;
    private readonly Subject<string> _keyExpiredSubject = new();
    private bool _disposed;

       public Observable<string> KeyExpired => _keyExpiredSubject;

    public SecureKeyCache(IOptions<KeyCacheOptions> options)
    {
        _options = options.Value;
    }

       public void Store(string keyId, byte[] key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentNullException.ThrowIfNull(key);

        // Remove existing entry if present
        if (_cache.TryRemove(keyId, out var existing))
        {
            existing.Dispose();
        }

        // Determine TTL based on strategy.
        // For Strategy.None: no TTL timer - key is removed on first TryGet/UseKey.
        TimeSpan? ttl = _options.Strategy switch
        {
            KeyCacheStrategy.NONE => null, // No timer - removed after single use
            KeyCacheStrategy.SESSION => null, // No expiration (until page refresh)
            KeyCacheStrategy.TIMED => TimedTtl(),
            _ => TimeSpan.FromMinutes(15) // Default
        };

        var entry = new SecureKeyEntry(key, ttl);

        // Subscribe to key's one-shot expiration observable (only for timed strategies)
        if (ttl.HasValue)
        {
            var capturedKeyId = keyId;
            entry.Expired.Subscribe(_ => RemoveExpired(capturedKeyId));
        }

        _cache[keyId] = entry;
    }

       public byte[]? TryGet(string keyId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(keyId))
        {
            return null;
        }

        if (!_cache.TryGetValue(keyId, out var entry))
        {
            return null;
        }

        if (entry.IsExpired)
        {
            RemoveExpired(keyId);
            return null;
        }

        try
        {
            var key = entry.GetKey();
            if (_options.Strategy == KeyCacheStrategy.NONE)
            {
                Remove(keyId);
            }
            return key;
        }
        catch
        {
            Remove(keyId);
            return null;
        }
    }

       public bool UseKey(string keyId, ReadOnlySpanAction<byte> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(action);

        if (string.IsNullOrEmpty(keyId))
        {
            return false;
        }

        if (!_cache.TryGetValue(keyId, out var entry))
        {
            return false;
        }

        if (entry.IsExpired)
        {
            RemoveExpired(keyId);
            return false;
        }

        try
        {
            entry.UseKey(action);

            // For Strategy.None: remove key immediately after single use (no event)
            if (_options.Strategy == KeyCacheStrategy.NONE)
            {
                Remove(keyId);
            }

            return true;
        }
        catch
        {
            Remove(keyId);
            return false;
        }
    }

       public bool UseKey<TResult>(string keyId, ReadOnlySpanFunc<byte, TResult> func, out TResult? result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(func);

        result = default;

        if (string.IsNullOrEmpty(keyId))
        {
            return false;
        }

        if (!_cache.TryGetValue(keyId, out var entry))
        {
            return false;
        }

        if (entry.IsExpired)
        {
            RemoveExpired(keyId);
            return false;
        }

        try
        {
            TResult? capturedResult = default;
            entry.UseKey(span => capturedResult = func(span));
            result = capturedResult;

            // For Strategy.None: remove key immediately after single use (no event)
            if (_options.Strategy == KeyCacheStrategy.NONE)
            {
                Remove(keyId);
            }

            return true;
        }
        catch
        {
            Remove(keyId);
            return false;
        }
    }

       public bool Contains(string keyId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(keyId))
        {
            return false;
        }

        if (!_cache.TryGetValue(keyId, out var entry))
        {
            return false;
        }

        if (entry.IsExpired)
        {
            RemoveExpired(keyId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Remove a specific key from the cache and zero its memory.
    /// </summary>
    /// <param name="keyId">The key identifier to remove</param>
    public void Remove(string keyId)
    {
        if (string.IsNullOrEmpty(keyId))
        {
            return;
        }

        if (_cache.TryRemove(keyId, out var entry))
        {
            entry.Dispose();
        }
    }

       public void Clear()
    {
        foreach (var keyId in _cache.Keys.ToList())
        {
            if (_cache.TryRemove(keyId, out var entry))
            {
                entry.Dispose();
            }
        }
    }

       public void NotifyKeyCleared(string keyId)
    {
        if (_disposed) return;
        _keyExpiredSubject.OnNext(keyId);
    }

    /// <summary>
    /// Remove all expired keys from the cache.
    /// </summary>
    public void CleanupExpired()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var keyId in _cache.Keys.ToList())
        {
            if (_cache.TryGetValue(keyId, out var entry) && entry.IsExpired)
            {
                RemoveExpired(keyId);
            }
        }
    }

    private TimeSpan TimedTtl()
    {
        // TtlMs takes precedence when set so integration tests can drive the
        // expiry timer within an E2E budget without bumping production minutes.
        if (_options.TtlMs is { } ms)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return TimeSpan.FromMinutes(_options.TtlMinutes);
    }

    /// <summary>
    /// Remove a key that has expired and emit via KeyExpired observable.
    /// Note: For Strategy.None, keys are removed immediately after use via Remove(),
    /// so this method is never called for that strategy.
    /// </summary>
    private void RemoveExpired(string keyId)
    {
        if (_cache.TryRemove(keyId, out var entry))
        {
            entry.Dispose();
            _keyExpiredSubject.OnNext(keyId);
        }
    }

    /// <summary>
    /// Dispose the cache and securely zero all stored keys.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _keyExpiredSubject.Dispose();

        // Zero all keys
        Clear();
    }
}
