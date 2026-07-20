using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Engine gaps found auditing the live events/ script pack — all Source-X-
/// present (pack-custom tokens were skipped per the maintainer rule): the
/// char EQUIP verb dropped its UID argument (killing combat-bonus memories),
/// script/engine unequip and worn REMOVE never fired @Unequip (leaving the
/// bonus cleanup — e.g. a stun freeze — stuck), SKILL FAIL no-op'd, CRAFTEDBY
/// was unreadable, and the pack's pervasive ISVALIDE spelling + STATTOTAL
/// resolved to empty.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class EventScriptParityTests
{
    private static GameWorld World()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void EquipVerb_WithUid_EquipsAndFiresEquipTrigger()
    {
        var world = World();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var robe = world.CreateItem();
        robe.BaseId = 0x1F03;

        Item? equipped = null;
        Character? equippedOn = null;
        Character.ScriptEquipItem = (wearer, item) =>
        {
            // Host wiring: equip at layer 22 (robe) and record it.
            bool ok = wearer.Equip(item, Layer.Robe);
            if (ok) { equipped = item; equippedOn = wearer; }
            return ok;
        };

        Assert.True(ch.TryExecuteCommand("EQUIP", $"0{robe.Uid.Value:X}", null!));
        Assert.Same(robe, equipped);
        Assert.Same(ch, equippedOn);
        Assert.Same(robe, ch.GetEquippedItem(Layer.Robe)); // actually worn
    }

    [Fact]
    public void WornItemRemove_FiresUnequip_ForCleanup()
    {
        var world = World();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var mem = world.CreateItem();
        mem.BaseId = 0x1F14;
        ch.Equip(mem, Layer.Special);

        var unequipFired = new List<(Item, Character)>();
        Item.OnItemUnequipped = (it, wearer) => unequipFired.Add((it, wearer));

        // The combat-bonus scripts REMOVE the worn memory on @Timer/@GetHit;
        // its @Unequip block (clearing the freeze) must run.
        mem.RemoveFromWorld();

        Assert.Contains((mem, ch), unequipFired);
    }

    [Fact]
    public void UnequipVerb_OnWornItem_FiresUnequip()
    {
        var world = World();
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 18001);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, ch);

        var worn = world.CreateItem();
        worn.BaseId = 0x1075;
        ch.Equip(worn, Layer.Gloves);

        var fired = new List<Item>();
        Item.OnItemUnequipped = (it, _) => fired.Add(it);

        Assert.True(worn.TryExecuteCommand("UNEQUIP", "", (SphereNet.Core.Interfaces.ITextConsole)client));
        Assert.Contains(worn, fired);
        Assert.Null(ch.GetEquippedItem(Layer.Gloves)); // bounced off
    }

    [Fact]
    public void SkillFail_CancelsActiveSkill()
    {
        var world = World();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        int? aborted = null;
        Character.ActiveSkillAborted = (_, skill) => aborted = skill;
        // An active skill in progress (Source-X Skill_Start state).
        ch.BeginSkillPending((int)SkillType.Hiding, long.MaxValue, 0, Serial.Invalid, null);
        Assert.True(ch.HasActiveSkillPending());

        Assert.True(ch.TryExecuteCommand("SKILL", "FAIL", null!));
        Assert.Equal((int)SkillType.Hiding, aborted);
        Assert.False(ch.HasActiveSkillPending()); // cancelled
    }

    [Fact]
    public void CraftedBy_ReadsAndWritesLikeCrafter()
    {
        var world = World();
        var item = world.CreateItem();

        Assert.True(item.TrySetProperty("CRAFTEDBY", "0x1234"));
        Assert.True(item.TryGetProperty("CRAFTEDBY", out string byVal));
        Assert.True(item.TryGetProperty("CRAFTER", out string crafterVal));
        Assert.Equal(crafterVal, byVal);           // same backing store
        Assert.Equal("01234", byVal);
    }

    [Fact]
    public void IsValide_Alias_AndStatTotal_Resolve()
    {
        var world = World();
        var ch = world.CreateCharacter();
        ch.Str = 100; ch.Dex = 90; ch.Int = 35;

        // The pack's pervasive ISVALIDE spelling resolves like ISVALID.
        Assert.True(ch.TryGetProperty("ISVALIDE", out string valide));
        Assert.Equal("1", valide);
        Assert.True(ch.TryGetProperty("ISVALID", out string valid));
        Assert.Equal("1", valid);

        // STATTOTAL = STR+DEX+INT (Source-X Stat_GetSum).
        Assert.True(ch.TryGetProperty("STATTOTAL", out string total));
        Assert.Equal("225", total);
    }
}
