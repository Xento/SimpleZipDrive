using Microsoft.Win32.SafeHandles;

namespace SimpleZipDrive.Core;

/// <summary>
/// A read-only stream over a shared, cached decompressed entry buffer.
/// Every open of the same archive entry gets its own stream instance with an independent
/// position, but all instances share the single underlying <see cref="byte"/>[] so the entry
/// is decompressed only once regardless of how many handles (or on-demand reads) are active.
/// Disposing the stream releases the caller's reference; the buffer stays warm in the cache
/// until it is evicted under memory pressure or the owning core is disposed.
/// </summary>
internal sealed class SharedMemoryStream : Stream
{
    private readonly MemoryStream _inner;
    private readonly Action _onDispose;
    private bool _disposed;

    public SharedMemoryStream(byte[] buffer, Action onDispose)
    {
        _inner = new MemoryStream(buffer, false);
        _onDispose = onDispose;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        return _inner.Read(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _onDispose();
            }
        }

        _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A decompressed entry buffer shared by all open streams of the same archive entry.
/// <see cref="RefCount"/> tracks active opens; buffers with <see cref="RefCount"/> == 0 stay
/// warm in the memory cache and are evicted (LRU by <see cref="LastUsed"/>) only when a new
/// allocation would exceed the total memory cache limit.
/// </summary>
internal sealed class MemoryEntryCacheEntry
{
    public required byte[] Buffer { get; init; }

    public required int Size { get; init; }

    public int RefCount;

    public long LastUsed;
}

/// <summary>
/// Provides direct read access to a stored (uncompressed) entry within an archive stream
/// without extracting it to a separate cache. Uses the original archive stream with
/// position synchronization via an external lock.
/// </summary>
internal sealed class StoredEntryStream : Stream
{
    private const int ReadAheadBufferSize = 4 * 1024 * 1024; // 4 MB

    private readonly Stream _sourceStream;
    private readonly long _dataOffset;
    private readonly object _sourceLock;
    private readonly SafeFileHandle? _fileHandle;
    private long _position;
    private bool _disposed;

    private byte[]? _readAheadBuffer;
    private long _readAheadFileOffset = -1;
    private int _readAheadLength;
    private long _lastReadEnd = -1;

    public StoredEntryStream(Stream sourceStream, long dataOffset, long dataLength, object sourceLock)
    {
        if (dataOffset < 0 || dataOffset > sourceStream.Length)
            throw new ArgumentOutOfRangeException(nameof(dataOffset));

        ArgumentOutOfRangeException.ThrowIfNegative(dataLength);

        _sourceStream = sourceStream;
        _dataOffset = dataOffset;
        Length = dataLength;
        _sourceLock = sourceLock;
        _fileHandle = (sourceStream as FileStream)?.SafeFileHandle;
        _position = 0;
        _sourceStream.Position = dataOffset;

        if (dataLength > ReadAheadBufferSize)
        {
            _readAheadBuffer = new byte[ReadAheadBufferSize];
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > Length)
                throw new ArgumentOutOfRangeException(nameof(value));

            _position = value;
            _lastReadEnd = -1;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var maxBytes = (int)Math.Min(count, Length - _position);
        if (maxBytes <= 0) return 0;

        var targetPosition = _dataOffset + _position;

        if (_fileHandle != null)
        {
            var bytesRead = RandomAccess.Read(_fileHandle, buffer.AsSpan(offset, maxBytes), targetPosition);
            _position += bytesRead;
            return bytesRead;
        }

        lock (_sourceLock)
        {
            if (_sourceStream.Position != targetPosition)
            {
                _sourceStream.Position = targetPosition;
            }

            var bytesRead = _sourceStream.Read(buffer, offset, maxBytes);
            _position += bytesRead;
            return bytesRead;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var maxBytes = (int)Math.Min(buffer.Length, Length - _position);
        if (maxBytes <= 0) return 0;

        var targetPosition = _dataOffset + _position;

        if (_fileHandle != null)
        {
            var bytesRead = RandomAccess.Read(_fileHandle, buffer[..maxBytes], targetPosition);
            _position += bytesRead;
            return bytesRead;
        }

        lock (_sourceLock)
        {
            if (_sourceStream.Position != targetPosition)
            {
                _sourceStream.Position = targetPosition;
            }

            var bytesRead = _sourceStream.Read(buffer[..maxBytes]);
            _position += bytesRead;
            return bytesRead;
        }
    }

    public int ReadAt(long fileOffset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (fileOffset < 0 || fileOffset >= Length)
            return 0;

        var maxBytes = (int)Math.Min(count, Length - fileOffset);
        if (maxBytes <= 0) return 0;

        var buf = _readAheadBuffer;
        if (buf != null)
        {
            var isSequential = _lastReadEnd >= 0 && fileOffset == _lastReadEnd;

            switch (isSequential)
            {
                case true
                    when _readAheadFileOffset >= 0
                         && fileOffset >= _readAheadFileOffset
                         && fileOffset < _readAheadFileOffset + _readAheadLength:
                {
                    var bufStart = (int)(fileOffset - _readAheadFileOffset);
                    var available = _readAheadLength - bufStart;
                    var toCopy = Math.Min(maxBytes, available);
                    Buffer.BlockCopy(buf, bufStart, buffer, bufferOffset, toCopy);
                    _position = fileOffset + toCopy;
                    _lastReadEnd = fileOffset + toCopy;
                    return toCopy;
                }
                case true:
                {
                    var readAheadSize = (int)Math.Min(ReadAheadBufferSize, Length - fileOffset);
                    var directBytes = ReadFromSource(fileOffset, buf, 0, readAheadSize);
                    _readAheadFileOffset = fileOffset;
                    _readAheadLength = directBytes;

                    var resultBytes = Math.Min(maxBytes, directBytes);
                    Buffer.BlockCopy(buf, 0, buffer, bufferOffset, resultBytes);
                    _position = fileOffset + resultBytes;
                    _lastReadEnd = fileOffset + resultBytes;
                    return resultBytes;
                }
            }
        }

        var bytesRead = ReadFromSource(fileOffset, buffer, bufferOffset, maxBytes);
        _position = fileOffset + bytesRead;
        _lastReadEnd = fileOffset + bytesRead;
        return bytesRead;
    }

    private int ReadFromSource(long fileOffset, byte[] buffer, int bufferOffset, int count)
    {
        var targetPosition = _dataOffset + fileOffset;

        if (_fileHandle != null)
        {
            return RandomAccess.Read(_fileHandle, buffer.AsSpan(bufferOffset, count), targetPosition);
        }

        lock (_sourceLock)
        {
            if (_sourceStream.Position != targetPosition)
            {
                _sourceStream.Position = targetPosition;
            }

            return _sourceStream.Read(buffer, bufferOffset, count);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (_position < 0 || _position > Length)
            throw new IOException("Seek position out of range");

        _lastReadEnd = -1;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _readAheadBuffer = null;
        base.Dispose(disposing);
    }
}
