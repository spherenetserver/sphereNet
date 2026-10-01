using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Guild;

/// <summary>
/// One membership record as a script reference - the port of Source-X CStoneMember
/// as r_GetRef hands it out for MEMBER.n / MEMBERFROMUID.uid on a stone
/// (CItemStone.cpp:214).
///
/// The record is bound to the stone it was reached through, so a character who is a
/// guild member AND a town citizen is two different references: TITLE, PRIV and
/// LOYALTO act on the record of the stone the script asked, never on whichever
/// membership a lookup by character happens to find first. Keys the record does not
/// own fall through to the member's character, as the reference does
/// (CStoneMember::r_WriteVal / r_Verb, CStoneMember.cpp:169/276).
/// </summary>
public sealed class StoneMemberRef : IScriptObj
{
    public GuildDef Stone { get; }
    public GuildMember Member { get; }

    public StoneMemberRef(GuildDef stone, GuildMember member)
    {
        Stone = stone;
        Member = member;
    }

    /// <summary>The record for the nth member of <paramref name="stone"/> (zero-based,
    /// candidates included - every record linked to a character), or null.</summary>
    public static StoneMemberRef? ByIndex(GuildDef stone, int index) =>
        index >= 0 && index < stone.MemberCount ? new StoneMemberRef(stone, stone.Members[index]) : null;

    /// <summary>The record <paramref name="charUid"/> holds on <paramref name="stone"/>, or null.</summary>
    public static StoneMemberRef? ByCharacter(GuildDef stone, Serial charUid) =>
        stone.FindMember(charUid) is { } member ? new StoneMemberRef(stone, member) : null;

    private Character? LinkedCharacter =>
        ObjBase.ResolveWorld?.Invoke()?.FindChar(Member.CharUid);

    public string GetName() => "CStoneMember";

    public bool TryGetProperty(string key, out string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "ISCANDIDATE":
                value = Member.Priv == GuildPriv.Candidate ? "1" : "0";
                return true;
            case "ISMASTER":
                value = Member.Priv == GuildPriv.Master ? "1" : "0";
                return true;
            case "ISMEMBER":
                value = Member.Priv is GuildPriv.Member or GuildPriv.Master ? "1" : "0";
                return true;
            case "ACCOUNTGOLD":
                value = Member.AccountGold.ToString();
                return true;
            case "LOYALTO":
                value = Member.LoyalTo.IsValid ? $"0{Member.LoyalTo.Value:X}" : "0";
                return true;
            case "PRIV":
                value = ((byte)Member.Priv).ToString();
                return true;
            case "PRIVNAME":
                value = GetPrivName(Member.Priv);
                return true;
            case "GUILDTITLE":
                value = Member.Title;
                return true;
            case "SHOWABBREV":
                value = Member.ShowAbbrev ? "1" : "0";
                return true;
        }

        // Anything else is the character's (fNoCallParent unset: pRef->r_WriteVal).
        if (LinkedCharacter is { } ch)
            return ch.TryGetProperty(key, out value);
        value = "";
        return false;
    }

    public bool TrySetProperty(string key, string value)
    {
        if (TryLoadMemberValue(key, value))
            return true;
        return LinkedCharacter is { } ch && ch.TrySetProperty(key, value);
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source) =>
        TryExecuteCommand(key, args, source, out _);

    /// <summary>CStoneMember::r_Verb: the record has no verbs of its own, so a key it
    /// can load is a load; everything else is a verb line on the character.</summary>
    public bool TryExecuteCommand(string key, string args, ITextConsole source, out bool nameOwned)
    {
        nameOwned = true;
        if (TryLoadMemberValue(key, args))
            return true;
        if (LinkedCharacter is { } ch)
            ch.ExecuteVerbLine(key, args, source);
        return true;
    }

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) =>
        TriggerResult.Default;

    /// <summary>CStoneMember::r_LoadVal for a character record. Only the record's own
    /// keys; the title is GUILDTITLE (TITLE is the character's).</summary>
    private bool TryLoadMemberValue(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "ACCOUNTGOLD":
                Member.AccountGold = unchecked((int)ObjBase.ParseHexOrDecUInt(value));
                return true;
            case "LOYALTO":
                ApplyLoyalTo(Stone, Member, value);
                return true;
            case "PRIV":
                Member.Priv = (GuildPriv)(byte)ObjBase.ParseHexOrDecUInt(value);
                return true;
            case "GUILDTITLE":
                Member.Title = value;
                return true;
            case "SHOWABBREV":
                Member.ShowAbbrev = ObjBase.ParseHexOrDecUInt(value) != 0;
                return true;
        }
        return false;
    }

    /// <summary>LOYALTO=&lt;uid&gt; on a membership record: <c>SetLoyalTo(CUID(arg).CharFind())</c>
    /// (CStoneMember.cpp:221). A uid naming no character is a vote for oneself; the
    /// refusal for a candidate voter or a non-member target is spoken to the voter.
    /// Shared by every surface that sets a vote, so all of them validate and hold the
    /// election the same way.</summary>
    public static bool ApplyLoyalTo(GuildDef stone, GuildMember voter, string value)
    {
        var world = ObjBase.ResolveWorld?.Invoke();
        var me = world?.FindChar(voter.CharUid);
        if (me == null)
            return false;   // on shutdown: the reference changes nothing

        uint uid = ObjBase.ParseHexOrDecUInt(value);
        var target = uid != 0 ? world!.FindChar(new Serial(uid)) : null;
        bool ok = stone.SetLoyalTo(voter, target?.Uid ?? Serial.Invalid, out string? refusal);
        if (refusal != null)
            ObjBase.ResolveClientConsole?.Invoke(me)?.SysMessage(refusal);
        return ok;
    }

    /// <summary>CStoneMember::GetPrivName: the STONECONFIG_PRIVNAME_PRIVID-n DEF,
    /// else STONECONFIG_PRIVNAME_PRIVUNK, else empty.</summary>
    private static string GetPrivName(GuildPriv priv)
    {
        var resources = Definitions.DefinitionLoader.StaticResources;
        if (resources == null)
            return "";
        if (resources.TryGetDefValue($"STONECONFIG_PRIVNAME_PRIVID-{(int)priv}", out string name))
            return StripQuotes(name);
        return resources.TryGetDefValue("STONECONFIG_PRIVNAME_PRIVUNK", out string unknown)
            ? StripQuotes(unknown) : "";
    }

    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
}
