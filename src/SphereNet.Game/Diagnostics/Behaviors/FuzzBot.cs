using System.Buffers.Binary;
using System.Text;
using SphereNet.Network.Packets;

namespace SphereNet.Game.Diagnostics.Behaviors;

/// <summary>
/// A hostile but well-framed client: every packet it sends has a correct opcode and
/// length, and a payload chosen to break the handler behind it - serials of objects
/// it can and cannot reach, amounts and coordinates at their limits, counts that
/// promise more data than follows, truncated and over-long bodies, unknown 0xBF/0xD7
/// sub-commands. Between bursts it does ordinary things (opens its pack, double-clicks
/// what it sees, uses skills, talks) so the server has open containers, target
/// cursors, gumps and vendor windows for the hostile packets to land in.
///
/// Framing attacks (a lying length field, a stalled partial packet) are not sent:
/// those close the connection before any handler runs, and HostileConnectionTests
/// covers them. What this exercises is the handler surface behind a valid frame.
///
/// Every packet is written to <c>trace</c> (hex, one line, flushed) before it is
/// sent, so a server that dies leaves the packets that killed it on disk.
/// </summary>
public sealed class FuzzBot : IBotBehavior
{
    public string Name => "Fuzz";

    private readonly Random _rng;
    private readonly byte[] _opcodes;
    private readonly TextWriter? _trace;
    private readonly int _packetsPerSecond;

    public long PacketsSent;

