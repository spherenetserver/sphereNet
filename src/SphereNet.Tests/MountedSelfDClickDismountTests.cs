using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Mounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// Riding and dismounting through the real client handlers and real script loading.
/// The fixture is modelled on a Sphere 56T custom-version pack: a named CHARDEF that
/// borrows its body through ID= from a numeric horse CHARDEF, the GM body, the STATF_
/// and ATTR_ defnames, a staff event whose @Click drops the criminal bit with
/// "FLAGS &lt;FLAGS&gt;&amp;~statf_criminal", and a pack creature answering the
/// 56T-only @NPCMount / @NPCDisMount hooks.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MountedSelfDClickDismountTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_mount_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed record Bench(SphereNet.Game.World.GameWorld World,
        SphereNet.Game.Clients.GameClient Client, Character Me, Character Unicorn,
        MountEngine Mounts, SphereNet.Scripting.Resources.ResourceHolder Resources);

    private static readonly string[] Script =
    [
        "[DEFNAME flags]",
        "statf_invul             000000001",
        "statf_war               000000020",
        "statf_insubstantial     000002000",
        "statf_criminal          002000000",
        "statf_ridden            040000000",
        "statf_onhorse           080000000",
        "attr_identified         01",
        "attr_newbie             04",
        "attr_move_never         010",
        "mt_ghost                01",
        "mt_walk                 04",
        "",
        "[DEFNAME mount_items]",
        "mount_0xcc\t03ea2\t// horse 4",
        "",
        "[CHARDEF 03db]", "DEFNAME=c_man_gm", "NAME=GM",
        "CAN=MT_WALK|MT_RUN|MT_SWIM|MT_USEHANDS|MT_EQUIP|MT_GHOST", "",
        "[CHARDEF 0cc]", "DEFNAME=c_horse_brown_dk", "NAME=Dark Brown Horse",
        "CAN=MT_WALK|MT_RUN|MT_NONHUM", "",
        "[CHARDEF c_unicorn] ", "DEFNAME=c_unicorn ", "NAME=Unicorn",
        "ID=c_horse_brown_dk", "CAN=MT_WALK|MT_RUN", "",
        "ON=@Create ", "NPC=brain_animal", "COLOR=0481", "",
        "[CHARDEF 0e2]", "DEFNAME=c_horse_gray", "NAME=Gray Horse",
        "CAN=MT_WALK|MT_RUN|MT_NONHUM", "",
        "ON=@NPCMount",
        "if (<src.tag0.mount> != 2)",
        "\targo.tag.mount_offered <src.uid>",
        "\treturn 1",
        "endif",
        "",
        "on=@NPCDisMount",
        "tag.dismounted_by <src.uid>",
        "tag.dismount_argo <argo.uid>",
        "",
        "[ITEMDEF i_mt_horse_brown_dk]", "ID=03ea2", "TYPE=t_eq_horse", "LAYER=layer_horse", "TDATA3=c_horse_brown_dk", "",
        "[EVENTS e_staff]",
        "On=@click",
        "flags <flags>&~statf_criminal",
        "IF (<FLAGS> & statf_insubstantial)",
        "\tmessage @080a [Invis]",
        "endif",
        "return 1",
        "",
        "[EVENTS e_selfdclick]",
        "ON=@DClick",
        "tag.dclick_src <src.uid>",
        "return 1",
        "",
    ];

    private Bench Build()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "m.scp");
        File.WriteAllLines(file, Script);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.ScpBaseDir = _dir;
        stack.Resources.LoadResourceFile(file);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(stack.LoggerFactory, world, new AccountManager(stack.LoggerFactory), 8851);
        var mounts = new MountEngine(world);
        client.SetEngines(mountEngine: mounts, triggerDispatcher: stack.Dispatcher);

        var me = world.CreateCharacter();
        me.IsPlayer = true; me.Str = 100; me.Dex = 100; me.Int = 25;
        Assert.True(CharDefHelper.TryApplyDefName(me, "c_man_gm", stack.Resources));
        me.PrivLevel = PrivLevel.Owner;
        Assert.True(me.TryExecuteCommand("EVENTS", "+e_staff", null!));
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var unicorn = world.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(unicorn, "c_unicorn", stack.Resources));
        unicorn.Name = "Snowflake";
        world.PlaceCharacter(unicorn, new Point3D(101, 100, 0, 0));
        return new Bench(world, client, me, unicorn, mounts, stack.Resources);
    }

    private static void AssertDismounted(Bench b, Character mount)
    {
        Assert.False(b.Me.IsMounted, "self dclick while riding dismounts");
        Assert.Null(b.Me.GetEquippedItem(Layer.Horse));
        Assert.False(mount.IsStatFlag(StatFlag.Ridden));
        Assert.Same(mount, b.World.FindChar(mount.Uid));
        Assert.Contains(mount, b.World.GetCharsInRange(b.Me.Position, 2));
    }

    [Fact]
    public void GmRidingANamedChardefMount_DismountsOnSelfDoubleClick()
    {
        var b = Build();
        Assert.Equal(0x3DB, b.Me.BodyId);
        Assert.Equal(0xCC, b.Unicorn.BodyId);

        b.Client.HandleDoubleClick(b.Unicorn.Uid.Value);
        Assert.True(b.Me.IsMounted, "dclick on the creature mounts it");
        Assert.NotNull(b.Me.GetEquippedItem(Layer.Horse));
        Assert.True(b.Unicorn.IsStatFlag(StatFlag.Ridden));

        b.Client.HandleDoubleClick(b.Me.Uid.Value);
        AssertDismounted(b, b.Unicorn);
    }

    /// <summary>CHC_FLAGS reads GetArgULLVal (CChar.cpp:3871). A '|'-list-only parse
    /// stored 0 for this line, wiping STATF_ONHORSE while the mount item stayed worn,
    /// so the next self double-click no longer counted as a dismount.</summary>
    [Fact]
    public void StaffClickEventClearingOneFlagBit_KeepsTheRiderMounted_AndSelfDClickDismounts()
    {
        var b = Build();
        b.Client.HandleDoubleClick(b.Unicorn.Uid.Value);
        Assert.True(b.Me.IsMounted);
        b.Me.SetStatFlag(StatFlag.Criminal);

        b.Client.HandleSingleClick(b.Me.Uid.Value);

        Assert.False(b.Me.IsStatFlag(StatFlag.Criminal), "the cleared bit is gone");
        Assert.True(b.Me.IsMounted, "every other bit survives the expression");

        b.Client.HandleDoubleClick(b.Me.Uid.Value);
        AssertDismounted(b, b.Unicorn);
    }

    [Theory]
    [InlineData("080000000|statf_war", 0x80000020u)]
    [InlineData("statf_onhorse|statf_war|statf_criminal", 0x82000020u)]
    [InlineData("2181038112&~statf_criminal", 0x80000020u)]
    [InlineData("(statf_onhorse|statf_criminal)&~statf_criminal", 0x80000000u)]
    [InlineData("020002101", 0x20002101u)]
    public void FlagsAssignment_ReadsTheValueAsAnExpression(string value, uint expected)
    {
        var ch = Build().World.CreateCharacter();
        Assert.True(ch.TrySetProperty("FLAGS", value));
        Assert.Equal(expected, (uint)ch.StatFlags);
    }

    /// <summary>Make_Figurine + Horse_Mount (CCharAct.cpp:3633-3638, 3995): the mount
    /// item is IT_EQ_HORSE, carries the creature's name and hue, MORE1 its CHARDEF,
    /// MORE2 its uid and LINK the rider - what FINDLAYER.layer_horse.MORE1/MORE2 read.</summary>
    [Fact]
    public void MountItem_CarriesTheCreatureLikeAFigurine()
    {
        var b = Build();
        b.Client.HandleDoubleClick(b.Unicorn.Uid.Value);
        var worn = b.Me.GetEquippedItem(Layer.Horse)!;

        Assert.Equal(ItemType.EqHorse, worn.ItemType);
        Assert.Equal("Snowflake", worn.Name);
        Assert.Equal(b.Unicorn.Hue, worn.Hue);
        Assert.Equal(b.Unicorn.Uid.Value, worn.More2);
        Assert.Equal(b.Me.Uid, worn.Link);
        Assert.True(worn.TryGetProperty("MORE1", out string more1));
        Assert.Equal("c_unicorn", more1.Trim());
    }

    /// <summary>LayerAdd IT_EQ_HORSE / OnRemoveObj (CCharAct.cpp:380, 488): whatever
    /// puts an item on the mount layer makes the wearer a rider, and taking it off
    /// puts them on foot - a script EQUIP, NEWITEM+CONT, a CHARDEF ITEM= line, a load.</summary>
    [Fact]
    public void AnyItemOnTheMountLayer_MakesTheWearerARider()
    {
        var b = Build();
        var npc = b.World.CreateCharacter();
        b.World.PlaceCharacter(npc, new Point3D(110, 110, 0, 0));
        var mt = b.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(mt, b.Resources.ResolveDefName("i_mt_horse_brown_dk").Index));
        Assert.Equal(ItemType.EqHorse, mt.ItemType);

        Assert.True(npc.Equip(mt, Layer.Horse));
        Assert.True(npc.IsMounted);

        npc.Unequip(Layer.Horse);
        Assert.False(npc.IsMounted);
    }

    /// <summary>A rider out of a Sphere save: the mount item names the creature only
    /// through MORE1 (CHARDEF) and MORE2 (uid), the creature is parked with
    /// STATF_RIDDEN, and none of the engine's own MOUNT_NPC_* tags exist.</summary>
    [Fact]
    public void LegacySaveRider_FindsItsCreatureThroughTheMountItem()
    {
        var b = Build();
        string dir = Path.Combine(_dir, "save");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spherechars.scp"), """
            [WORLDCHAR c_unicorn]
            SERIAL=031709
            NAME=Unicorn
            COLOR=0481
            FLAGS=040000000
            P=900,900,0

            [WORLDCHAR c_man_gm]
            SERIAL=02da70
            NAME=Rider
            P=300,300,0

            [WORLDITEM 03ea2]
            SERIAL=040026218
            NAME=Unicorn
            TYPE=t_eq_horse
            LINK=02da70
            ATTR=010
            MORE1=c_unicorn
            MORE2=031709
            LAYER=25
            CONT=02da70
            """);
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(world, dir);

        var rider = world.FindChar(new Serial(0x02DA70))!;
        var pet = world.FindChar(new Serial(0x031709))!;
        Assert.NotNull(rider);
        Assert.NotNull(pet);
        Assert.True(rider.IsMounted, "the worn mount item makes the loaded character a rider");
        Assert.True(pet.IsStatFlag(StatFlag.Ridden));
        Assert.DoesNotContain(pet, world.GetCharsInRange(pet.Position, 1));
        var worn = rider.GetEquippedItem(Layer.Horse)!;
        Assert.True(worn.TryGetProperty("MORE1", out string more1));
        Assert.Equal("c_unicorn", more1.Trim());

        var client = TestHarness.CreateClient(LoggerFactory.Create(_ => { }), world,
            new AccountManager(LoggerFactory.Create(_ => { })), 8853);
        client.SetEngines(mountEngine: new MountEngine(world));
        rider.IsPlayer = true;
        TestHarness.AttachCharacter(client, rider);

        client.HandleDoubleClick(rider.Uid.Value);

        Assert.False(rider.IsMounted);
        Assert.Null(rider.GetEquippedItem(Layer.Horse));
        Assert.False(pet.IsStatFlag(StatFlag.Ridden));
        Assert.Equal(rider.X, pet.X);
        Assert.Equal(rider.Y, pet.Y);
        Assert.Contains(pet, world.GetCharsInRange(rider.Position, 2));
    }

    /// <summary>Event_DoubleClick fires @DClick on the clicked character - yourself
    /// included - before Horse_UnMount, and RETURN 1 ends it there
    /// (CClientEvent.cpp:2354-2373).</summary>
    [Fact]
    public void SelfDoubleClick_FiresDClickFirst_AndReturnOneKeepsTheRiderMounted()
    {
        var b = Build();
        b.Client.HandleDoubleClick(b.Unicorn.Uid.Value);
        Assert.True(b.Me.IsMounted);
        Assert.True(b.Me.TryExecuteCommand("EVENTS", "+e_selfdclick", null!));

        b.Client.HandleDoubleClick(b.Me.Uid.Value);

        Assert.True(b.Me.TryGetTag("dclick_src", out string? src));
        Assert.Equal(b.Me.Uid.Value, new Serial(ScriptNumber.TryParseToken(src, out long s) ? (uint)s : 0).Value);
        Assert.True(b.Me.IsMounted, "@DClick RETURN 1 cancels the dismount");

        Assert.True(b.Me.TryExecuteCommand("EVENTS", "-e_selfdclick", null!));
        b.Client.HandleDoubleClick(b.Me.Uid.Value);
        AssertDismounted(b, b.Unicorn);
    }

    /// <summary>Sphere 56T custom-version compatibility: the creature's @NPCMount runs
    /// with the creature as SRC and the rider as ARGO and may refuse the ride; a bare
    /// MOUNT on the creature with the player as SRC seats that player (the pack-mount
    /// dialog); @NPCDisMount runs on the creature once it is down, SRC/ARGO the rider.</summary>
    [Fact]
    public void NpcMountAndNpcDisMount_FireOnTheCreature()
    {
        var b = Build();
        var horse = b.World.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(horse, "c_horse_gray", b.Resources));
        b.World.PlaceCharacter(horse, new Point3D(100, 101, 0, 0));

        b.Client.HandleDoubleClick(horse.Uid.Value);
        Assert.False(b.Me.IsMounted, "@NPCMount RETURN 1 refuses the ride");
        Assert.False(horse.IsStatFlag(StatFlag.Ridden));
        Assert.True(b.Me.TryGetTag("mount_offered", out string? offered));
        Assert.True(ScriptNumber.TryParseToken(offered, out long offeredUid));
        Assert.Equal(horse.Uid.Value, (uint)offeredUid);

        horse.SetTag("mount", "2");
        Character.OnScriptMount = (rider, h) => rider == b.Me && b.Client.MountFromScript(h);
        try
        {
            Assert.True(horse.TryExecuteCommand("MOUNT", "", b.Client));
        }
        finally { Character.OnScriptMount = null; }
        Assert.True(b.Me.IsMounted, "the bare MOUNT verb seats SRC once @NPCMount allows it");
        Assert.True(horse.IsStatFlag(StatFlag.Ridden));

        b.Client.HandleDoubleClick(b.Me.Uid.Value);
        AssertDismounted(b, horse);
        Assert.True(horse.TryGetTag("dismounted_by", out string? by));
        Assert.True(ScriptNumber.TryParseToken(by, out long byUid));
        Assert.Equal(b.Me.Uid.Value, (uint)byUid);
        Assert.True(horse.TryGetTag("dismount_argo", out string? argo));
        Assert.True(ScriptNumber.TryParseToken(argo, out long argoUid));
        Assert.Equal(b.Me.Uid.Value, (uint)argoUid);
    }

    /// <summary>A ship's wheel is worn on the mount layer too (SetPilot), so its holder
    /// is "on horse", and Horse_UnMount hands it back to the ship instead of looking
    /// for a creature (CCharAct.cpp:4030-4038).</summary>
    [Fact]
    public void ShipWheelOnTheMountLayer_IsReleasedThroughTheShipOnDismount()
    {
        var b = Build();
        var wheel = b.World.CreateItem();
        wheel.BaseId = MountEngine.ShipPilotItemId;
        wheel.ItemType = ItemType.EqHorse;
        Assert.True(b.Me.Equip(wheel, Layer.Horse));
        Assert.True(b.Me.IsMounted);

        Item? released = null;
        b.Mounts.ReleaseShipPilot = (pilot, item) =>
        {
            released = item;
            pilot.Unequip(Layer.Horse);
            b.World.DeleteObject(item);
            return true;
        };

        b.Client.HandleDoubleClick(b.Me.Uid.Value);

        Assert.Same(wheel, released);
        Assert.False(b.Me.IsMounted);
        Assert.Null(b.Me.GetEquippedItem(Layer.Horse));
    }

    private Item WearMountItem(Bench b, Character wearer, string? more1 = null)
    {
        var mt = b.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(mt, b.Resources.ResolveDefName("i_mt_horse_brown_dk").Index));
        if (more1 != null)
            Assert.True(mt.TrySetProperty("MORE1", more1));
        mt.Name = "Blaze";
        mt.Hue = new Color(0x0481);
        Assert.True(wearer.Equip(mt, Layer.Horse));
        return mt;
    }

    /// <summary>Use_Figurine with no creature linked (CCharUse.cpp:1152-1176, 1197-1206):
    /// CreateNPC from TDATA3 when MORE1 is empty, named and coloured by the item, owned
    /// by the rider, placed where the rider stands; the item is gone.</summary>
    [Fact]
    public void UnlinkedMountItem_MakesItsCreatureFromTData3_OnDismount()
    {
        var b = Build();
        var mt = WearMountItem(b, b.Me);
        Assert.True(b.Me.IsMounted);

        b.Client.HandleDoubleClick(b.Me.Uid.Value);

        Assert.False(b.Me.IsMounted);
        Assert.True(mt.IsDeleted);
        var horse = b.World.GetCharsInRange(b.Me.Position, 0).Single(c => !c.IsPlayer);
        Assert.Equal(0xCC, horse.CharDefIndex);
        Assert.Equal("Blaze", horse.Name);
        Assert.Equal(0x0481, horse.Hue.Value);
        Assert.True(horse.HasOwner(b.Me.Uid));
        Assert.False(horse.IsStatFlag(StatFlag.Ridden));
    }

    [Fact]
    public void UnlinkedMountItem_PrefersTheCreatureNamedInMore1()
    {
        var b = Build();
        WearMountItem(b, b.Me, "c_unicorn");

        b.Client.HandleDoubleClick(b.Me.Uid.Value);

        var unicornIndex = b.Resources.ResolveDefName("c_unicorn").Index;
        var made = b.World.GetCharsInRange(b.Me.Position, 0).Single(c => !c.IsPlayer);
        Assert.Equal(unicornIndex, made.CharDefIndex);
        Assert.Equal(0xCC, made.BodyId);
        Assert.True(made.HasOwner(b.Me.Uid));
    }

    /// <summary>"if I'm NPC then my mount goes with me" (CCharAct.cpp:4399): an NPC
    /// rider does not get off when it dies, so no creature appears; the mount item
    /// stays worn and, deleted with the NPC, takes its parked creature along
    /// (CItem::DeleteCleanup, CItem.cpp:209-218).</summary>
    [Fact]
    public void NpcRider_KeepsItsMountOnDeath_AndTheCreatureGoesWithIt()
    {
        var b = Build();
        var dead = b.World.CreateCharacter();
        dead.MaxHits = dead.Hits = 10;
        b.World.PlaceCharacter(dead, new Point3D(120, 120, 0, 0));
        WearMountItem(b, dead);
        var death = new SphereNet.Game.Death.DeathEngine(b.World);
        bool dismounted = false;
        death.DismountHook = _ => dismounted = true;
        death.ProcessDeath(dead);
        Assert.False(dismounted, "the NPC keeps its mount");
        Assert.DoesNotContain(b.World.GetCharsInRange(new Point3D(120, 120, 0, 0), 1),
            c => c != dead && c.CharDefIndex == 0xCC);

        // A rider whose mount is a real parked creature loses it with itself.
        var rider = b.World.CreateCharacter();
        b.World.PlaceCharacter(rider, new Point3D(130, 130, 0, 0));
        b.Unicorn.TryAssignOwnership(rider, rider);
        var mt = b.World.CreateItem();
        mt.BaseId = 0x3EA2;
        mt.ItemType = ItemType.EqHorse;
        mt.More2 = b.Unicorn.Uid.Value;
        b.World.HideFromSector(b.Unicorn);
        b.Unicorn.SetStatFlag(StatFlag.Ridden);
        Assert.True(rider.Equip(mt, Layer.Horse));
        Item.FigurineDeletedHook = item => b.Mounts.OnMountItemDeleted(item);
        try
        {
            b.World.DeleteObject(rider);
        }
        finally { Item.FigurineDeletedHook = null; }
        Assert.True(b.Unicorn.IsDeleted);
    }

    [Fact]
    public void BareMountVerb_WithoutAPlayerSource_StaysANoOp()
    {
        var b = Build();
        bool called = false;
        Character.OnScriptMount = (_, _) => called = true;
        try
        {
            Assert.True(b.Unicorn.TryExecuteCommand("MOUNT", "", null!));
        }
        finally { Character.OnScriptMount = null; }
        Assert.False(called);
    }

    /// <summary>The other flag words read the same way: item ATTR (GetArgLLVal), CANMASK,
    /// region and sector FLAGS.</summary>
    [Theory]
    [InlineData("014&~attr_newbie", 0x10u)]
    [InlineData("attr_move_never|attr_newbie", 0x14u)]
    [InlineData("(attr_identified|attr_newbie)&~attr_identified", 0x04u)]
    [InlineData("04", 0x04u)]
    public void ItemAttr_ReadsTheValueAsAnExpression(string value, uint expected)
    {
        var b = Build();
        var item = b.World.CreateItem();
        Assert.True(item.TrySetProperty("ATTR", value));
        Assert.Equal(expected, (uint)item.Attributes);
    }

    [Fact]
    public void CanMaskRegionAndSectorFlags_ReadTheValueAsAnExpression()
    {
        var b = Build();
        var ch = b.World.CreateCharacter();
        Assert.True(ch.TrySetProperty("CANMASK", "(mt_walk|mt_ghost)&~mt_ghost"));
        Assert.Equal(0x04ul, ch.CanMask);

        var region = new SphereNet.Game.World.Regions.Region();
        Assert.True(region.TrySetProperty("FLAGS", "0100"));
        Assert.Equal(0x100u, (uint)region.Flags);
        Assert.True(region.TrySetProperty("FLAGS", "0300&~0100"));
        Assert.Equal(0x200u, (uint)region.Flags);
    }
}
