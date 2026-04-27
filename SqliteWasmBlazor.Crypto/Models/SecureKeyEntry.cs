using System.Runtime.InteropServices;
using System.Security.Cryptography;
using R3;
using SqliteWasmBlazor.Crypto.Services;

namespace SqliteWasmBlazor.Crypto.Models;

/// <summary>
/// A secure container for cryptographic key material stored in unmanaged memory.
/// Uses unmanaged memory to prevent GC from moving/copying the key material,
/// and ensures cryptographic zero-fill on disposal.
/// </summary>
public sealed class SecureKeyEntry : IDisposable
{
    private IntPtr _keyPtr;
    private int _keyLength;
    private bool _disposed;
    private readonly IDisposable? _expirationSubscription;
    private readonly Subject<SecureKeyEntry> _expiredSubject = new();

    /// <summary>
    /// One-shot observable that emits when the key expires.
    /// </summary>
    public Observable<SecureKeyEntry> Expired => _expiredSubject.Take(1);

    /// <summary>
    /// The time when this key was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The time when this key expires (null if no expiration).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Whether this key has expired.
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;

    /// <summary>
    /// Creates a new secure key entry with key material stored in unmanaged memory.
    /// </summary>
    /// <param name="key">The key material (will be copied to unmanaged memory)</param>
    /// <param name="ttl">Optional time-to-live</param>
    public SecureKeyEntry(ReadOnlySpan<byte> key, TimeSpan? ttl = null)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Key cannot be empty", nameof(key));
        }

        _keyLength = key.Length;
        _keyPtr = Marshal.AllocHGlobal(_keyLength);

        unsafe
        {
            key.CopyTo(new Span<byte>((void*)_keyPtr, _keyLength));
        }

        CreatedAt = DateTimeOffset.UtcNow;
        ExpiresAt = CreatedAt + ttl;

        // Set up one-shot expiration using Observable.Timer
        if (ttl.HasValue && ttl.Value > TimeSpan.Zero)
        {
            _expirationSubscription = Observable
                .Timer(ttl.Value)
                .Subscribe(_ => _expiredSubject.OnNext(this));
        }
    }

    /// <summary>
    /// Gets a copy of the key material.
    /// </summary>
    /// <returns>A copy of the key bytes</returns>
    /// <exception cref="ObjectDisposedException">If the key has been disposed</exception>
    /// <exception cref="InvalidOperationException">If the key has expired</exception>
    public byte[] GetKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsExpired)
        {
            throw new InvalidOperationException("Key has expired");
        }

        if (_keyPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Key has been disposed");
        }

        var copy = new byte[_keyLength];

        unsafe
        {
            new ReadOnlySpan<byte>((void*)_keyPtr, _keyLength).CopyTo(copy);
        }

        return copy;
    }

    /// <summary>
    /// Executes an action with direct access to the key material without creating a managed copy.
    /// </summary>
    /// <param name="action">Action to execute with the key span</param>
    /// <exception cref="ObjectDisposedException">If the key has been disposed</exception>
    /// <exception cref="InvalidOperationException">If the key has expired</exception>
    public void UseKey(ReadOnlySpanAction<byte> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsExpired)
        {
            throw new InvalidOperationException("Key has expired");
        }

        if (_keyPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Key has been disposed");
        }

        unsafe
        {
            action(new ReadOnlySpan<byte>((void*)_keyPtr, _keyLength));
        }
    }

    /// <summary>
    /// Disposes the key, securely zeroing the unmanaged memory.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Stop the expiration timer
        _expirationSubscription?.Dispose();
        _expiredSubject.Dispose();

        if (_keyPtr != IntPtr.Zero)
        {
            unsafe
            {
                CryptographicOperations.ZeroMemory(new Span<byte>((void*)_keyPtr, _keyLength));
            }

            Marshal.FreeHGlobal(_keyPtr);
            _keyPtr = IntPtr.Zero;
            _keyLength = 0;
        }

        _disposed = true;
    }
}
