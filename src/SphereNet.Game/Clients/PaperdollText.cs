using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Clients;

/// <summary>
/// The pieces of a paperdoll's name line. <see cref="Text"/> is the whole line; the
/// other fields are its parts, trimmed, for callers that style them separately
/// (the panel and the public paperdoll JSON).
/// </summary>
/// <param name="Name">The character's name (TAG.NAME.ALT when set).</param>
/// <param name="NotoTitle">The karma/fame rank from [NOTOTITLES], or the murderer /
/// criminal title that replaces it. Empty when the rank slot is blank.</param>
/// <param name="FameTitle">TAG.NAME.PREFIX when set, otherwise the staff title
/// (GM, Counselor...) or Lord / Lady.</param>
/// <param name="NameSuffix">TAG.NAME.SUFFIX.</param>
/// <param name="FullName">The name with its rank, fame title and suffix, e.g.
/// "The Glorious Lord Name".</param>
/// <param name="GuildAbbrev">The guild abbreviation, only when the member shows it.</param>
/// <param name="GuildTitle">The member's guild title, only alongside the abbreviation.</param>
/// <param name="TradeTitle">What follows the comma: the guild title, or the TITLE /
/// best-skill title.</param>
/// <param name="Text">The line exactly as the 0x88 packet carries it.</param>
public sealed record PaperdollTextParts(
    string Name,
    string NotoTitle,
    string FameTitle,
    string NameSuffix,
    string FullName,
    string GuildAbbrev,
    string GuildTitle,
    string TradeTitle,
    string Text);

/// <summary>
/// The name line of a character's paperdoll (the 60-character text field of the
/// 0x88 OpenPaperdoll packet). One builder, so the game client, the admin panel
/// and the public paperdoll endpoint all show the same string.
///
/// Source-X PacketPaperdoll (network/send.cpp): an incognito character shows its
/// bare name; otherwise the line is
///   Noto_GetTitle [ABBR], guild title-or-trade title   (a guild member showing the abbreviation)
///   Noto_GetTitle, trade title                          (otherwise, when there is one)
///   Noto_GetTitle
/// where the trade title is suppressed by OF_NoPaperdollTradeTitle. The helpers are
/// ported below: Noto_GetTitle / Noto_GetFameTitle (CCharNotoriety.cpp),
/// GetTradeTitle (CCharStatus.cpp), Skill_GetBest (CCharSkill.cpp) and
/// CExprGlobals::SkillTitle (CExpression.cpp).
/// </summary>
public static class PaperdollText
{
    /// <summary>Source-X NPCNOFAMETITLE: NPCs never get the Lord/Lady prefix.</summary>
    public static bool NpcNoFameTitle { get; set; }

    public static string Build(Character ch) => BuildParts(ch).Text;

    public static PaperdollTextParts BuildParts(Character ch)
    {
        string name = GetName(ch);
        if (ch.IsStatFlag(StatFlag.Incognito))
            return new PaperdollTextParts(name, "", "", "", name, "", "", "", name);

        string notoTitle = GetNotoRankTitle(ch);
        string fameTitle = GetTag(ch, "NAME.PREFIX");
        if (fameTitle.Length == 0)
            fameTitle = GetFameTitle(ch);
        string suffix = GetTag(ch, "NAME.SUFFIX");
        bool female = ch.IsFemale;

        // Noto_GetTitle: "%s%s%s%s%s%s" = article, rank, space, fame title, name, suffix.
        string fullName = string.Concat(
            notoTitle.Length > 0
                ? ServerMessages.Get(female ? Msg.TitleArticleFemale : Msg.TitleArticleMale)
                : "",
            notoTitle,
            notoTitle.Length > 0 ? " " : "",
            fameTitle,
            name,
            suffix);

        bool tradeTitleAllowed = (GameClient.ServerOptionFlags & OptionFlags.NoPaperdollTradeTitle) == 0;
        string text = "";
        string abbrev = "", guildTitle = "", after = "";

        var guildMember = FindGuildMember(ch, out string guildAbbrev);
        if (guildMember != null && guildMember.ShowAbbrev && guildAbbrev.Length > 0)
        {
            abbrev = guildAbbrev;
            guildTitle = guildMember.Title;
            after = guildTitle.Length > 0 ? guildTitle : tradeTitleAllowed ? GetTradeTitle(ch) : "";
            text = $"{fullName} [{abbrev}], {after}";
        }

        if (text.Length == 0)
        {
            after = tradeTitleAllowed ? GetTradeTitle(ch) : "";
            text = after.Length > 0 ? $"{fullName}, {after}" : fullName;
        }

        return new PaperdollTextParts(
            name.Trim(), notoTitle.Trim(), fameTitle.Trim(), suffix.Trim(), fullName.Trim(),
            abbrev, guildTitle.Trim(), after.Trim(), text);
    }

