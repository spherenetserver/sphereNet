using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Character-side script keywords of the Source-X property tables
/// (CCharNpc_props.tbl, CCharPlayer_props.tbl, CChar_props.tbl, CClient_props.tbl,
/// CParty_props.tbl) read and written the way CCharNPC / CCharPlayer / CChar /
/// CClient / CPartyDef answer them.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharKeywordParityTests
{
    private static Character Make(GameWorld world, bool player, int x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static string Read(Character ch, string key)
    {
        Assert.True(ch.TryGetProperty(key, out string value), key);
        return value;
    }

    // ---- CCharNPC ----

    [Fact]
    public void SpellAdd_PutsEveryListedSpellInTheNpcBook()
    {
        var world = TestHarness.CreateWorld();
        var npc = Make(world, player: false);

        Assert.True(npc.TrySetProperty("SPELLADD", "1,5 18"));

        Assert.Contains((SpellType)1, npc.NpcSpells);
        Assert.Contains((SpellType)5, npc.NpcSpells);
        Assert.Contains((SpellType)18, npc.NpcSpells);
    }

    [Fact]
    public void SpellAdd_EmptyListIsRefused_AndAPlayerIgnoresIt()
    {
        var world = TestHarness.CreateWorld();
        var npc = Make(world, player: false);
        var player = Make(world, player: true, x: 102);

        Assert.False(npc.TrySetProperty("SPELLADD", ""));
        Assert.True(player.TrySetProperty("SPELLADD", "1"));
        Assert.Empty(player.NpcSpells);
    }

    [Fact]
    public void NeedName_WritesNeed_AndReadsMinusOneWhenUnset()
    {
        var world = TestHarness.CreateWorld();
        var npc = Make(world, player: false);

        // The rid is left at UID_UNUSED, printed signed.
        Assert.Equal("-1", Read(npc, "NEEDNAME"));

        Assert.True(npc.TrySetProperty("NEEDNAME", "3 i_gold"));
        Assert.True(npc.TryGetTag("NEED", out string? need));
        Assert.Equal("3 i_gold", need);

        // A player has no NPC part: the key reads 0.
        var player = Make(world, player: true, x: 102);
        Assert.Equal("0", Read(player, "NEEDNAME"));
    }

    [Fact]
    public void VendCapAndVendGold_AreTheBankRestockCeilingAndThePurse()
    {
        var world = TestHarness.CreateWorld();
        var vendor = Make(world, player: false);

        Assert.True(vendor.TrySetProperty("VENDCAP", "25000"));
        Assert.Equal("25000", Read(vendor, "VENDCAP"));
        var bank = vendor.GetEquippedItem(Layer.BankBox);
        Assert.NotNull(bank);
        Assert.Equal(25000u, bank!.More2);
        // The restock engine reads the same field.
        Assert.Equal(25000L, SphereNet.Game.Trade.VendorEngine.RestockGoldFor(vendor));

        Assert.True(vendor.TrySetProperty("VENDGOLD", "1234"));
        Assert.Equal("1234", Read(vendor, "VENDGOLD"));
        Assert.Equal(1234L, SphereNet.Game.Trade.VendorEngine.GetVendorGold(vendor));

        var player = Make(world, player: true, x: 102);
        Assert.Equal("0", Read(player, "VENDGOLD"));
        Assert.Equal("0", Read(player, "VENDCAP"));
    }

    // ---- CCharPlayer ----

    [Fact]
    public void HouseAndShipPositions_AndMultiCounts()
    {
        var world = TestHarness.CreateWorld();
        var owner = Make(world, player: true);

        const int weightyMulti = 0x7FF1;
        var weighty = new ItemDef(ResourceId.Invalid);
        weighty.TagDefs.Set("MULTICOUNT", "3");
        DefinitionLoader.SetItemDef(weightyMulti, weighty);

        var house1 = world.CreateItem();
        house1.BaseId = (ushort)weightyMulti;
        var house2 = world.CreateItem();
        house2.BaseId = 0x7FF2; // no definition: weighs 1
        var ship = world.CreateItem();
        ship.BaseId = 0x7FF3;

        Character.ResolveHouseUidsByOwner = uid => uid == owner.Uid ? [house1.Uid, house2.Uid] : [];
        Character.ResolveShipUidsByOwner = uid => uid == owner.Uid ? [ship.Uid] : [];
        try
        {
            Assert.Equal("0", Read(owner, $"GETHOUSEPOS 0{house1.Uid.Value:X}"));
            Assert.Equal("1", Read(owner, $"GETHOUSEPOS 0{house2.Uid.Value:X}"));
            Assert.Equal("-1", Read(owner, $"GETHOUSEPOS 0{ship.Uid.Value:X}"));
            Assert.Equal("0", Read(owner, $"GETSHIPPOS 0{ship.Uid.Value:X}"));
            Assert.Equal("-1", Read(owner, "GETSHIPPOS 0"));

            Assert.Equal("4", Read(owner, "HOUSEMULTICOUNT"));
            Assert.Equal("1", Read(owner, "SHIPMULTICOUNT"));
            Assert.False(owner.TrySetProperty("HOUSEMULTICOUNT", "9"));

            // A player key asked of an NPC answers 0.
            var npc = Make(world, player: false, x: 102);
            Assert.Equal("0", Read(npc, "HOUSEMULTICOUNT"));
        }
        finally
        {
            Character.ResolveHouseUidsByOwner = null;
            Character.ResolveShipUidsByOwner = null;
        }
    }

    [Fact]
    public void KrToolbarStatus_IsAPlayerFlagThatTellsTheClient()
    {
        var world = TestHarness.CreateWorld();
        var player = Make(world, player: true);
        var sent = new List<bool>();
        Character.SendKrToolbarStatus = (_, on) => sent.Add(on);

        Assert.Equal("0", Read(player, "KRTOOLBARSTATUS"));
        Assert.True(player.TrySetProperty("KRTOOLBARSTATUS", "1"));
        Assert.Equal("1", Read(player, "KRTOOLBARSTATUS"));
        Assert.Equal([true], sent);

        var npc = Make(world, player: false, x: 102);
        Assert.True(npc.TrySetProperty("KRTOOLBARSTATUS", "1"));
        Assert.Equal("0", Read(npc, "KRTOOLBARSTATUS"));
        Assert.Single(sent);
    }

    [Fact]
    public void RefuseGlobalChatRequests_RoundTrips()
    {
        var world = TestHarness.CreateWorld();
        var player = Make(world, player: true);

        Assert.Equal("0", Read(player, "REFUSEGLOBALCHATREQUESTS"));
        Assert.True(player.TrySetProperty("REFUSEGLOBALCHATREQUESTS", "1"));
        Assert.Equal("1", Read(player, "REFUSEGLOBALCHATREQUESTS"));
        Assert.True(player.RefuseGlobalChatRequests);
        Assert.True(player.TrySetProperty("REFUSEGLOBALCHATREQUESTS", "0"));
        Assert.Equal("0", Read(player, "REFUSEGLOBALCHATREQUESTS"));
    }

    // ---- CChar ----

    [Fact]
    public void DamAdjusted_IsTheUnrolledDamageRange()
    {
        var world = TestHarness.CreateWorld();
        var npc = Make(world, player: false);
        Assert.True(npc.TrySetProperty("DAM", "4,8"));

        // An NPC takes no stat bonus without COMBAT_NPC_BONUSDAMAGE.
        Assert.Equal("4,8", Read(npc, "DAMADJUSTED"));
        Assert.Equal("4", Read(npc, "DAMADJUSTED.LO"));
        Assert.Equal("8", Read(npc, "DAMADJUSTED.HI"));
        Assert.False(npc.TrySetProperty("FIGHTRANGE", "3"));
    }

    [Fact]
    public void FightRange_IsTheLargerOfBodyAndWeaponReach()
    {
        var world = TestHarness.CreateWorld();
        const int reachDef = 0x3FF1;
        DefinitionLoader.SetCharDef(reachDef, new CharDef(ResourceId.Invalid) { RangeMax = 2 });
        const int plainDef = 0x3FF2;
        DefinitionLoader.SetCharDef(plainDef, new CharDef(ResourceId.Invalid));

        var reacher = Make(world, player: false);
        reacher.CharDefIndex = reachDef;
        var plain = Make(world, player: false, x: 102);
        plain.CharDefIndex = plainDef;

        // Bare hands add nothing (m_uidWeapon empty).
        Assert.Equal("2", Read(reacher, "FIGHTRANGE"));
        Assert.Equal("0", Read(plain, "FIGHTRANGE"));

        // A weapon naming no RANGE reaches 1.
        var sword = world.CreateItem();
        sword.BaseId = 0x7FF4;
        sword.ItemType = ItemType.WeaponSword;
        Assert.True(plain.Equip(sword, Layer.OneHanded));
        Assert.Equal("1", Read(plain, "FIGHTRANGE"));

        // A spear def with RANGE=3 outreaches the body.
        const int spearId = 0x7FF5;
        DefinitionLoader.SetItemDef(spearId, new ItemDef(ResourceId.Invalid) { RangeMax = 3 });
        var spear = world.CreateItem();
        spear.BaseId = (ushort)spearId;
        spear.ItemType = ItemType.WeaponFence;
        Assert.True(reacher.Equip(spear, Layer.TwoHanded));
        Assert.Equal("3", Read(reacher, "FIGHTRANGE"));
    }

    [Fact]
    public void EmoteColorOverride_RoundTrips()
    {
        var world = TestHarness.CreateWorld();
        var ch = Make(world, player: false);

        Assert.Equal("0", Read(ch, "EMOTECOLOROVERRIDE"));
        Assert.True(ch.TrySetProperty("EMOTECOLOROVERRIDE", "0481"));
        Assert.Equal((ushort)0x481, ch.EmoteColorOverride);
        Assert.Equal(0x481.ToString(), Read(ch, "EMOTECOLOROVERRIDE"));
        // Independent of the speech colour.
        Assert.Equal("0", Read(ch, "SPEECHCOLOROVERRIDE"));
    }

    // ---- CClient ----

    [Fact]
    public void LastEvent_IsOnTheTimeHiresClock()
    {
        var world = TestHarness.CreateWorld();
        world.SetGameClockMs(500_000);
        var player = Make(world, player: true);

        Assert.Equal("0", Read(player, "LASTEVENT")); // no client attached

        Character.ResolveClientLastEventTick = _ => Environment.TickCount64;
        long last = long.Parse(Read(player, "LASTEVENT"));
        Assert.InRange(last, 499_000, 500_000);
        Assert.False(player.TrySetProperty("LASTEVENT", "5"));
    }

    // ---- CPartyDef ----

    [Fact]
    public void PartySpeechFilter_TakesOnlyALoadedFunction()
    {
        var world = TestHarness.CreateWorld();
        var master = Make(world, player: true);
        var party = new PartyDef(master.Uid);
        var savedFinder = Character.ResolvePartyFinder;
        Character.ResolvePartyFinder = uid => party.IsMember(uid) ? party : null;
        PartyDef.FunctionExists = name => name == "f_party_filter";
        try
        {
            Assert.Equal("", Read(master, "PARTY.SPEECHFILTER"));

            Assert.True(master.TrySetProperty("PARTY.SPEECHFILTER", "f_party_filter"));
            Assert.Equal("f_party_filter", Read(master, "PARTY.SPEECHFILTER"));

            // An unknown function is refused and the filter kept.
            Assert.False(master.TrySetProperty("PARTY.SPEECHFILTER", "f_missing"));
            Assert.Equal("f_party_filter", party.SpeechFilter);

            // No argument clears it.
            Assert.True(master.TrySetProperty("PARTY.SPEECHFILTER", ""));
            Assert.Equal("", Read(master, "PARTY.SPEECHFILTER"));
        }
        finally
        {
            Character.ResolvePartyFinder = savedFinder;
        }
    }
}
