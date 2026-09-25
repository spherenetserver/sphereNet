using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The item keywords whose NAME was answered but whose MEANING differed from the
/// reference (CItem.cpp r_WriteVal / r_LoadVal / r_Verb, CItemBase.cpp): durability
/// halves, Sphere-number arguments, the base-def key families, the container keys,
/// the spellbook bit, and the BOUNCE / CONTCONSUME / DECAY verbs.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemScriptSurfaceParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_itemsurf_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private void LoadDefs(params string[] lines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllLines(file, lines);
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private static Item Loose(GameWorld w)
    {
        var it = w.CreateItem();
        w.PlaceItem(it, new Point3D(100, 100, 0, 0));
        return it;
    }

    private static string Get(ObjBase o, string key)
    {
        Assert.True(o.TryGetProperty(key, out string v), $"{key} did not answer");
        return v;
    }

    private sealed class CharConsole(Character ch) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => ch.Name;
        public IScriptObj? GetSourceChar() => ch;
    }

    private sealed class ServerConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "server";
    }

    private static (Character ch, Item pack) Wearer(GameWorld w)
    {
        var ch = w.CreateCharacter();
        w.PlaceCharacter(ch, new Point3D(200, 200, 0, 0));
        var pack = w.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return (ch, pack);
    }

    // ---------------- durability ----------------

    [Fact]
    public void Hitpoints_SetsTheCurrentAndTheMaximum()
    {
        var w = MakeWorld();
        var it = Loose(w);
        Assert.True(it.TrySetProperty("HITPOINTS", "40"));
        Assert.Equal((40, 40), (it.HitsCur, it.HitsMax));
    }

    [Fact]
    public void Hits_FillsAMaximumThatWasNeverSet_AndLeavesASetOneAlone()
    {
        var w = MakeWorld();
        var fresh = Loose(w);
        fresh.TrySetProperty("HITS", "30");
        Assert.Equal((30, 30), (fresh.HitsCur, fresh.HitsMax));

        var worn = Loose(w);
        worn.TrySetProperty("MAXHITS", "50");
        worn.TrySetProperty("HITS", "20");
        Assert.Equal((20, 50), (worn.HitsCur, worn.HitsMax));
        Assert.Equal("40", Get(worn, "REPAIRPERCENT"));

        var none = Loose(w);
        Assert.Equal("100", Get(none, "REPAIRPERCENT"));   // no maximum at all
    }

    // ---------------- Sphere-number arguments ----------------

    [Fact]
    public void HalfWordKeys_ReadSphereNumbers()
    {
        var w = MakeWorld();
        var it = Loose(w);
        it.TrySetProperty("MORE1H", "0100");   // leading zero = hex
        it.TrySetProperty("MORE2L", "1+2");
        Assert.Equal("256", Get(it, "MORE1H"));
        Assert.Equal("3", Get(it, "MORE2L"));
        it.TrySetProperty("USESCUR", "0A");
        Assert.Equal("10", Get(it, "USESCUR"));
        it.TrySetProperty("MOREM", "02");
        Assert.Equal("2", Get(it, "MOREM"));
    }

    [Fact]
    public void UsesMax_StartsAFreshItemFull_ButKeepsACurrentValue()
    {
        var w = MakeWorld();
        var fresh = Loose(w);
        Assert.True(fresh.TrySetProperty("USESMAX", "0A"));
        Assert.Equal(("10", "10"), (Get(fresh, "USESMAX"), Get(fresh, "USESCUR")));

        var used = Loose(w);
        used.TrySetProperty("USESCUR", "3");
        used.TrySetProperty("USESMAX", "20");
        Assert.Equal(("20", "3"), (Get(used, "USESMAX"), Get(used, "USESCUR")));
    }

    [Fact]
    public void Consume_CountIsASphereNumber()
    {
        var w = MakeWorld();
        var stack = Loose(w);
        stack.Amount = 20;
        Assert.True(stack.TryExecuteCommand("CONSUME", "010", new ServerConsole()));   // 16
        Assert.Equal(4, stack.Amount);
    }

    // ---------------- DISPIDDEC / AC / AMOUNT ----------------

    [Fact]
    public void DispIdDec_IsTheDisplayId_AndACoinPilesGraphic()
    {
        var w = MakeWorld();
        var it = Loose(w);
        it.BaseId = 0x0E75;
        it.TrySetProperty("DISPID", "0E76");
        Assert.Equal(0x0E76.ToString(), Get(it, "DISPIDDEC"));

        var coins = Loose(w);
        coins.BaseId = 0x0EEA;
        coins.ItemType = ItemType.Coin;
        coins.Amount = 1;
        Assert.Equal(0x0EEA.ToString(), Get(coins, "DISPIDDEC"));
        coins.Amount = 3;
        Assert.Equal(0x0EEB.ToString(), Get(coins, "DISPIDDEC"));
        coins.Amount = 50;
        Assert.Equal(0x0EEC.ToString(), Get(coins, "DISPIDDEC"));
    }

    [Fact]
    public void AcAndAr_AreTheArmourDefense_AndZeroOffArmour()
    {
        var w = MakeWorld();
        var plate = Loose(w);
        plate.ItemType = ItemType.Armor;
        plate.TrySetProperty("ARMOR", "12");
        Assert.Equal(("12", "12"), (Get(plate, "AC"), Get(plate, "AR")));

        var rock = Loose(w);
        rock.TrySetProperty("ARMOR", "12");
        Assert.Equal("0", Get(rock, "AR"));
    }

    [Fact]
    public void Amount_OnASpawner_IsItsCapacity()
    {
        var w = MakeWorld();
        var spawner = Loose(w);
        spawner.SpawnChar = new SpawnComponent(spawner, w) { MaxCount = 1 };
        spawner.SpawnChar.MaxCount = 7;
        Assert.Equal("7", Get(spawner, "AMOUNT"));
    }

    // ---------------- attribute flag keys ----------------

    [Fact]
    public void NoDropNoTradeQuestItem_SetClearAndReadTheBit()
    {
        var w = MakeWorld();
        var it = Loose(w);
        Assert.Equal("0", Get(it, "NODROP"));
        Assert.True(it.TrySetProperty("NODROP", ""));                 // no argument sets it
        Assert.Equal(0x200000.ToString(), Get(it, "NODROP"));
        it.TrySetProperty("NOTRADE", "1");
        it.TrySetProperty("QUESTITEM", "1");
        Assert.Equal(0x400000.ToString(), Get(it, "NOTRADE"));
        Assert.Equal(0x80000.ToString(), Get(it, "QUESTITEM"));
        it.TrySetProperty("NODROP", "0");
        Assert.Equal("0", Get(it, "NODROP"));
        Assert.Equal(0x400000.ToString(), Get(it, "NOTRADE"));      // the others untouched
    }

    // ---------------- container keys ----------------

    [Fact]
    public void TopContGridAndPoint_AnswerForAContainedItem()
    {
        var w = MakeWorld();
        var (ch, pack) = Wearer(w);
        var bag = w.CreateItem();
        bag.ItemType = ItemType.Container;
        pack.AddItem(bag);
        var gem = w.CreateItem();
        bag.AddItem(gem);
        gem.Position = new Point3D(44, 55, 0, 0);
        gem.ContainerGridIndex = 3;

        Assert.Equal($"0{pack.Uid.Value:X}", Get(gem, "TOPCONT"));   // stops below the char
        Assert.Equal($"0{pack.Uid.Value:X}", Get(gem, "TOPCONT.UID"));
        Assert.Equal("3", Get(gem, "CONTGRID"));
        Assert.Equal("44,55", Get(gem, "CONTP"));

        Assert.True(gem.TrySetProperty("CONTP", "10,20"));
        Assert.Equal("10,20", Get(gem, "CONTP"));

        var loose = Loose(w);
        Assert.Equal("0", Get(loose, "TOPCONT"));
        Assert.False(loose.TryGetProperty("CONTGRID", out _));
        Assert.False(loose.TryGetProperty("CONTP", out _));
        Assert.False(loose.TrySetProperty("CONTP", "1,1"));
        _ = ch;
    }

    [Fact]
    public void LinkIsValid_AnswersEvenWithoutALink()
    {
        var w = MakeWorld();
        var it = Loose(w);
        Assert.Equal("0", Get(it, "LINK.ISVALID"));
        var other = Loose(w);
        it.TrySetProperty("LINK", $"0{other.Uid.Value:X}");
        Assert.Equal("1", Get(it, "LINK.ISVALID"));
        it.TrySetProperty("LINK", "07FFFFF0");
        Assert.Equal("0", Get(it, "LINK.ISVALID"));
    }

    // ---------------- spellbook ----------------

    [Fact]
    public void AddSpell_WritesSpellNAtBitNMinusOne_AndReadsBack()
    {
        var w = MakeWorld();
        var book = Loose(w);
        book.ItemType = ItemType.Spellbook;
        Assert.True(book.TrySetProperty("ADDSPELL", "1"));        // Clumsy
        Assert.Equal(1u, book.More1 & 1u);
        Assert.True(book.ContainsSpell(1));
        Assert.False(book.ContainsSpell(2));
        Assert.Equal("1", Get(book, "ADDSPELL.1"));
        Assert.Equal("0", Get(book, "ADDSPELL 2"));

        var rock = Loose(w);
        rock.TrySetProperty("ADDSPELL", "1");                     // not a spellbook
        Assert.Equal(0u, rock.More1);
    }

    // ---------------- base-def key families ----------------

    [Fact]
    public void NumberDefKeys_FallBackToTheDefinition_AndAZeroOverridesIt()
    {
        LoadDefs("[ITEMDEF 01f03]", "DEFNAME=i_probe_robe", "TYPE=t_clothing",
            "DURABILITY=7", "LIFESPAN=30", "MATERIAL=iron", "MAKERSNAME=Nobody");
        var w = MakeWorld();
        var it = Loose(w);
        it.BaseId = 0x1F03;

        Assert.Equal("7", Get(it, "DURABILITY"));
        Assert.Equal("30", Get(it, "LIFESPAN"));
        Assert.Equal("iron", Get(it, "MATERIAL"));
        Assert.Equal("", Get(it, "MAKERSNAME"));           // no definition fallback
        Assert.Equal("0", Get(it, "RECHARGE"));

        Assert.True(it.TrySetProperty("DURABILITY", "0"));
        Assert.Equal("0", Get(it, "DURABILITY"));          // stored, not deleted
        Assert.True(it.TrySetProperty("MAKERSNAME", "\"Smith\""));
        Assert.Equal("Smith", Get(it, "MAKERSNAME"));
        Assert.True(it.TrySetProperty("ITEMSETNAME", "Plate Set"));
        Assert.Equal("Plate Set", Get(it, "ITEMSETNAME"));
    }

    [Fact]
    public void DropSoundAndDoorOpenId_ReachWhatTheEngineReads()
    {
        var w = MakeWorld();
        var it = Loose(w);
        Assert.True(it.TrySetProperty("DROPSOUND", "0123"));
        Assert.Equal((ushort)0x123, it.GetDropSound(false));

        Assert.True(it.TrySetProperty("DOOROPENID", "0675"));
        Assert.Equal((ushort)0x675, it.DoorOpenId);
        Assert.Equal("0675", Get(it, "DOOROPENID"));
        Assert.True(it.TrySetProperty("DOOROPENSOUND", "0EB"));
        Assert.Equal((ushort)0xEB, it.GetDoorSound(opening: true));
    }

    [Fact]
    public void CanFlagKeys_OnTheDefinition_ReadAsTheMaskedBit()
    {
        LoadDefs("[ITEMDEF 01f04]", "DEFNAME=i_probe_sword", "TYPE=t_weapon_sword",
            "EXCEPTIONAL=1", "DYE=1", "MAKERSMARK=0", "REPAIR=1");
        var w = MakeWorld();
        var it = Loose(w);
        it.BaseId = 0x1F04;

        Assert.Equal(0x20000.ToString(), Get(it, "EXCEPTIONAL"));
        Assert.Equal(0x200.ToString(), Get(it, "DYE"));
        Assert.Equal("0", Get(it, "MAKERSMARK"));
        Assert.Equal("01000", Get(it, "REPAIR"));
        Assert.Equal("0", Get(it, "ENCHANT"));
        // No SKILL line: a sword implies swordsmanship (IBC_SKILL).
        Assert.Equal(((int)SkillType.Swordsmanship).ToString(), Get(it, "SKILL"));

        var def = DefinitionLoader.GetItemDef(0x1F04)!;
        Assert.True((def.Can & CanFlags.I_Exceptional) != 0);
        Assert.True(def.Dye);
    }

    [Fact]
    public void MaxAmountZero_PutsTheItemBackOnTheGlobalCap()
    {
        LoadDefs("[ITEMDEF 01f05]", "DEFNAME=i_probe_pile", "CAN=0100");
        var w = MakeWorld();
        var it = Loose(w);
        it.BaseId = 0x1F05;
        Assert.True(it.TrySetProperty("MAXAMOUNT", "100"));
        Assert.Equal("100", Get(it, "MAXAMOUNT"));
        Assert.True(it.TrySetProperty("MAXAMOUNT", "0"));
        Assert.Equal(Item.ItemsMaxAmount.ToString(), Get(it, "MAXAMOUNT"));
    }

    // ---------------- verbs ----------------

    [Fact]
    public void Bounce_PutsANewItemIntoTheSourcesPack()
    {
        var w = MakeWorld();
        var (ch, pack) = Wearer(w);
        var made = w.CreateItem();                 // NEWITEM: not placed anywhere yet
        Assert.True(made.TryExecuteCommand("BOUNCE", "", new CharConsole(ch)));
        Assert.Equal(pack.Uid, made.ContainedIn);

        var other = Loose(w);
        Assert.False(other.TryExecuteCommand("BOUNCE", "", new ServerConsole()));  // no SRC char
        Assert.False(other.ContainedIn.IsValid);
    }

    [Fact]
    public void ContConsume_ReadsAWholeResourceList()
    {
        var w = MakeWorld();
        var chest = Loose(w);
        chest.ItemType = ItemType.Container;
        var gold = w.CreateItem(); gold.BaseId = 0x0EED; gold.Amount = 10; chest.AddItem(gold);
        var logs = w.CreateItem(); logs.BaseId = 0x1BDD; logs.Amount = 5; chest.AddItem(logs);
        var gem = w.CreateItem(); gem.BaseId = 0x0F10; gem.ItemType = ItemType.Gem; gem.Amount = 4; chest.AddItem(gem);

        Assert.True(chest.TryExecuteCommand("CONTCONSUME", "2 0EED, 3 01BDD, t_gem 1", new ServerConsole()));
        Assert.Equal((8, 2, 3), ((int)gold.Amount, (int)logs.Amount, (int)gem.Amount));

        var rock = Loose(w);                        // not a container: nothing happens
        Assert.True(rock.TryExecuteCommand("CONTCONSUME", "1 0EED", new ServerConsole()));
    }

    [Fact]
    public void Decay_TakesTenthsOfASecond_AndANegativeClearsIt()
    {
        var w = MakeWorld();
        var it = Loose(w);
        long before = Environment.TickCount64;
        Assert.True(it.TryExecuteCommand("DECAY", "50", new ServerConsole()));   // 5 s
        Assert.InRange(it.DecayTime, before + 4_000, Environment.TickCount64 + 5_000);
        Assert.True(it.IsAttr(ObjAttributes.Decay));

        Assert.True(it.TryExecuteCommand("DECAY", "-1", new ServerConsole()));
        Assert.Equal(0, it.DecayTime);
        Assert.False(it.IsAttr(ObjAttributes.Decay));
    }
}
