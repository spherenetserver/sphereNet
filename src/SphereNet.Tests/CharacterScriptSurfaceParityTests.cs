using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// The character script surface, read against the reference case by case
/// (CChar::r_WriteVal / r_LoadVal / r_Verb in CChar.cpp). Each test names the
/// upstream case it follows.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharacterScriptSurfaceParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_chs_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Console : ITextConsole
    {
        public readonly List<string> Messages = [];
        public IScriptObj? Src;
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) => Messages.Add(text);
        public IScriptObj? GetSourceChar() => Src;
    }

    private GameWorld _world = null!;
    private ResourceHolder _resources = null!;

    /// <summary>Load a small pack - the [FAME]/[KARMA] tables of the shipped
    /// spheretables.scp plus two probe CHARDEFs - and make a world.</summary>
    private void LoadPack(params string[] extra)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "pack.scp");
        var lines = new List<string>
        {
            "[FAME]", "0,2000,6000", "Anonymous", "Known", "Famous", "",
            "[KARMA]", "-10000,-6000,-2000,2001,6001",
            "Wicked", "Belligerent", "Neutral", "Kindly", "Goodhearted", "",
            "[CHARDEF c_probe_woman]", "ID=0191", "NAME=probe woman", "CAN=MT_FEMALE",
            "ICON=020d2", "SOUNDIDLE=05a", "",
            "[CHARDEF c_probe_man]", "ID=0190", "NAME=probe man", "",
            "[ITEMDEF 0eed]", "NAME=gold", "CAN=0100", "",   // CAN_I_PILE: gold stacks
        };
        lines.AddRange(extra);
        File.WriteAllLines(file, lines);

        using var lf = LoggerFactory.Create(_ => { });
        _resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        _resources.LoadResourceFile(file);
        new DefinitionLoader(_resources, new SpellRegistry()).LoadAll();

        _world = new GameWorld(lf);
        _world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;
    }

    private Character Make(string defName, int x = 1000, int y = 1000)
    {
        var ch = _world.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(ch, defName, _resources));
        ch.Name = defName;
        _world.PlaceCharacter(ch, new Point3D((short)x, (short)y, 0, 0));
        return ch;
    }

    private Item Container(ItemType type = ItemType.Container)
    {
        var c = _world.CreateItem();
        c.BaseId = 0x0E75;
        c.ItemType = type;
        return c;
    }

    private Item Gold(int amount)
    {
        var g = _world.CreateItem();
        g.BaseId = 0x0EED;
        g.ItemType = ItemType.Gold;
        g.Amount = (ushort)amount;
        return g;
    }

    private static string Read(Character ch, string key) =>
        ch.TryGetProperty(key, out string v) ? v : "<unanswered>";

    // ------------------------------------------------------------------ FAME./KARMA.

    [Fact]
    public void KarmaTitle_AnswersWhetherKarmaFallsInTheNamedBand()
    {
        // CHC_KARMA with a '.' (CChar.cpp:2700): the band table walked from the top.
        LoadPack();
        var ch = Make("c_probe_man");

        ch.TrySetProperty("KARMA", "-7000");
        Assert.Equal("1", Read(ch, "KARMA.WICKED"));
        Assert.Equal("0", Read(ch, "KARMA.BELLIGERENT"));

        ch.TrySetProperty("KARMA", "-3000");
        Assert.Equal("0", Read(ch, "KARMA.WICKED"));
        Assert.Equal("1", Read(ch, "KARMA.Belligerent"));

        ch.TrySetProperty("KARMA", "0");
        Assert.Equal("1", Read(ch, "KARMA.NEUTRAL"));
        ch.TrySetProperty("KARMA", "7000");
        Assert.Equal("1", Read(ch, "KARMA.GOODHEARTED"));

        // A name the table does not spell that way never matches (the shipped
        // NPC speech spells it "BELLIGERANT").
        ch.TrySetProperty("KARMA", "-3000");
        Assert.Equal("0", Read(ch, "KARMA.BELLIGERANT_TYPO"));
    }

    [Fact]
    public void FameTitle_AnswersWhetherFameFallsInTheNamedBand()
    {
        // CHC_FAME with a '.' (CChar.cpp:2603).
        LoadPack();
        var ch = Make("c_probe_man");

        ch.TrySetProperty("FAME", "500");
        Assert.Equal("1", Read(ch, "FAME.ANONYMOUS"));
        Assert.Equal("0", Read(ch, "FAME.KNOWN"));
        ch.TrySetProperty("FAME", "2500");
        Assert.Equal("1", Read(ch, "FAME.Known"));
        ch.TrySetProperty("FAME", "9000");
        Assert.Equal("1", Read(ch, "FAME.FAMOUS"));
        Assert.Equal("0", Read(ch, "FAME.ANONYMOUS"));
    }

    [Fact]
    public void FameAndKarmaWrites_AreClampedToTheConfiguredLimits()
    {
        // SetFame / SetKarma (CCharStat.cpp:686/723).
        LoadPack();
        var ch = Make("c_probe_man");

        ch.TrySetProperty("FAME", "-50");
        Assert.Equal(0, ch.Fame);
        ch.TrySetProperty("KARMA", "-20000");
        Assert.Equal(-10000, ch.Karma);
        ch.TrySetProperty("KARMA", "20000");
        Assert.Equal(10000, ch.Karma);
    }

    // ------------------------------------------------------------------ SEX

    [Fact]
    public void SexWithWords_PicksTheWordForTheDefinitionsSex()
    {
        // CHC_SEX (CChar.cpp:2690): <SRC.SEX milord/milady>, split on ':' ',' '/'.
        LoadPack();
        var woman = Make("c_probe_woman");
        var man = Make("c_probe_man");

        Assert.Equal("milady", Read(woman, "SEX milord/milady"));
        Assert.Equal("milord", Read(man, "SEX milord/milady"));
        Assert.Equal("Mi'lady", Read(woman, "SEX Mi'lord:Mi'lady"));
        Assert.Equal("sir", Read(man, "SEX sir,dear lady"));
        Assert.Equal("dear lady", Read(woman, "SEX sir,dear lady"));
        // The bare form keeps answering 1/0 for the top-level <SEX a/b> expression.
        Assert.Equal("1", Read(woman, "SEX"));
        Assert.Equal("0", Read(man, "SEX"));
    }

    // ------------------------------------------------------------------ DISPIDDEC / ICON

    [Fact]
    public void DispIdDec_IsTheChardefTrackingIcon()
    {
        // CHC_DISPIDDEC reads pCharDef->m_trackID (CChar.cpp:2883); a CHARDEF with no
        // ICON has ITEMID_TRACK_WISP.
        LoadPack();
        var woman = Make("c_probe_woman");
        var man = Make("c_probe_man");

        Assert.Equal(0x20D2.ToString(), Read(woman, "DISPIDDEC"));
        Assert.Equal("020D2", Read(woman, "ICON"));
        Assert.Equal(0x2100.ToString(), Read(man, "DISPIDDEC"));
    }

    // ------------------------------------------------------------------ ISONLINE

    [Fact]
    public void IsOnline_AnNpcIsOnlineUnlessParked()
    {
        // CHC_ISONLINE (CChar.cpp:2913).
        LoadPack();
        var npc = Make("c_probe_man");
        Assert.Equal("1", Read(npc, "ISONLINE"));
        npc.SetStatFlag(StatFlag.Ridden);
        Assert.Equal("0", Read(npc, "ISONLINE"));

        var player = Make("c_probe_man");
        player.IsPlayer = true;
        player.IsOnline = false;
        Assert.Equal("0", Read(player, "ISONLINE"));
        player.IsOnline = true;
        Assert.Equal("1", Read(player, "ISONLINE"));
    }

    // ------------------------------------------------------------------ MAXHITS / OMAXHITS

    [Fact]
    public void MaxHits_IsTheAdjustedCeiling_OMaxHitsTheBase()
    {
        // CHC_MAXHITS = Stat_GetMaxAdjusted, CHC_OMAXHITS = Stat_GetMax (CChar.cpp:3186/3189);
        // writing either sets the base (:3665).
        LoadPack();
        var ch = Make("c_probe_man");
        ch.TrySetProperty("OMAXHITS", "80");
        ch.TrySetProperty("MODMAXHITS", "15");

        Assert.Equal("95", Read(ch, "MAXHITS"));
        Assert.Equal("80", Read(ch, "OMAXHITS"));
        Assert.Equal(80, ch.BaseMaxHits);

        ch.TrySetProperty("OMAXMANA", "40");
        Assert.Equal("40", Read(ch, "OMAXMANA"));
        Assert.Equal("40", Read(ch, "MAXMANA"));
    }

    // ------------------------------------------------------------------ GOLD / BANKBALANCE / NEWGOLD

    [Fact]
    public void Gold_CountsPackBankAndNestedBags_ButNotLockedOnes()
    {
        // CHC_GOLD = ContentCount(t_gold) over the whole char (CChar.cpp:3333,
        // CContainer.cpp:385); CHC_BANKBALANCE = the bank's share (:2756).
        LoadPack();
        var ch = Make("c_probe_man");
        var pack = Container();
        ch.Equip(pack, Layer.Pack);
        var bank = Container(ItemType.EqBankBox);
        ch.Equip(bank, Layer.BankBox);
        var bag = Container();
        var locked = Container(ItemType.ContainerLocked);

        pack.TryAddItem(Gold(100));
        pack.TryAddItem(bag);
        bag.TryAddItem(Gold(25));
        pack.TryAddItem(locked);
        locked.TryAddItem(Gold(1000));
        bank.TryAddItem(Gold(50));

        Assert.Equal("175", Read(ch, "GOLD"));
        Assert.Equal("50", Read(ch, "BANKBALANCE"));
    }

    [Fact]
    public void GoldWrite_ConsumesTheDifference_AndNeverWipesThePurse()
    {
        // CHC_GOLD= (CChar.cpp:4057): SRC.GOLD -= 100 takes exactly one hundred.
        LoadPack();
        var ch = Make("c_probe_man");
        var pack = Container();
        ch.Equip(pack, Layer.Pack);
        pack.TryAddItem(Gold(300));

        Assert.True(ch.TrySetProperty("GOLD", "200"));
        Assert.Equal("200", Read(ch, "GOLD"));

        // A larger total adds the difference to the bank box, made if missing.
        Assert.True(ch.TrySetProperty("GOLD", "260"));
        Assert.Equal("260", Read(ch, "GOLD"));
        Assert.Equal("60", Read(ch, "BANKBALANCE"));

        Assert.False(ch.TrySetProperty("GOLD", "-5"));
        Assert.Equal("260", Read(ch, "GOLD"));
    }

    [Fact]
    public void NewGold_PutsNewGoldInThePack()
    {
        // CHV_NEWGOLD (CChar.cpp:4746).
        LoadPack();
        var ch = Make("c_probe_man");
        var pack = Container();
        ch.Equip(pack, Layer.Pack);
        var console = new Console { Src = ch };

        Assert.True(ch.TryExecuteCommand("NEWGOLD", "250", console));
        Assert.Equal("250", Read(ch, "GOLD"));
        // Default: a new pile rather than a merge.
        Assert.True(ch.TryExecuteCommand("NEWGOLD", "10", console));
        Assert.Equal(2, pack.Contents.Count(i => i.ItemType == ItemType.Gold));
        // Pile argument 1 stacks onto gold already there.
        Assert.True(ch.TryExecuteCommand("NEWGOLD", "5,1", console));
        Assert.Equal(2, pack.Contents.Count(i => i.ItemType == ItemType.Gold));
        Assert.Equal("265", Read(ch, "GOLD"));

        Assert.False(ch.TryExecuteCommand("NEWGOLD", "0", console));
    }

    // ------------------------------------------------------------------ AC / AR / HEIGHT

    [Fact]
    public void Ac_IsTheSameKeyAsAr_AndCountsTheChardefArmor()
    {
        // CHC_AR / CHC_AC (CChar.cpp:2749): m_defense + pCharDef->m_defense.
        LoadPack("[CHARDEF c_probe_turtle]", "ID=0190", "NAME=turtle", "ARMOR=30", "");
        var ch = Make("c_probe_turtle");
        Assert.Equal("30", Read(ch, "AR"));
        Assert.Equal(Read(ch, "AR"), Read(ch, "AC"));
    }

    [Fact]
    public void Height_FallsBackThroughDefinitionDefnameAndPlayerHeight()
    {
        // CChar::GetHeight (CChar.cpp:1509).
        LoadPack("[DEFNAME heights]", "height_0191 12", "");
        var man = Make("c_probe_man");
        var woman = Make("c_probe_woman");
        Assert.Equal("16", Read(man, "HEIGHT"));
        Assert.Equal("12", Read(woman, "HEIGHT"));
        man.TrySetProperty("HEIGHT", "20");
        Assert.Equal("20", Read(man, "HEIGHT"));
    }

    // ------------------------------------------------------------------ value normalisation

    [Fact]
    public void ActDiff_IsWrittenAndReadInTenths()
    {
        // CHC_ACTDIFF (CChar.cpp:3052 / 3697).
        LoadPack();
        var ch = Make("c_probe_man");
        ch.TrySetProperty("ACTDIFF", "500");
        Assert.Equal(50, ch.ActDiff);
        Assert.Equal("500", Read(ch, "ACTDIFF"));
        ch.TrySetProperty("ACTDIFF", "-10");
        Assert.Equal(-1, ch.ActDiff);
        Assert.Equal("-1", Read(ch, "ACTDIFF"));
    }

    [Fact]
    public void FontDirAndActionEffect_AreNormalisedLikeTheReference()
    {
        LoadPack();
        var ch = Make("c_probe_man");
        ch.TrySetProperty("FONT", "42");              // CChar.cpp:3883: >= FONT_QTY -> FONT_NORMAL
        Assert.Equal("3", Read(ch, "FONT"));
        ch.TrySetProperty("DIR", "9");                // CChar.cpp:3839: invalid -> DIR_SE
        Assert.Equal(Direction.SouthEast, ch.Direction);
        ch.TrySetProperty("ACTIONEFFECT", "-7");      // CChar.cpp:3730: negative -> -1
        Assert.Equal("-1", Read(ch, "ACTIONEFFECT"));
        ch.TrySetProperty("OSKIN", "0482");           // GetArgWVal: leading 0 is hex
        Assert.Equal(0x482, ch.OSkin);
        ch.TrySetProperty("OBODY", "c_probe_woman");  // CChar.cpp:3983: a CHARDEF by name
        Assert.Equal(0x191, ch.OBody);
    }

    [Fact]
    public void Action_TakesASkillKeyOrASphereNumber()
    {
        // CHC_ACTION = FindSkillKey (CChar.cpp:3720). Classic saves write NPC actions
        // in Sphere hex: ACTION=067 is 0x67, NPCACT_WANDER.
        LoadPack();
        var ch = Make("c_probe_man");
        ch.TrySetProperty("ACTION", "067");
        Assert.Equal((int)NpcAction.Wander, (int)ch.Action);
        ch.TrySetProperty("ACTION", "109");
        Assert.Equal((int)NpcAction.GoHome, (int)ch.Action);
        ch.TrySetProperty("ACTION", "Magery");
        Assert.Equal(SkillType.Magery, ch.Action);
    }

    [Fact]
    public void TitleWithASuffix_IsTheTradeTitle()
    {
        // CHC_TITLE (CChar.cpp:3315): bare TITLE is the field, TITLE.x GetTradeTitle.
        LoadPack();
        var ch = Make("c_probe_man");
        ch.TrySetProperty("TITLE", "the Brave");
        Assert.Equal("the Brave", Read(ch, "TITLE"));
        Assert.Equal("the Brave", Read(ch, "TITLE.TRADE"));
    }

    [Fact]
    public void DirWithAUid_IsTheDirectionTowardThatCharacter()
    {
        // CHC_DIR (CChar.cpp:3109).
        LoadPack();
        var ch = Make("c_probe_man", 1000, 1000);
        var east = Make("c_probe_man", 1010, 1000);
        Assert.Equal(((int)Direction.East).ToString(), Read(ch, $"DIR 0{east.Uid.Value:X}"));
    }

    [Fact]
    public void SkillBest_RanksEverySkill_LaterSkillWinsATie()
    {
        // Skill_GetBest (CCharSkill.cpp:25).
        LoadPack();
        var ch = Make("c_probe_man");
        ch.SetSkill(SkillType.Magery, 900);
        ch.SetSkill(SkillType.Tactics, 700);
        ch.SetSkill(SkillType.Swordsmanship, 700);
        Assert.Equal(((int)SkillType.Magery).ToString(), Read(ch, "SKILLBEST"));
        int later = Math.Max((int)SkillType.Tactics, (int)SkillType.Swordsmanship);
        Assert.Equal(later.ToString(), Read(ch, "SKILLBEST.1"));
    }

    // ------------------------------------------------------------------ verbs

    [Fact]
    public void CriminalVerb_FlagsAndAnExplicitZeroClears()
    {
        // CHV_CRIMINAL (CChar.cpp:4503).
        LoadPack();
        var ch = Make("c_probe_man");
        Assert.True(ch.TryExecuteCommand("CRIMINAL", "", new Console()));
        Assert.True(ch.IsCriminal);
        Assert.True(ch.IsStatFlag(StatFlag.Criminal));

        Assert.True(ch.TrySetProperty("CRIMINAL", "0"));
        Assert.False(ch.IsCriminal);
        Assert.False(ch.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void Face_TurnsTowardAUidOrTheSource_NotADirectionNumber()
    {
        // CHV_FACE (CChar.cpp:4574).
        LoadPack();
        var ch = Make("c_probe_man", 1000, 1000);
        var south = Make("c_probe_man", 1000, 1010);
        var west = Make("c_probe_man", 990, 1000);
        ch.Direction = Direction.North;

        Assert.True(ch.TryExecuteCommand("FACE", $"0{south.Uid.Value:X}", new Console()));
        Assert.Equal(Direction.South, ch.Direction);

        Assert.True(ch.TryExecuteCommand("FACE", "", new Console { Src = west }));
        Assert.Equal(Direction.West, ch.Direction);

        Assert.True(ch.TryExecuteCommand("FACE", "1010,1000", new Console()));
        Assert.Equal(Direction.East, ch.Direction);
    }

    [Fact]
    public void BounceAndDrop_TakeTheItemTheyAreGiven()
    {
        // CHV_BOUNCE = ItemBounce, CHV_DROP = ItemDrop at my feet (CChar.cpp:4483/4539).
        LoadPack();
        var ch = Make("c_probe_man");
        var pack = Container();
        ch.Equip(pack, Layer.Pack);
        var blade = _world.CreateItem();
        blade.ItemType = ItemType.WeaponSword;
        ch.Equip(blade, Layer.OneHanded);

        Assert.True(ch.TryExecuteCommand("BOUNCE", $"0{blade.Uid.Value:X}", new Console()));
        Assert.Null(ch.GetEquippedItem(Layer.OneHanded));
        Assert.Contains(blade, pack.Contents);

        Assert.True(ch.TryExecuteCommand("DROP", $"0{blade.Uid.Value:X}", new Console()));
        Assert.DoesNotContain(blade, pack.Contents);
        Assert.Equal(ch.X, blade.X);
        Assert.Equal(ch.Y, blade.Y);
    }

    [Fact]
    public void Hungry_ReportsTheFoodLevel_AndLeavesItAlone()
    {
        // CHV_HUNGRY (CChar.cpp:4644) only reports.
        LoadPack();
        var ch = Make("c_probe_man");
        ch.Food = 60;
        var self = new Console { Src = ch };
        Assert.True(ch.TryExecuteCommand("HUNGRY", "", self));
        Assert.Equal(60, ch.Food);
        Assert.Single(self.Messages);
        Assert.StartsWith("You are ", self.Messages[0]);

        var other = Make("c_probe_man");
        var watcher = new Console { Src = other };
        Assert.True(ch.TryExecuteCommand("HUNGRY", "", watcher));
        Assert.Contains("looks", watcher.Messages[0]);
    }

    [Fact]
    public void AllSkillsAndSkillGain_TakeSphereNumbersAndSkillNames()
    {
        // CHV_ALLSKILLS GetArgUSVal (CChar.cpp:4449); CHV_SKILLGAIN FindSkillKey (:4889).
        LoadPack();
        var ch = Make("c_probe_man");
        Assert.True(ch.TryExecuteCommand("ALLSKILLS", "0100", new Console()));
        Assert.Equal(0x100, ch.GetSkill(SkillType.Magery));
        Assert.Equal(0x100, ch.GetSkill(SkillType.Tactics));

        Assert.True(ch.TryExecuteCommand("SKILLGAIN", "Magery,50", new Console()));
        Assert.False(ch.TryExecuteCommand("SKILLGAIN", "NotASkill,50", new Console()));
    }

    [Fact]
    public void Bark_PlaysTheCreaturesOwnSound()
    {
        // CHV_BARK is SoundChar (CChar.cpp:4480): type 0 = the CHARDEF's idle sound.
        LoadPack();
        var ch = Make("c_probe_woman");
        var sent = new List<PacketWriter>();
        Character.BroadcastNearby = (_, _, p, _) => sent.Add(p);

        Assert.True(ch.TryExecuteCommand("BARK", "0", new Console()));
        var sound = Assert.IsType<PacketSound>(Assert.Single(sent));
        var id = (ushort)typeof(PacketSound)
            .GetField("_soundId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sound)!;
        Assert.Equal(0x05A, id);
    }
}
