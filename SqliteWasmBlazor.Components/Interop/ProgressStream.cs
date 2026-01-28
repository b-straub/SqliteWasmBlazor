namespace SqliteWasmBlazor.Components.Interop;

/// <summary>
/// Stream wrapper that reports progress as bytes are read
/// </summary>
public class ProgressStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _totalBytes;
    private readonly Action<long> _onProgress;
    private long _bytesRead;
    private long _lastReportedBytes;

    public ProgressStream(Stream baseStream, long totalBytes, Action<long> onProgress)
    {
        _baseStream = baseStream;
        _totalBytes = totalBytes;
        _onProgress = onProgress;
        _bytesRead = 0;
    }

    public override bool CanRead => _baseStream.CanRead;
    public override bool CanSeek => _baseStream.CanSeek;
    public override bool CanWrite => _baseStream.CanWrite;
    public override long Length => _baseStream.Length;

    public override long Position
    {
        get => _baseStream.Position;
        set => _baseStream.Position = value;
    }

    public override void Flush() => _baseStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _baseStream.Read(buffer, offset, count);
        UpdateProgress(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
        UpdateProgress(bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _baseStream.ReadAsync(buffer, cancellationToken);
        UpdateProgress(bytesRead);
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
    public override void SetLength(long value) => _baseStream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);

    private void UpdateProgress(int bytesRead)
    {
        if (bytesRead > 0)
        {
            _bytesRead += bytesRead;
            // Only report progress every 64KB to avoid too many UI updates
            if (_bytesRead - _lastReportedBytes >= 65536 || _bytesRead >= _totalBytes)
            {
                _onProgress(_bytesRead);
                _lastReportedBytes = _bytesRead;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _baseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
