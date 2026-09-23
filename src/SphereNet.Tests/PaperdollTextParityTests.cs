using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The paperdoll name line (0x88 text) against Source-X PacketPaperdoll and the
/// helpers it calls: Noto_GetTitle, Noto_GetFameTitle, GetTradeTitle,
/// Skill_GetBest and the SKILLTITLE ranks.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PaperdollTextParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sphnet_pdtext_{Guid.NewGuid():N}");
    private GameWorld _world = null!;

    private const string Script = """
        [DEFNAME skilltitles]
        SKILLTITLE_NEOPHYTE     300
        SKILLTITLE_NOVICE       400
        SKILLTITLE_APPRENTICE   500
        SKILLTITLE_JOURNEYMAN   600
        SKILLTITLE_EXPERT       700
        SKILLTITLE_ADEPT        800
        SKILLTITLE_MASTER       900
        SKILLTITLE_GRANDMASTER  1000
        SKILLTITLE_ELDER        1100
        SKILLTITLE_LEGENDARY    1200

        [NOTOTITLES]
        0
        5000
        <none>
        Glorious,Radiant
        Wicked
        Dread

        [CHARDEF 0400]
        ID=0190
        NAME=#NAMES_HUMANMALE the mage

        [CHARDEF 0401]
        ID=0190
        NAME=shopkeeper

        [EOF]
        """;

    // Run from each test body: the per-test static reset (ResetEngineStatics) runs
    // after the constructor and would wipe anything set up there.
    private void Setup()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "pd.scp");
        File.WriteAllText(file, Script);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = _dir,
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        SetSkill(SkillType.Alchemy, "Alchemist");
        SetSkill(SkillType.Anatomy, "Healer");
        SetSkill(SkillType.Swordsmanship, "Warrior");
        SetSkill(SkillType.Bushido, "Samurai");

        _world = new GameWorld(LoggerFactory.Create(_ => { }));
        _world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;
    }

    public void Dispose()
    {
        Character.ResolveGuildManager = null;
        PaperdollText.NpcNoFameTitle = false;
        GameClient.ServerOptionFlags = OptionFlags.FileCommands | OptionFlags.Buffs;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void SetSkill(SkillType skill, string title)
    {
        var def = new SkillDef(ResourceId.Invalid);
        def.LoadFromKey("TITLE", title);
        DefinitionLoader.SetSkillDef((int)skill, def);
    }

    private Character Player(string name = "Yunus", ushort body = 0x0190)
    {
        var ch = _world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.BodyId = body;
        ch.PrivLevel = PrivLevel.Player;
        ch.SetSkill(SkillType.Swordsmanship, 1000);
        return ch;
    }

    [Fact]
    public void APlainPlayerShowsNameAndBestSkillTitle()
    {
        Setup();
        var ch = Player();
        Assert.Equal("Yunus, Grandmaster Warrior", PaperdollText.Build(ch));

        var parts = PaperdollText.BuildParts(ch);
        Assert.Equal("Yunus", parts.Name);
        Assert.Equal("", parts.NotoTitle);
        Assert.Equal("", parts.FameTitle);
        Assert.Equal("Yunus", parts.FullName);
        Assert.Equal("Grandmaster Warrior", parts.TradeTitle);
    }

    [Fact]
    public void FameAndKarmaAddTheRankArticleAndLordOrLady()
    {
        Setup();
        var ch = Player();
        ch.Fame = 10000;
        Assert.Equal("The Glorious Lord Yunus, Grandmaster Warrior", PaperdollText.Build(ch));
        var parts = PaperdollText.BuildParts(ch);
        Assert.Equal("Glorious", parts.NotoTitle);
        Assert.Equal("Lord", parts.FameTitle);
        Assert.Equal("The Glorious Lord Yunus", parts.FullName);

        var lady = Player("Ayse", body: 0x0191);
        lady.Fame = 10000;
        Assert.Equal("The Radiant Lady Ayse, Grandmaster Warrior", PaperdollText.Build(lady));

        // Fame must exceed 9900 for Lord; 9900 exactly is not enough.
        ch.Fame = 9900;
        Assert.Equal("The Glorious Yunus, Grandmaster Warrior", PaperdollText.Build(ch));

        ch.Fame = 0;
        ch.Karma = -100;
        Assert.Equal("The Wicked Yunus, Grandmaster Warrior", PaperdollText.Build(ch));
    }

    [Fact]
    public void MurdererAndCriminalReplaceTheRank()
    {
        Setup();
        var ch = Player();
        ch.Fame = 10000;
        ch.Kills = (short)(Character.MurderMinCount + 1);
        Assert.Equal("The Murderer Lord Yunus, Grandmaster Warrior", PaperdollText.Build(ch));

        ch.Kills = 0;
        ch.SetStatFlag(StatFlag.Criminal);
        Assert.Equal("The Criminal Lord Yunus, Grandmaster Warrior", PaperdollText.Build(ch));
    }

    [Fact]
    public void TitleWinsOverTheSkillTitleAndTheOptionFlagHidesIt()
    {
        Setup();
        var ch = Player();
        ch.Title = "the Brave";
        Assert.Equal("Yunus, the Brave", PaperdollText.Build(ch));

        GameClient.ServerOptionFlags |= OptionFlags.NoPaperdollTradeTitle;
        Assert.Equal("Yunus", PaperdollText.Build(ch));
    }

    [Fact]
    public void NameTagsOverrideTheirSlots()
    {
        Setup();
        var ch = Player();
        ch.Fame = 10000;
        ch.SetTag("NAME.PREFIX", "Sir ");
        ch.SetTag("NAME.SUFFIX", " the Third");
        ch.SetTag("NAME.ALT", "Hidden");
        Assert.Equal("The Glorious Sir Hidden the Third, Grandmaster Warrior", PaperdollText.Build(ch));
        var parts = PaperdollText.BuildParts(ch);
        Assert.Equal("Hidden", parts.Name);
        Assert.Equal("Sir", parts.FameTitle);
        Assert.Equal("the Third", parts.NameSuffix);
    }

    [Fact]
    public void IncognitoShowsTheBareName()
    {
        Setup();
        var ch = Player();
        ch.Fame = 10000;
        ch.Title = "the Brave";
        ch.SetStatFlag(StatFlag.Incognito);
        Assert.Equal("Yunus", PaperdollText.Build(ch));
    }

    [Fact]
    public void StaffTitleNeedsPrivShow()
    {
        Setup();
        var ch = Player();
        ch.Fame = 10000;
        ch.PrivLevel = PrivLevel.GM;
        Assert.True(ch.PrivShow); // upstream default: PRIV_PRIV_NOSHOW clear
        Assert.Equal("The Glorious GM Yunus, Grandmaster Warrior", PaperdollText.Build(ch));

        ch.PrivLevel = PrivLevel.Counsel;
        Assert.Equal("The Glorious Counselor Yunus, Grandmaster Warrior", PaperdollText.Build(ch));

        ch.PrivShow = false;
        Assert.Equal("The Glorious Lord Yunus, Grandmaster Warrior", PaperdollText.Build(ch));
    }

    [Fact]
    public void GuildAbbreviationAndGuildTitle()
    {
        Setup();
        var ch = Player();
        var guilds = new GuildManager();
        var guild = guilds.CreateGuild(new Serial(0x40000100), "The Order", ch.Uid);
        guild.Abbreviation = "ABC";
        Character.ResolveGuildManager = _ => guilds;

        // No guild title: the trade title follows the abbreviation.
        Assert.Equal("Yunus [ABC], Grandmaster Warrior", PaperdollText.Build(ch));

        var member = guild.FindMember(ch.Uid)!;
        member.Title = "Knight";
        Assert.Equal("Yunus [ABC], Knight", PaperdollText.Build(ch));
        var parts = PaperdollText.BuildParts(ch);
        Assert.Equal("ABC", parts.GuildAbbrev);
        Assert.Equal("Knight", parts.GuildTitle);
        Assert.Equal("Knight", parts.TradeTitle);

        // Without a guild title and with trade titles off the line keeps the
        // comma and nothing after it, as upstream's format string does.
        member.Title = "";
        GameClient.ServerOptionFlags |= OptionFlags.NoPaperdollTradeTitle;
        Assert.Equal("Yunus [ABC], ", PaperdollText.Build(ch));
        GameClient.ServerOptionFlags &= ~OptionFlags.NoPaperdollTradeTitle;

        // Abbreviation hidden, or none set: the plain form.
        member.ShowAbbrev = false;
        Assert.Equal("Yunus, Grandmaster Warrior", PaperdollText.Build(ch));
        member.ShowAbbrev = true;
        guild.Abbreviation = "";
        Assert.Equal("Yunus, Grandmaster Warrior", PaperdollText.Build(ch));
    }

    [Fact]
    public void SkillTitleRanksFollowTheDefThresholds()
    {
        Setup();
        Assert.Equal("", PaperdollText.GetSkillTitle(SkillType.Alchemy, 299));
        Assert.Equal("Neophyte", PaperdollText.GetSkillTitle(SkillType.Alchemy, 300));
        Assert.Equal("Master", PaperdollText.GetSkillTitle(SkillType.Alchemy, 999));
        Assert.Equal("Elder", PaperdollText.GetSkillTitle(SkillType.Alchemy, 1100));
        Assert.Equal("Tatsujin", PaperdollText.GetSkillTitle(SkillType.Bushido, 1100));
        Assert.Equal("Kengo", PaperdollText.GetSkillTitle(SkillType.Bushido, 1200));
        Assert.Equal("Shinobi", PaperdollText.GetSkillTitle(SkillType.Ninjitsu, 1150));

        // Below Neophyte upstream still prints "%s " before the skill title.
        var ch = Player();
        ch.SetSkill(SkillType.Swordsmanship, 0);
        ch.SetSkill(SkillType.Alchemy, 250);
        Assert.Equal("Yunus,  Alchemist", PaperdollText.Build(ch));
    }

    [Fact]
    public void BestSkillTieGoesToTheLaterSkill()
    {
        Setup();
        var ch = Player();
        ch.SetSkill(SkillType.Swordsmanship, 0);
        ch.SetSkill(SkillType.Alchemy, 500);
        ch.SetSkill(SkillType.Anatomy, 500);
        Assert.Equal(SkillType.Anatomy, PaperdollText.GetBestSkill(ch));
        Assert.Equal("Yunus, Apprentice Healer", PaperdollText.Build(ch));
    }

    [Fact]
    public void NpcsGetTheirTradeNameOrNothing()
    {
        Setup();
        var mage = _world.CreateCharacter();
        mage.IsPlayer = false;
        mage.BodyId = 0x0190;
        mage.CharDefIndex = 0x0400;
        mage.Name = "Bob";
        Assert.Equal("Bob, the mage", PaperdollText.Build(mage));

        var keeper = _world.CreateCharacter();
        keeper.IsPlayer = false;
        keeper.BodyId = 0x0190;
        keeper.CharDefIndex = 0x0401;
        keeper.Name = "Al";
        keeper.SetSkill(SkillType.Swordsmanship, 1000);
        Assert.Equal("Al", PaperdollText.Build(keeper)); // NPCs get no skill title

        keeper.Fame = 10000;
        Assert.Equal("The Glorious Lord Al", PaperdollText.Build(keeper));
        PaperdollText.NpcNoFameTitle = true;
        Assert.Equal("The Glorious Al", PaperdollText.Build(keeper));
    }

    [Fact]
    public void TradeNameStripsTheNameListAndThe()
    {
        Setup();
        Assert.Equal("mage", PaperdollText.GetTradeName("#NAMES_HUMANMALE the mage"));
        Assert.Equal("guard", PaperdollText.GetTradeName("#NAMES_HUMANMALE guard"));
        string plain = "orc";
        Assert.Same(plain, PaperdollText.GetTradeName(plain));
        string noSpace = "#NAMES_ORC";
        Assert.Same(noSpace, PaperdollText.GetTradeName(noSpace));
    }
}
