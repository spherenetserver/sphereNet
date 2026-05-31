using System.Buffers;
using System.Buffers.Binary;
using SphereNet.Network.Encryption;

namespace SphereNet.Network.Packets;

/// <summary>
/// Big-endian packet buffer for UO protocol. Maps to Packet in Source-X.
/// UO protocol uses network byte order (big-endian) for all multi-byte values.
/// </summary>
public sealed class PacketBuffer
{
    private byte[] _data;
    private int _position;
    private int _length;
    private readonly bool _pooled;
    private bool _returned;

    // Broadcast sharing: a single built packet can be enqueued onto many send
    // queues (one event, identical bytes for every recipient). _refCount tracks
    // how many flushes still owe a ReturnToPool; the backing array goes back only
    // when the last recipient has consumed it. Unicast packets keep _refCount=1
    // and behave exactly as before. The Huffman-compressed payload is computed
    // once by the first flush and cached here (UNENCRYPTED, read-only) so the
    // other recipients reuse it instead of recompressing.
    private int _refCount = 1;
    private byte[]? _sharedCompressed;
    private int _sharedCompressedLen;

    /// <summary>True once this buffer has been marked for multi-recipient
    /// broadcast (so FlushOutput caches/reuses its compressed payload and never
    /// encrypts it in place).</summary>
    public bool IsShared { get; private set; }

    /// <summary>Mark this packet as shared across <paramref name="recipientCount"/>
    /// send queues, and pre-compute its Huffman-compressed (pre-encryption)
    /// payload once here. Pre-computing at broadcast time (not lazily on the
    /// first flush) is what lets recipients' FlushOutput run in PARALLEL: the
    /// cache is then immutable and read-only during flushing, so there is no
    /// init race. Must be called before enqueuing onto any queue.</summary>
    public void MarkShared(int recipientCount)
    {
        IsShared = true;
        _refCount = recipientCount < 1 ? 1 : recipientCount;
        if (_sharedCompressed == null)
        {
            _sharedCompressed = HuffmanCompression.Compress(_data, 0, _length);
            _sharedCompressedLen = _sharedCompressed.Length;
        }
    }

    /// <summary>The cached, compression-only (pre-encryption) payload, or null
    /// until the first flush computes it.</summary>
    public byte[]? SharedCompressed => _sharedCompressed;
    public int SharedCompressedLen => _sharedCompressedLen;

    /// <summary>Cache the compressed payload for reuse by the remaining
    /// recipients. The array is treated as immutable after this — recipients
    /// that need encryption copy it first, they never encrypt it in place.</summary>
    public void SetSharedCompressed(byte[] compressed, int length)
    {
        _sharedCompressed = compressed;
        _sharedCompressedLen = length;
    }

    /// <summary>Outgoing packet: rent the backing array from the shared pool.
    /// The byte[] is the bulk of a packet's allocation, and every outgoing
    /// PacketBuffer is built, queued and flushed exactly once (broadcasts build
    /// per recipient), so <see cref="ReturnToPool"/> can recycle it right after
    /// FlushOutput consumes the bytes. Rent may return a larger array than
    /// requested — all readers use the logical <see cref="Length"/>, never
    /// _data.Length, so the slack is never observed.</summary>
    public PacketBuffer(int capacity = 64)
    {
        _data = ArrayPool<byte>.Shared.Rent(capacity);
        _pooled = true;
    }

    /// <summary>Incoming/wrapped packet: owns a caller-supplied array verbatim.
    /// Never pooled — the array is not ours to recycle and its Length is the
    /// payload length.</summary>
    public PacketBuffer(byte[] data)
    {
        _data = data;
        _length = data.Length;
        _pooled = false;
    }

    public int Position { get => _position; set => _position = value; }
    public int Length => _length;
    public byte[] Data => _data;
    public ReadOnlySpan<byte> Span => _data.AsSpan(0, _length);
    public bool IsUnderrun { get; private set; }

