using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;

namespace SphereNet.Game.World;

/// <summary>
/// One entry in the GM page queue, and a script object in its own right
/// (Source-X CGMPage, which derives from CScriptObj rather than CObjBase - it has
/// no uid and does not live in the world, but a script can still read and write it).
///
/// It was a five-field value record before, holding an account name, free text, a
/// handler NAME and a status string. The reference's own GM page dialog needs
/// things that record could not hold: CHARUID to find the player who paged, P to
/// travel to where they paged from, TIME as the age of the page, and HANDLED as the
/// UID of the staff member who took it - comparable to SRC and assignable, which is
/// how one GM claims a page and another is told it is taken.
/// </summary>
public sealed class GmPage : IScriptObj
{
    /// <summary>Name of the account that paged (CGMPage::GetName).</summary>
    public string Account { get; set; } = "";

    /// <summary>The character who paged. A page whose player has since been deleted
    /// keeps its text and its place in the queue, with nobody behind it.</summary>
    public Serial CharUid { get; set; } = Serial.Invalid;

    /// <summary>Where the page was made from, which is where a GM travels to.</summary>
    public Point3D Position { get; set; }

    public string Reason { get; set; } = "";

    /// <summary>When the page was made, in unix seconds. The script-facing TIME key
    /// answers the AGE in seconds instead, which is what the reference means by it
    /// and what a queue dialog renders; the save keeps the absolute stamp so a
    /// restart does not reset every page's age.</summary>
    public long Created { get; set; }

    /// <summary>The staff character handling this page, invalid when nobody is
    /// (CGMPage::m_pClientHandling). Upstream holds the live client; a serial
    /// survives a save and a disconnect, and resolves to the same answer.</summary>
    public Serial Handler { get; set; } = Serial.Invalid;

    /// <summary>This shard's own queue state, kept because the save format has
    /// always carried it. Not a reference concept - there, a page is handled or it
    /// is not, and HANDLED says which.</summary>
    public string Status { get; set; } = "open";

    /// <summary>Unix seconds now, overridable so a test can age a page without
    /// waiting.</summary>
    public static Func<long>? NowSeconds;

    internal static long Now() => NowSeconds?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public string GetName() => Account;

    /// <summary>How old this page is, in seconds (CGMPage GC_TIME, which formats
    /// the difference between now and when the page was made).</summary>
    public long AgeSeconds => Math.Max(0, Now() - Created);

    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        switch (key.ToUpperInvariant())
        {
            case "ACCOUNT": value = Account; return true;
            case "CHARUID": value = FormatSerial(CharUid); return true;
            // The UID of the staff character handling it, or 0. A dialog compares
            // this against SRC, so it has to be a uid and not a name.
            case "HANDLED": value = FormatSerial(Handler); return true;
            case "P": value = Position.ToString(); return true;
            case "P.X": value = Position.X.ToString(); return true;
            case "P.Y": value = Position.Y.ToString(); return true;
            case "P.Z": value = Position.Z.ToString(); return true;
            case "P.MAP": value = Position.Map.ToString(); return true;
            case "REASON": value = Reason; return true;
            case "TIME": value = AgeSeconds.ToString(); return true;
            // This shard's own two, which the save has always carried.
            case "HANDLER": value = Handler.IsValid ? FormatSerial(Handler) : ""; return true;
            case "STATUS": value = Status; return true;
            case "CREATED": value = Created.ToString(); return true;
            default: return false;
        }
    }

    public bool TrySetProperty(string key, string value)
    {
        string arg = (value ?? "").Trim();
        switch (key.ToUpperInvariant())
        {
            case "ACCOUNT": Account = arg; return true;
            case "CHARUID": CharUid = ParseSerial(arg); return true;
            case "HANDLED": Handler = ParseSerial(arg); return true;
            case "P":
                if (Point3D.TryParse(arg, out var pt)) Position = pt;
                return true;
            case "REASON": Reason = arg; return true;
            // Written as an AGE, the way the reference reads it back
            // (CGMPage::r_LoadVal GC_TIME subtracts it from now).
            case "TIME":
                if (long.TryParse(arg, out long age)) Created = Now() - age;
                return true;
            case "CREATED":
                if (long.TryParse(arg, out long created)) Created = created;
                return true;
            case "STATUS": Status = arg; return true;
            default: return false;
        }
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source)
        => TryExecuteCommand(key, args, source, out _);

    public bool TryExecuteCommand(string key, string args, ITextConsole source, out bool nameOwned)
    {
        nameOwned = false;
        string upper = key.ToUpperInvariant();
        // DELETE is upstream's own verb-shaped key (GC_DELETE removes the page from
        // the world's list). The removal itself belongs to the world, which owns the
        // list, so the page only reports that the name is its own.
        if (upper is "ACCOUNT" or "CHARUID" or "HANDLED" or "P" or "REASON" or "TIME"
            or "STATUS" or "CREATED")
        {
            nameOwned = true;
            return TrySetProperty(upper, args);
        }
        if (upper == "DELETE")
        {
            nameOwned = true;
            return false;   // the caller removes it; see GameWorld.RemoveGmPage
        }
        return false;
    }

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
        => TriggerResult.Default;

    private static string FormatSerial(Serial s) => s.IsValid ? $"0{s.Value:X}" : "0";

    private static Serial ParseSerial(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Serial.Invalid;
        string t = text.Trim();
        uint value;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uint.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        else if (t.Length > 1 && t[0] == '0')
            uint.TryParse(t[1..], System.Globalization.NumberStyles.HexNumber, null, out value);
        else
            uint.TryParse(t, out value);
        return value == 0 ? Serial.Invalid : new Serial(value);
    }
}