    /// <param name="opcodes">Client opcodes to aim at - the server's registered
    /// handlers (<see cref="PacketManager.RegisteredOpcodes"/>).</param>
    public FuzzBot(int seed, IEnumerable<byte> opcodes, TextWriter? trace = null, int packetsPerSecond = 25)
    {
        _rng = new Random(seed);
        _opcodes = opcodes.ToArray();
        _trace = trace;
        _packetsPerSecond = Math.Max(1, packetsPerSecond);
    }

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        int delayMs = Math.Max(1, 1000 / _packetsPerSecond);
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            Send(bot, Next(bot));
            await Task.Delay(delayMs, ct);
        }
    }

    /// <summary>The next packet this bot would send: ordinary play a quarter of the
    /// time, hostile otherwise.</summary>
    internal byte[] Next(BotClient bot) => _rng.Next(100) < 25 ? Context(bot) : Hostile(bot);

    /// <summary>The length the server frames a fixed packet with, for the 7.0 client
    /// the bot announces (0 = variable).</summary>
    internal static int FixedLength(byte op) => op == 0x08 ? 15 : PacketDefinitions.GetPacketLength(op);

    /// <summary>Longest 0xBE the server always frames by its length field.</summary>
    internal const int MaxAssistVersionLength = 128;

    private void Send(BotClient bot, byte[] packet)
    {
        if (_trace != null)
        {
            lock (_trace)
            {
                _trace.WriteLine($"{Environment.TickCount64} {Convert.ToHexString(packet)}");
                _trace.Flush();
            }
        }
        bot.SendRawPacket(packet);
        Interlocked.Increment(ref PacketsSent);
    }

    // ---- ordinary play: gives the hostile packets state to land in -----------

    private byte[] Context(BotClient bot)
    {
        var w = bot.World;
        return _rng.Next(8) switch
        {
            0 => BotPacketBuilder.BuildDoubleClick(w.BackpackSerial != 0 ? w.BackpackSerial : w.CharUid),
            1 => BotPacketBuilder.BuildDoubleClick(AnySerial(bot)),
            2 => BotPacketBuilder.BuildSkillUse((ushort)_rng.Next(0, 58)),
            3 => BotPacketBuilder.BuildCastSpell(_rng.Next(1, 65)),
            4 => BotPacketBuilder.BuildSpeech(Pick(SpeechLines)),
            5 => BotPacketBuilder.BuildMoveRequest((byte)_rng.Next(8), 0),
            6 => BotPacketBuilder.BuildDoubleClick(KnownMobile(bot)),
            _ => BotPacketBuilder.BuildWarMode(_rng.Next(2) == 0),
        };
    }

    private static readonly string[] SpeechLines =
    [
        "vendor buy", "vendor sell", "buy", "sell", "bank", "balance", "guards", "i resign from my guild",
        "train", "hire", "stable", "claim", "all follow me", "all kill", "all stop", "news", "help",
    ];

    // ---- hostile packets -------------------------------------------------------

    private byte[] Hostile(BotClient bot)
    {
        int roll = _rng.Next(100);
        if (roll < 55) return Structured(bot);
        byte op = _opcodes[_rng.Next(_opcodes.Length)];
        if (roll < 80) return RandomBody(op);
        return BitFlip(Structured(bot));
    }

    /// <summary>Correct frame, random body: the defined length for a fixed packet, a
    /// random length (often tiny) for a variable one.</summary>
    private byte[] RandomBody(byte op)
    {
        int fixedLen = FixedLength(op);
        if (fixedLen > 0)
        {
            var p = new byte[fixedLen];
            _rng.NextBytes(p);
            p[0] = op;
            return p;
        }
        int len = _rng.Next(4) switch
        {
            0 => 3,
            1 => _rng.Next(4, 12),
            2 => _rng.Next(12, 64),
            _ => _rng.Next(64, 1200),
        };
        // 0xBE longer than 128 bytes may be framed as the 5-byte legacy assist form
        // (NetworkManager.TryGetLegacyAssistPacketLength): the rest would misalign
        // this bot's own stream.
        if (op == 0xBE)
            len = Math.Min(len, MaxAssistVersionLength);
        var v = new byte[len];
        _rng.NextBytes(v);
        return Frame(op, v.AsSpan(3));
    }

    private byte[] BitFlip(byte[] packet)
    {
        int start = FixedLength(packet[0]) > 0 ? 1 : 3;
        if (packet.Length <= start) return packet;
        int flips = 1 + _rng.Next(4);
        for (int i = 0; i < flips; i++)
            packet[start + _rng.Next(packet.Length - start)] ^= (byte)(1 << _rng.Next(8));
        return packet;
    }

    private byte[] Structured(BotClient bot)
    {
        var w = bot.World;
        switch (_rng.Next(30))
        {
            case 0: // 0x02 move: any direction byte, any sequence, any fast-walk key
            {
                var p = new byte[7];
                p[0] = 0x02; p[1] = (byte)_rng.Next(256); p[2] = (byte)_rng.Next(256);
                BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(3), U32());
                return p;
            }
            case 1: return Fixed(0x05, b => Put32(b, 1, Serial(bot)));
            case 2: return Fixed(0x06, b => Put32(b, 1, Serial(bot)));
            case 3: return Fixed(0x09, b => Put32(b, 1, Serial(bot)));
            case 4: // 0x07 pick up
                return Fixed(0x07, b => { Put32(b, 1, Serial(bot)); Put16(b, 5, Amount()); });
            case 5: // 0x08 drop (7.0.9+ layout with grid byte: 15 bytes)
            {
                var p = new byte[15];
                p[0] = 0x08;
                Put32(p, 1, Serial(bot));
                Put16(p, 5, Coord(w.X)); Put16(p, 7, Coord(w.Y)); p[9] = (byte)Z();
                p[10] = (byte)_rng.Next(256);
                Put32(p, 11, _rng.Next(3) == 0 ? 0xFFFFFFFF : Serial(bot));
                return p;
            }
            case 6: // 0x13 equip
                return Fixed(0x13, b => { Put32(b, 1, Serial(bot)); b[5] = (byte)_rng.Next(256); Put32(b, 6, Serial(bot)); });
            case 7: // 0x6C target response
                return Fixed(0x6C, b =>
                {
                    b[1] = (byte)_rng.Next(4);
                    Put32(b, 2, w.HasPendingTarget && _rng.Next(2) == 0 ? w.TargetCursorId : U32());
                    b[6] = (byte)_rng.Next(4);
                    Put32(b, 7, Serial(bot));
                    Put16(b, 11, Coord(w.X)); Put16(b, 13, Coord(w.Y));
                    b[15] = (byte)_rng.Next(256); b[16] = (byte)Z();
                    Put16(b, 17, (ushort)_rng.Next(0x10000));
                });
            case 8: return GumpResponse(bot);
            case 9: return VendorBuy(bot);
            case 10: return VendorSell(bot);
            case 11: return SecureTrade(bot);
            case 12: return TextCommand();
            case 13: return UnicodeSpeech();
            case 14: return AsciiSpeech();
            case 15: return Fixed(0x34, b => { Put32(b, 1, U32()); b[5] = (byte)_rng.Next(256); Put32(b, 6, Serial(bot)); });
            case 16: return Fixed(0x72, b => { b[1] = (byte)_rng.Next(256); b[2] = 0; b[3] = 0x32; b[4] = 0; });
            case 17: return Variable(0x98, b => { b.Put32(Serial(bot)); });
            case 18: return Tooltips(bot);
            case 19: return Extended(bot);
            case 20: return Encoded(bot);
            case 21: return Fixed(0x75, b => { Put32(b, 1, Serial(bot)); Ascii(b, 5, 30, RandomText(40)); });
            case 22: return SkillLock();
            case 23: return BookPage(bot);
            case 24: return BookHeader(bot);
            case 25: return BulletinBoard(bot);
            case 26: return Prompt(bot);
            case 27: return Fixed(0x95, b => { Put32(b, 1, Serial(bot)); Put16(b, 5, (ushort)_rng.Next(0x10000)); Put16(b, 7, (ushort)_rng.Next(0x10000)); });
            case 28: return MapPin(bot);
            default: // 0xB8 profile: read or update, any serial, any text
                return Variable(0xB8, b =>
                {
                    b.Put8((byte)_rng.Next(0, 3));
                    b.Put32(Serial(bot));
                    if (_rng.Next(2) == 0)
                    {
                        b.Put16((ushort)_rng.Next(0x10000));
                        string s = RandomText(_rng.Next(0, 400));
                        b.Put16(_rng.Next(4) == 0 ? (ushort)_rng.Next(0x10000) : (ushort)s.Length);
                        b.Unicode(s);
                    }
                });
        }
    }

    // 0xB1: counts that promise more than follows, text entries with lying lengths.
    private byte[] GumpResponse(BotClient bot)
    {
        var w = bot.World;
        var g = w.ActiveGump;
        return Variable(0xB1, b =>
        {
            b.Put32(g != null && _rng.Next(3) > 0 ? g.Serial : Serial(bot));
            b.Put32(g != null && _rng.Next(3) > 0 ? g.GumpId : U32());
            b.Put32(_rng.Next(4) == 0 ? U32() : (uint)_rng.Next(0, 40));
            int switches = Count();
            b.Put32((uint)switches);
            for (int i = 0; i < Math.Min(switches, 64) && _rng.Next(8) > 0; i++)
                b.Put32(_rng.Next(3) == 0 ? U32() : (uint)_rng.Next(0, 40));
            int texts = Count();
            b.Put32((uint)texts);
            for (int i = 0; i < Math.Min(texts, 16) && _rng.Next(8) > 0; i++)
            {
                b.Put16((ushort)_rng.Next(0, 40));
                string s = RandomText(_rng.Next(0, 80));
                b.Put16(_rng.Next(4) == 0 ? (ushort)_rng.Next(0x10000) : (ushort)s.Length);
                b.Unicode(s);
            }
        });
    }

    private byte[] VendorBuy(BotClient bot) => Variable(0x3B, b =>
    {
        b.Put32(KnownMobile(bot));
        b.Put8((byte)_rng.Next(3) == 0 ? (byte)0 : (byte)2);
        int n = _rng.Next(0, 8);
        for (int i = 0; i < n; i++)
        {
            b.Put8((byte)_rng.Next(256));
            b.Put32(Serial(bot));
            b.Put16(Amount());
        }
    });

    private byte[] VendorSell(BotClient bot) => Variable(0x9F, b =>
    {
        b.Put32(KnownMobile(bot));
        int n = Count();
        b.Put16((ushort)n);
        for (int i = 0; i < Math.Min(n, 32) && _rng.Next(8) > 0; i++)
        {
            b.Put32(Serial(bot));
            b.Put16(Amount());
        }
    });

    private byte[] SecureTrade(BotClient bot) => Variable(0x6F, b =>
    {
        b.Put8((byte)_rng.Next(0, 8));
        b.Put32(_rng.Next(2) == 0 && bot.World.TradeContainer != 0 ? bot.World.TradeContainer : Serial(bot));
        b.Put32(_rng.Next(3) == 0 ? U32() : (uint)_rng.Next(0, 3));
        if (_rng.Next(2) == 0) b.Put32(U32());
        if (_rng.Next(2) == 0) b.Put32(U32());
    });

    // 0x12: skill/spell/action/door/spellbook commands with mangled arguments.
    private byte[] TextCommand()
    {
        byte[] types = [0x24, 0x56, 0x27, 0x58, 0x43, 0xC7, 0x6B, 0x00, 0xFF];
        byte type = types[_rng.Next(types.Length)];
        string arg = _rng.Next(5) switch
        {
            0 => $"{_rng.Next(-5, 70)} 0",
            1 => $"{U32()}",
            2 => "-1",
            3 => RandomText(_rng.Next(0, 200)),
            _ => $" {_rng.Next(0, 800)}",
        };
        return Variable(0x12, b => { b.Put8(type); b.Ascii(arg); b.Put8(0); });
    }

    private byte[] UnicodeSpeech() => Variable(0xAD, b =>
    {
        b.Put8((byte)(_rng.Next(3) == 0 ? _rng.Next(256) : 0xC0));
        b.Put16((ushort)_rng.Next(0x10000));
        b.Put16((ushort)_rng.Next(0x10000));
        b.Ascii("ENU"); b.Put8(0);
        if (_rng.Next(2) == 0)
        {
            // Keyword table: a count of 12-bit keywords that may overrun the body.
            int kw = _rng.Next(0, 4096);
            b.Put8((byte)(kw >> 4));
            b.Put8((byte)(((kw & 0xF) << 4) | _rng.Next(16)));
            int extra = _rng.Next(0, 6);
            for (int i = 0; i < extra; i++) b.Put8((byte)_rng.Next(256));
            b.Ascii(Pick(SpeechLines)); b.Put8(0);
        }
        else
        {
            b.Unicode(RandomText(_rng.Next(0, 300)));
            if (_rng.Next(2) == 0) b.Put16(0);
        }
    });

    private byte[] AsciiSpeech() => Variable(0x03, b =>
    {
        b.Put8((byte)_rng.Next(256));
        b.Put16((ushort)_rng.Next(0x10000));
        b.Put16((ushort)_rng.Next(0x10000));
        b.Ascii(RandomText(_rng.Next(0, 300)));
        if (_rng.Next(2) == 0) b.Put8(0);
    });

    private byte[] Tooltips(BotClient bot) => Variable(0xD6, b =>
    {
        int n = _rng.Next(0, 40);
        for (int i = 0; i < n; i++) b.Put32(Serial(bot));
        if (_rng.Next(4) == 0) b.Put16((ushort)_rng.Next(0x10000));
    });

    private byte[] SkillLock() => Variable(0x3A, b =>
    {
        b.Put16((ushort)(_rng.Next(3) == 0 ? _rng.Next(0x10000) : _rng.Next(0, 60)));
        b.Put8((byte)_rng.Next(256));
    });

    private byte[] BookPage(BotClient bot) => Variable(0x66, b =>
    {
        b.Put32(Serial(bot));
        int pages = Count();
        b.Put16((ushort)pages);
        for (int i = 0; i < Math.Min(pages, 8) && _rng.Next(6) > 0; i++)
        {
            b.Put16((ushort)_rng.Next(0x10000));
            int lines = Count();
            b.Put16((ushort)lines);
            for (int l = 0; l < Math.Min(lines, 8) && _rng.Next(6) > 0; l++)
            {
                b.Ascii(RandomText(_rng.Next(0, 80)));
                b.Put8(0);
            }
        }
    });

    private byte[] BookHeader(BotClient bot)
    {
        var p = new byte[FixedLength(0x93)];
        p[0] = 0x93;
        Put32(p, 1, Serial(bot));
        p[5] = (byte)_rng.Next(2); p[6] = (byte)_rng.Next(2);
        Put16(p, 7, (ushort)_rng.Next(0x10000));
        Ascii(p, 9, Math.Min(60, p.Length - 9), RandomText(70));
        if (p.Length > 69) Ascii(p, 69, p.Length - 69, RandomText(40));
        return p;
    }

    private byte[] BulletinBoard(BotClient bot) => Variable(0x71, b =>
    {
        b.Put8((byte)_rng.Next(0, 8));
        b.Put32(Serial(bot));
        b.Put32(Serial(bot));
        if (_rng.Next(2) == 0)
        {
            int len = _rng.Next(0, 300);
            b.Put8((byte)Math.Min(255, len));
            b.Ascii(RandomText(len));
            b.Put8((byte)_rng.Next(256));
            for (int i = 0; i < 4; i++) { b.Put8((byte)_rng.Next(256)); b.Ascii(RandomText(_rng.Next(0, 30))); }
        }
    });

    private byte[] Prompt(BotClient bot)
    {
        bool unicode = _rng.Next(2) == 0;
        return Variable(unicode ? (byte)0xC2 : (byte)0x9A, b =>
        {
            b.Put32(Serial(bot));
            b.Put32(U32());
            b.Put32((uint)_rng.Next(0, 3));
            if (unicode) { b.Ascii("ENU"); b.Put8(0); b.Unicode(RandomText(_rng.Next(0, 200))); }
            else { b.Ascii(RandomText(_rng.Next(0, 200))); b.Put8(0); }
        });
    }

    private byte[] MapPin(BotClient bot) => Fixed(0x56, b =>
    {
        Put32(b, 1, Serial(bot));
        b[5] = (byte)_rng.Next(256); b[6] = (byte)_rng.Next(256);
        Put16(b, 7, (ushort)_rng.Next(0x10000)); Put16(b, 9, (ushort)_rng.Next(0x10000));
    });

    // 0xBF: known sub-commands mostly, unknown ones sometimes; bodies short or random.
    private byte[] Extended(BotClient bot)
    {
        var known = ExtendedCommandRegistry.KnownSubCommands.ToArray();
        ushort sub = _rng.Next(4) == 0 ? (ushort)_rng.Next(0, 0x40) : known[_rng.Next(known.Length)];
        return Variable(0xBF, b =>
        {
            b.Put16(sub);
            int fields = _rng.Next(0, 6);
            for (int i = 0; i < fields; i++)
            {
                switch (_rng.Next(4))
                {
                    case 0: b.Put32(Serial(bot)); break;
                    case 1: b.Put16((ushort)_rng.Next(0x10000)); break;
                    case 2: b.Put8((byte)_rng.Next(256)); break;
                    default: b.Put32(U32()); break;
                }
            }
        });
    }

    // 0xD7: [serial][sub][body] - house design and ability commands.
    private byte[] Encoded(BotClient bot) => Variable(0xD7, b =>
    {
        b.Put32(_rng.Next(2) == 0 ? bot.World.CharUid : Serial(bot));
        b.Put16((ushort)_rng.Next(0, 0x30));
        int fields = _rng.Next(0, 8);
        for (int i = 0; i < fields; i++)
        {
            b.Put8(0x00);
            b.Put32(_rng.Next(2) == 0 ? (uint)_rng.Next(0, 0x4000) : U32());
        }
        if (_rng.Next(2) == 0) b.Put8(0x0A);
    });

    // ---- value pools -------------------------------------------------------------

    /// <summary>Mostly serials the server knows about and this character can (or
    /// nearly can) reach; sometimes the boundaries.</summary>
    private uint Serial(BotClient bot)
    {
        switch (_rng.Next(10))
        {
            case 0: return Pick(BoundarySerials);
            case 1: return bot.World.CharUid;
            case 2: return bot.World.BackpackSerial != 0 ? bot.World.BackpackSerial : bot.World.CharUid;
            case 3: return KnownMobile(bot);
            case 4: return (uint)(0x40000001 + _rng.Next(0, 200_000));
            case 5: return (uint)(1 + _rng.Next(0, 50_000));
            default: return AnySerial(bot);
        }
    }

    private static readonly uint[] BoundarySerials =
        [0, 1, 0xFFFFFFFF, 0x7FFFFFFF, 0x80000000, 0x40000000, 0x3FFFFFFF, 0x40000001, 0xC0000000];

    private uint AnySerial(BotClient bot)
    {
        var w = bot.World;
        lock (w.SyncRoot)
        {
            int total = w.KnownItems.Count + w.Equipment.Count + w.Mobiles.Count;
            if (total == 0) return w.CharUid;
            int pick = _rng.Next(total);
            if (pick < w.KnownItems.Count) return w.KnownItems.Keys.ElementAt(pick);
            pick -= w.KnownItems.Count;
            if (pick < w.Equipment.Count) return w.Equipment.Values.ElementAt(pick).Serial;
            pick -= w.Equipment.Count;
            return w.Mobiles.Keys.ElementAt(pick);
        }
    }

    private uint KnownMobile(BotClient bot)
    {
        var w = bot.World;
        lock (w.SyncRoot)
        {
            if (w.Mobiles.Count == 0) return w.CharUid;
            return w.Mobiles.Keys.ElementAt(_rng.Next(w.Mobiles.Count));
        }
    }

    private ushort Amount() => _rng.Next(5) switch
    {
        0 => 0,
        1 => 0xFFFF,
        2 => 0x8000,
        3 => (ushort)_rng.Next(1, 5),
        _ => (ushort)_rng.Next(0x10000),
    };

    /// <summary>Element counts: small, zero, or far more than the body holds.</summary>
    private int Count() => _rng.Next(5) switch
    {
        0 => 0,
        1 => _rng.Next(1, 4),
        2 => _rng.Next(4, 40),
        3 => 0xFFFF,
        _ => _rng.Next(0, 0x10000),
    };

    private ushort Coord(short near) => _rng.Next(4) switch
    {
        0 => 0xFFFF,
        1 => (ushort)_rng.Next(0x10000),
        2 => 0,
        _ => (ushort)(near + _rng.Next(-3, 4)),
    };

    private sbyte Z() => _rng.Next(3) switch
    {
        0 => sbyte.MinValue,
        1 => sbyte.MaxValue,
        _ => (sbyte)_rng.Next(-10, 40),
    };

    private uint U32() => (uint)_rng.NextInt64(0, 0x1_0000_0000);

    private T Pick<T>(T[] values) => values[_rng.Next(values.Length)];

    private string RandomText(int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(_rng.Next(6) switch
            {
                0 => (char)_rng.Next(0x20, 0x7F),
                1 => (char)_rng.Next(0x80, 0x800),
                2 => "<>[]{}%.\"'\\;=#@"[_rng.Next(15)],
                3 => (char)_rng.Next(0, 0x20),
                4 => (char)_rng.Next(0xD800, 0xE000), // lone surrogates
                _ => (char)_rng.Next('a', 'z' + 1),
            });
        }
        return sb.ToString();
    }

    // ---- framing helpers ---------------------------------------------------------

    private static byte[] Fixed(byte op, Action<byte[]> fill)
    {
        var p = new byte[FixedLength(op)];
        p[0] = op;
        fill(p);
        return p;
    }

    private static byte[] Variable(byte op, Action<Body> fill)
    {
        var b = new Body();
        fill(b);
        return Frame(op, b.Bytes);
    }

    private static byte[] Frame(byte op, ReadOnlySpan<byte> body)
    {
        int len = Math.Min(0xFFFF, 3 + body.Length);
        var p = new byte[len];
        p[0] = op;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(1), (ushort)len);
        body[..(len - 3)].CopyTo(p.AsSpan(3));
        return p;
    }

    private static void Put32(byte[] b, int at, uint v) { if (at + 4 <= b.Length) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v); }
    private static void Put16(byte[] b, int at, ushort v) { if (at + 2 <= b.Length) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v); }

    private static void Ascii(byte[] b, int at, int max, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bytes.AsSpan(0, Math.Min(bytes.Length, Math.Max(0, Math.Min(max, b.Length - at)))).CopyTo(b.AsSpan(at));
    }

    private sealed class Body
    {
        private readonly List<byte> _bytes = new(64);
        public ReadOnlySpan<byte> Bytes => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bytes);
        public void Put8(byte v) => _bytes.Add(v);
        public void Put16(ushort v) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); }
        public void Put32(uint v) { Put16((ushort)(v >> 16)); Put16((ushort)v); }
        public void Ascii(string s) => _bytes.AddRange(Encoding.UTF8.GetBytes(s));
        public void Unicode(string s) => _bytes.AddRange(Encoding.BigEndianUnicode.GetBytes(s));
    }
}
