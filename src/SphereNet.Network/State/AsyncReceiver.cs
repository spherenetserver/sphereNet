using System.Net.Sockets;

namespace SphereNet.Network.State;

/// <summary>
/// Completion-driven receive for one connection: a receive is always outstanding on
/// the socket, and what arrives is appended to a small locked staging buffer that the
/// main loop drains into the connection's own receive buffer. The main loop does no
/// per-connection system call to find out whether data arrived, and it is woken as
/// soon as something does instead of when its sleep ends.
///
/// One instance belongs to one socket. When the connection slot is cleared the
/// instance is abandoned with the socket; a completion that lands afterwards only
/// touches this instance, never the slot's next connection.
/// </summary>
internal sealed class AsyncReceiver
{
    /// <summary>Most bytes held between two drains - the size of the connection's
    /// receive buffer, past which the synchronous path disconnects as well.</summary>
    public const int StagingCapacity = 65536;
    private const int ReadSize = 16384;

    private readonly Socket _socket;
    private readonly Action _signal;
    private readonly object _lock = new();
    private readonly byte[] _staging = new byte[StagingCapacity];
    private int _stagingLength;
    private bool _ended;      // peer closed, socket error, or abandoned
    private bool _overflow;   // the client sent more than one drain can hold
    private volatile bool _abandoned;

    public AsyncReceiver(Socket socket, Action signal)
    {
        _socket = socket;
        _signal = signal;
    }

    public void Start() => _ = RunAsync();

    /// <summary>Whether more arrived than the staging buffer holds (diagnostics/tests).</summary>
    internal bool HasOverflowed { get { lock (_lock) return _overflow; } }

    /// <summary>The slot moved on; nothing received from here on is delivered.</summary>
    public void Abandon() => _abandoned = true;

    private async Task RunAsync()
    {
        var buffer = new byte[ReadSize];
        try
        {
            while (!_abandoned)
            {
                int read = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).ConfigureAwait(false);
                if (read <= 0)
                    break; // the peer's FIN

                lock (_lock)
                {
                    if (_stagingLength + read > StagingCapacity)
                    {
                        _overflow = true;
                        break;
                    }
                    Buffer.BlockCopy(buffer, 0, _staging, _stagingLength, read);
                    _stagingLength += read;
                }
                _signal();
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }

        lock (_lock)
            _ended = true;
        _signal();
    }

    /// <summary>Main loop: move what has arrived into <paramref name="dest"/>.
    /// Returns the bytes moved; <paramref name="ended"/> once the connection is over
    /// and everything it sent has been handed out; <paramref name="overflow"/> when
    /// more arrived than the staging buffer can hold, or bytes are waiting and the
    /// destination is full - the synchronous path's "receive buffer full". What does
    /// not fit a partly full destination waits for the next drain, as it would wait
    /// in the kernel.</summary>
    public int Drain(Span<byte> dest, out bool ended, out bool overflow)
    {
        lock (_lock)
        {
            overflow = _overflow || (dest.Length == 0 && _stagingLength > 0);
            int count = Math.Min(_stagingLength, dest.Length);
            if (count > 0)
            {
                _staging.AsSpan(0, count).CopyTo(dest);
                _stagingLength -= count;
                if (_stagingLength > 0)
                    Buffer.BlockCopy(_staging, count, _staging, 0, _stagingLength);
            }
            ended = _ended && _stagingLength == 0 && count == 0;
            return count;
        }
    }
}