    /// <summary>CChar::GetName(true): TAG.NAME.ALT overrides the name.</summary>
    private static string GetName(Character ch)
    {
        string alt = GetTag(ch, "NAME.ALT");
        return alt.Length > 0 ? alt : ch.GetName();
    }

    private static string GetTag(Character ch, string key) =>
        ch.TryGetTag(key, out var v) && v != null ? v : "";

    /// <summary>The rank part of Noto_GetTitle: Murderer, Criminal, or the
    /// [NOTOTITLES] slot for the karma/fame level.</summary>
    private static string GetNotoRankTitle(Character ch)
    {
        if (ch.IsMurderer)
            return ServerMessages.Get(Msg.TitleMurderer);
        if (ch.IsStatFlag(StatFlag.Criminal) || ch.IsCriminal)
            return ServerMessages.Get(Msg.TitleCriminal);
        return DefinitionLoader.StaticResources?.GetNotoTitle(ch.Karma, ch.Fame, ch.IsFemale) ?? "";
    }

    /// <summary>CChar::Noto_GetFameTitle. The staff titles need PRIVSHOW; the
    /// GM-and-above ones also need GM mode, which in this engine is the GM
    /// privilege level itself.</summary>
    internal static string GetFameTitle(Character ch)
    {
        if (ch.IsStatFlag(StatFlag.Incognito) || ch.IsStatFlag(StatFlag.Polymorph))
            return "";

        if (ch.PrivShow)
        {
            var plevel = ch.PrivLevel;
            if (plevel >= PrivLevel.GM)
            {
                switch (plevel)
                {
                    case PrivLevel.Owner: return ServerMessages.Get(Msg.TitleOwner);
                    case PrivLevel.Admin: return ServerMessages.Get(Msg.TitleAdmin);
                    case PrivLevel.Dev: return ServerMessages.Get(Msg.TitleDev);
                    case PrivLevel.GM: return ServerMessages.Get(Msg.TitleGm);
                }
            }
            switch (plevel)
            {
                case PrivLevel.Seer: return ServerMessages.Get(Msg.TitleSeer);
                case PrivLevel.Counsel: return ServerMessages.Get(Msg.TitleCounsel);
            }
        }

        if (ch.Fame > 9900 && (ch.IsPlayer || !NpcNoFameTitle))
            return ServerMessages.Get(ch.IsFemale ? Msg.TitleLady : Msg.TitleLord);
        return "";
    }

    /// <summary>CChar::GetTradeTitle: TITLE wins; a non-playable body, an incognito
    /// character or an NPC whose CHARDEF name carries a trade part ("#NAMES the
    /// mage") gets "the &lt;trade&gt;"; a player gets the title of their best skill.</summary>
    internal static string GetTradeTitle(Character ch)
    {
        if (!string.IsNullOrEmpty(ch.Title))
            return ch.Title;

        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        string typeName = def?.Name ?? "";
        string tradeName = GetTradeName(typeName);
        bool npc = !ch.IsPlayer;

        if (ch.IsStatFlag(StatFlag.Incognito) || !IsPlayableBody(ch.BodyId) ||
            (npc && !ReferenceEquals(typeName, tradeName)))
        {
            if (string.IsNullOrEmpty(ch.Name))
                return "";
            string article = ServerMessages.Get(ch.IsFemale ? Msg.TradetitleArticleFemale : Msg.TradetitleArticleMale);
            return $"{article} {tradeName}";
        }

        if (npc)
            return "";

        var best = GetBestSkill(ch);
        ushort value = ch.GetSkill(best);
        string skillClassTitle = DefinitionLoader.GetSkillDef((int)best)?.Title ?? "";
        return $"{GetSkillTitle(best, value)} {skillClassTitle}";
    }

