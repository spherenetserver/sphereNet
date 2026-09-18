using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Every fixed-length packet we emit must be the length the client reads it as.
///
/// The client parses a fixed-length opcode by a table, not by anything in the packet.
/// A server that writes fewer bytes than the table says does not send one malformed
/// packet - it moves every byte after it, and the stream is read at the wrong offset
/// from there on. That is not a cosmetic bug: the connection stops working and only a
/// reconnect clears it.
///
/// It happened. A refused drop was sent as 0x28 with two bytes; 0x28 is five in the
/// client's table and is never adjusted for any client version. A player whose bank
/// deposit was refused saw the gold vanish and then could not move.
///
/// The table below is the client's own (ClassicUO PacketsTable.cs), for the opcodes
/// this server emits at a fixed length. Three of them are deliberately the MODERN
/// length rather than the legacy one, and the client calibrates its table to match on
/// the same version boundaries - those are listed with the version that moves them.
/// </summary>
public sealed class PacketLengthGuardrailTests
{
    private readonly ITestOutputHelper _out;
    public PacketLengthGuardrailTests(ITestOutputHelper output) => _out = output;

    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir;
    }

    /// <summary>
    /// opcode -> the lengths the client reads it as. Three opcodes have TWO, because
    /// the client calibrates its table on a version boundary and this server writes
    /// whichever form the connected client asked for; both are listed with the version
    /// that moves them. 0 means variable-length, which the other test covers.
    /// </summary>
    private static readonly Dictionary<int, int[]> ClientLength = new()
    {
        [0x0B] = [7],    // damage (5.0.0a+; 0x10A before that)
        [0x11] = [0],    // variable
        [0x1A] = [0],
        [0x1D] = [5],
        [0x20] = [19],
        [0x21] = [8],
        [0x22] = [3],
        [0x23] = [26],
        [0x24] = [7, 9],    // 7.0.9+ adds the type word (7 before)
        [0x25] = [20, 21],   // 6.0.1.7+ adds the grid byte (20 before)
        [0x26] = [5],
        [0x27] = [2],
        [0x28] = [5],
        [0x29] = [1],
        [0x2C] = [2],
        [0x2D] = [25],
        [0x2E] = [15],
        [0x2F] = [10],
        [0x55] = [1],
        [0x72] = [5],
        [0x73] = [2],
        [0x77] = [17],
        [0x7F] = [1],
        [0x88] = [66],
        [0x8A] = [11],
        [0x90] = [19],
        [0x9E] = [0],
        [0xA1] = [9],
        [0xA2] = [9],
        [0xA3] = [9],
        [0xAA] = [5],
        [0xB9] = [3, 5],    // 6.0.14+ widens the flags (3 before)
        [0xBC] = [3],
        [0xC1] = [0],
        [0xDB] = [0],
        [0xDC] = [9],
        [0xE2] = [10],
    };

    [Fact]
    public void AFixedLengthPacketIsTheLengthTheClientReads()
    {
        var root = RepoRoot();
        if (Gate.MissingValue(_out, "engine source", root)) return;

        string dir = Path.Combine(root!.FullName, "src", "SphereNet.Network", "Packets", "Outgoing");
        if (Gate.Missing(_out, "engine source", !Directory.Exists(dir))) return;

        var classes = new Regex(
            @"class\s+(\w+)\s*:\s*PacketWriter\b(.*?)(?=\n(?:public|internal)\s+(?:sealed\s+)?class|\Z)",
            RegexOptions.Singleline);
        var opcode = new Regex(@":\s*base\(\s*0x([0-9A-Fa-f]{2})\s*\)");
        var fixedSize = new Regex(@"CreateFixed\(\s*(\d+)\s*\)");

        var offenders = new List<string>();
        int checkedCount = 0;

        foreach (string path in Directory.EnumerateFiles(dir, "*.cs"))
        {
            string src = File.ReadAllText(path);
            foreach (Match cls in classes.Matches(src))
            {
                string body = cls.Groups[2].Value;
                var op = opcode.Match(body);
                if (!op.Success) continue;

                int id = Convert.ToInt32(op.Groups[1].Value, 16);
                if (!ClientLength.TryGetValue(id, out int[]? want) || want[0] == 0) continue;

                foreach (int size in fixedSize.Matches(body).Select(m => int.Parse(m.Groups[1].Value)).Distinct())
                {
                    checkedCount++;
                    if (!want.Contains(size))
                        offenders.Add($"0x{id:X2} {cls.Groups[1].Value}: writes {size}, " +
                                      $"client reads {string.Join(" or ", want)}");
                }
            }
        }

        _out.WriteLine($"{checkedCount} fixed-length packet bodies checked against the client's table");
        Assert.True(checkedCount > 10, "the scan found almost nothing - the shape it looks for has moved");
        Assert.True(offenders.Count == 0,
            "these would move every byte after them:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>A variable-length packet declares its own length in bytes 1-2, and the
    /// class that builds one has to fill that in. Nothing forces it to, so this is the
    /// thing that checks: a class that forgets sends a length of zero.</summary>
    [Fact]
    public void AVariableLengthPacketFillsInItsOwnLength()
    {
        var root = RepoRoot();
        if (Gate.MissingValue(_out, "engine source", root)) return;

        string dir = Path.Combine(root!.FullName, "src", "SphereNet.Network", "Packets", "Outgoing");
        if (Gate.Missing(_out, "engine source", !Directory.Exists(dir))) return;

        var classes = new Regex(
            @"class\s+(\w+)\s*:\s*PacketWriter\b(.*?)(?=\n(?:public|internal)\s+(?:sealed\s+)?class|\Z)",
            RegexOptions.Singleline);

        var offenders = new List<string>();
        int checkedCount = 0;

        foreach (string path in Directory.EnumerateFiles(dir, "*.cs"))
        {
            string src = File.ReadAllText(path);
            foreach (Match cls in classes.Matches(src))
            {
                string body = cls.Groups[2].Value;
                if (!body.Contains("CreateVariable")) continue;
                checkedCount++;
                if (!body.Contains("WriteLengthAt") && !body.Contains("RejectOversize"))
                    offenders.Add($"{cls.Groups[1].Value} ({Path.GetFileName(path)})");
            }
        }

        _out.WriteLine($"{checkedCount} variable-length packet classes checked");
        Assert.True(checkedCount > 10, "the scan found almost nothing - the shape it looks for has moved");
        Assert.True(offenders.Count == 0,
            "these declare a length of zero:\n  " + string.Join("\n  ", offenders));
    }
}