    private void EnsureCapacity(int needed)
    {
        int required = _position + needed;
        if (required <= _data.Length) return;

        int newSize = Math.Max(_data.Length * 2, required);
        if (_pooled)
        {
            // Grow within the pool: rent a bigger array, copy the written
            // bytes, return the old one.
            byte[] bigger = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_data, 0, bigger, 0, _length);
            ArrayPool<byte>.Shared.Return(_data);
            _data = bigger;
        }
        else
        {
            Array.Resize(ref _data, newSize);
        }
    }

    /// <summary>Release one flush's claim on the buffer; the pooled backing
    /// array is returned to the pool only when the last recipient releases it
    /// (refcount reaches zero). Idempotent past zero; a no-op for wrapped
    /// (non-pooled) buffers. For a unicast packet (refcount 1) this returns
    /// immediately, exactly as before. The buffer must not be read or written
    /// after the final release — the backing array is swapped for an empty one
    /// so any stray use fails loudly instead of corrupting a recycled array.</summary>
    public void ReturnToPool()
    {
        if (!_pooled || _returned) return;
        if (System.Threading.Interlocked.Decrement(ref _refCount) > 0) return;
        _returned = true;
        ArrayPool<byte>.Shared.Return(_data);
        _data = Array.Empty<byte>();
    }

    // --- Write methods (big-endian) ---

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _data[_position++] = value;
        if (_position > _length) _length = _position;
    }

    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(_position), value);
        _position += 2;
        if (_position > _length) _length = _position;
    }

    public void WriteInt16(short value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteInt16BigEndian(_data.AsSpan(_position), value);
        _position += 2;
        if (_position > _length) _length = _position;
    }

    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(_position), value);
        _position += 4;
        if (_position > _length) _length = _position;
    }

    public void WriteInt32(int value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteInt32BigEndian(_data.AsSpan(_position), value);
        _position += 4;
        if (_position > _length) _length = _position;
    }

    public void WriteAsciiFixed(string text, int length)
    {
        EnsureCapacity(length);
        int i = 0;
        for (; i < text.Length && i < length; i++)
            _data[_position + i] = (byte)text[i];
        for (; i < length; i++)
            _data[_position + i] = 0;
        _position += length;
        if (_position > _length) _length = _position;
    }

    public void WriteAsciiNull(string text)
    {
        int len = text.Length + 1;
        EnsureCapacity(len);
        for (int i = 0; i < text.Length; i++)
            _data[_position + i] = (byte)text[i];
        _data[_position + text.Length] = 0;
        _position += len;
        if (_position > _length) _length = _position;
    }

    public void WriteUnicodeNullBE(string text)
    {
        int len = (text.Length + 1) * 2;
        EnsureCapacity(len);
        for (int i = 0; i < text.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(_position), text[i]);
            _position += 2;
        }
        WriteUInt16(0);
        if (_position > _length) _length = _position;
    }

    /// <summary>Write a Unicode string in Little Endian without null terminator (for 0xD6 tooltip args).</summary>
    public void WriteUnicodeLE(string text)
    {
        int len = text.Length * 2;
        EnsureCapacity(len);
        for (int i = 0; i < text.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(_position), text[i]);
            _position += 2;
        }
        if (_position > _length) _length = _position;
    }

    public void WriteUnicodeLeNullTerminated(string text)
    {
        WriteUnicodeLE(text);
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(_position), 0);
        _position += 2;
        if (_position > _length) _length = _position;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_data.AsSpan(_position));
        _position += bytes.Length;
        if (_position > _length) _length = _position;
    }

    /// <summary>
    /// Write packet length at a specific position (for variable-length packets).
    /// </summary>
    public void WriteLengthAt(int position)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(position), (ushort)_length);
    }

    // --- Read methods (big-endian) ---

    public byte ReadByte()
    {
        if (_position >= _length) { IsUnderrun = true; return 0; }
        return _data[_position++];
    }

    public sbyte ReadSByte() => (sbyte)ReadByte();
    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUInt16()
    {
        if (_position + 2 > _length) { IsUnderrun = true; return 0; }
        var val = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_position));
        _position += 2;
        return val;
    }

    public short ReadInt16()
    {
        if (_position + 2 > _length) { IsUnderrun = true; return 0; }
        var val = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_position));
        _position += 2;
        return val;
    }

    public uint ReadUInt32()
    {
        if (_position + 4 > _length) { IsUnderrun = true; return 0; }
        var val = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_position));
        _position += 4;
        return val;
    }

    public int ReadInt32()
    {
        if (_position + 4 > _length) { IsUnderrun = true; return 0; }
        var val = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_position));
        _position += 4;
        return val;
    }

    public string ReadAsciiFixed(int length)
    {
        if (_position + length > _length) { IsUnderrun = true; return ""; }
        int end = _position + length;
        int nullIdx = Array.IndexOf(_data, (byte)0, _position, length);
        int strLen = nullIdx >= 0 ? nullIdx - _position : length;
        var result = System.Text.Encoding.ASCII.GetString(_data, _position, strLen);
        _position = end;
        return result;
    }

    public string ReadAsciiNull()
    {
        int start = _position;
        while (_position < _length && _data[_position] != 0)
            _position++;
        string result = System.Text.Encoding.ASCII.GetString(_data, start, _position - start);
        if (_position < _length) _position++; // skip null
        return result;
    }

    public string ReadUnicodeNullBE(int maxChars = 4096)
    {
        var sb = new System.Text.StringBuilder(Math.Min(maxChars, 256));
        int count = 0;
        while (_position + 2 <= _length && count < maxChars)
        {
            ushort ch = ReadUInt16();
            if (ch == 0) break;
            sb.Append((char)ch);
            count++;
        }
        return sb.ToString();
    }

    public string ReadUnicodeFixed(int charCount)
    {
        var sb = new System.Text.StringBuilder(charCount);
        for (int i = 0; i < charCount && _position + 2 <= _length; i++)
        {
            ushort ch = ReadUInt16();
            if (ch != 0) sb.Append((char)ch);
        }
        return sb.ToString();
    }

    public byte[] ReadBytes(int count)
    {
        if (_position + count > _length)
        {
            IsUnderrun = true;
            count = _length - _position;
        }
        var result = new byte[count];
        Array.Copy(_data, _position, result, 0, count);
        _position += count;
        return result;
    }

    public int Remaining => _length - _position;
    public bool HasBytes(int count) => count >= 0 && _position + count <= _length;

    public void Reset()
    {
        _position = 0;
        _length = 0;
        IsUnderrun = false;
    }
}