    /// <summary>CCharBase::GetTradeName: a NAME starting with '#' drops its first
    /// word (the name-list reference) and a leading "the ". The same instance comes
    /// back when there is nothing to drop, which is how GetTradeTitle tells the two
    /// apart.</summary>
    internal static string GetTradeName(string typeName)
    {
        if (typeName.Length == 0 || typeName[0] != '#')
            return typeName;
        int space = typeName.IndexOf(' ');
        if (space < 0)
            return typeName;
        string rest = typeName[(space + 1)..];
        return rest.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? rest[4..] : rest;
    }

    /// <summary>CChar::IsPlayableCharacter: human (incl. the GM robe body), elf or
    /// gargoyle, ghosts excluded.</summary>
    internal static bool IsPlayableBody(ushort body) => body is
        0x0190 or 0x0191 or 0x03DB or 0x025D or 0x025E or 0x029A or 0x029B;

    /// <summary>CChar::Skill_GetBest(0): the highest base value among the defined
    /// skills; on a tie the later skill wins (the insert test is &gt;=).</summary>
    internal static SkillType GetBestSkill(Character ch)
    {
        int best = 0;
        ushort bestValue = 0;
        bool any = false;
        for (int i = 0; i < (int)SkillType.Qty; i++)
        {
            if (DefinitionLoader.GetSkillDef(i) == null)
                continue;
            ushort v = ch.GetSkill((SkillType)i);
            if (!any || v >= bestValue)
            {
                best = i;
                bestValue = v;
                any = true;
            }
        }
        return (SkillType)best;
    }

    private static readonly (string Msg, string Def)[] GenericTitles =
    [
        (Msg.SkilltitleNeophyte, "SKILLTITLE_NEOPHYTE"),
        (Msg.SkilltitleNovice, "SKILLTITLE_NOVICE"),
        (Msg.SkilltitleApprentice, "SKILLTITLE_APPRENTICE"),
        (Msg.SkilltitleJourneyman, "SKILLTITLE_JOURNEYMAN"),
        (Msg.SkilltitleExpert, "SKILLTITLE_EXPERT"),
        (Msg.SkilltitleAdept, "SKILLTITLE_ADEPT"),
        (Msg.SkilltitleMaster, "SKILLTITLE_MASTER"),
        (Msg.SkilltitleGrandmaster, "SKILLTITLE_GRANDMASTER"),
        (Msg.SkilltitleElder, "SKILLTITLE_ELDER"),
        (Msg.SkilltitleLegendary, "SKILLTITLE_LEGENDARY"),
    ];

    /// <summary>CExprGlobals::SkillTitle + CValStr::FindName: the last rank whose
    /// DEF SKILLTITLE_* threshold the value reaches ("" below Neophyte). Bushido and
    /// Ninjitsu have their own Elder / Legendary names.</summary>
    internal static string GetSkillTitle(SkillType skill, int value)
    {
        var resources = DefinitionLoader.StaticResources;
        string title = "";
        for (int i = 0; i < GenericTitles.Length; i++)
        {
            long threshold = resources != null &&
                             resources.TryResolveDefNameValue(GenericTitles[i].Def, out long t) ? t : 0;
            if (value < threshold)
                break;
            string msg = GenericTitles[i].Msg;
            if (skill == SkillType.Bushido)
                msg = msg == Msg.SkilltitleElder ? Msg.SkilltitleElderBushido
                    : msg == Msg.SkilltitleLegendary ? Msg.SkilltitleLegendaryBushido : msg;
            else if (skill == SkillType.Ninjitsu)
                msg = msg == Msg.SkilltitleElder ? Msg.SkilltitleElderNinjitsu
                    : msg == Msg.SkilltitleLegendary ? Msg.SkilltitleLegendaryNinjitsu : msg;
            title = ServerMessages.Get(msg);
        }
        return title;
    }

    /// <summary>CChar::Guild_FindMember(MEMORY_GUILD): the member record of the
    /// character's guild (not a town stone), candidates included, as the guild
    /// memory is added for every record.</summary>
    private static Guild.GuildMember? FindGuildMember(Character ch, out string abbrev)
    {
        abbrev = "";
        var guild = Character.ResolveGuildManager?.Invoke(ch.Uid)?.FindGuildRecordFor(ch.Uid, townStones: false);
        var member = guild?.FindMember(ch.Uid);
        if (member == null || member.Priv is Guild.GuildPriv.Enemy or Guild.GuildPriv.Ally)
            return null;
        abbrev = guild!.Abbreviation ?? "";
        return member;
    }
}
