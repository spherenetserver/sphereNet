using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Gates Source-X puts in front of an action that SphereNet skipped:
/// <list type="bullet">
/// <item>the wand or scroll a cast is bound to belongs to that one cast
/// (Cmd_Skill_Magery, CClientUse.cpp:1009) and is released when its cursor is
/// dropped (SetTargMode, CClientMsg.cpp:1678-1701);</item>
/// <item>Magic Lock and Unlock go through SetMagicLock / Use_LockPick
/// (CItem.cpp:5382-5592, 5703-5719);</item>
/// <item>an item on the ground is lifted only when it is movable (CItem::IsMovable,
/// CCharAct.cpp:2976-2979);</item>
/// <item>the equip packet's layer byte is replaced by the item's own
/// (receive.cpp:554-564);</item>
/// <item>the blacksmith menu wants the ingots, a hammer and a forge
/// (Cmd_Skill_Smith, CClientUse.cpp:1255-1291).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXTouchAndSourceGateTests
{
    private sealed class Env : IDisposable
    {
        private readonly ILoggerFactory _logs = TestHarness.CreateLoggerFactory();
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly Character Player;
        public readonly GameClient Client;
        public readonly SpellEngine Spells;
        public readonly TriggerDispatcher Triggers = new();

        public Env()
        {
            Player = World.CreateCharacter();
            Player.IsPlayer = true;
            Player.MaxMana = Player.Mana = 100;
            World.PlaceCharacter(Player, new Point3D(100, 100, 0, 0));
            var pack = World.CreateItem();
            pack.ItemType = ItemType.Container;
            Player.Equip(pack, Layer.Pack);
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Heal, ManaCost = 10, CastTimeBase = 5,
                Flags = SpellFlag.Heal | SpellFlag.TargChar,
            });
            Spells = new SpellEngine(World, registry) { TriggerDispatcher = Triggers };
            Client = TestHarness.CreateClient(_logs, World, new AccountManager(_logs), 19870);
            TestHarness.AttachCharacter(Client, Player);
            Client.SetEngines(spellEngine: Spells, triggerDispatcher: Triggers);
            Character.MagicFlags = 0;
        }

        public Item Wand()
        {
            var wand = World.CreateItem();
            wand.ItemType = ItemType.Wand;
            wand.MoreP = new Point3D((short)SpellType.Heal, 0, 0, 0);
            wand.More2 = 5;
            wand.SetAttr(ObjAttributes.Magic);
            Player.Backpack!.AddItem(wand);
            return wand;
        }

        public string Said()
        {
            var sb = new StringBuilder();
            foreach (var pkt in TestHarness.GetQueuedPackets(Client.NetState))
            {
                var span = pkt.Span;
                if (span.Length > 48 && span[0] == 0xAE)
                    sb.Append(Encoding.BigEndianUnicode.GetString(span[48..])).Append('\n');
                else if (span.Length > 44 && span[0] == 0x1C)
                    sb.Append(Encoding.ASCII.GetString(span[44..])).Append('\n');
            }
            return sb.ToString().Replace("\0", "");
        }

        public void Dispose() => _logs.Dispose();
    }

    // ---- the cast source ----

    [Fact]
    public void DismissingAWandCursorReleasesTheWandAndFiresSpellTargetCancel()
    {
        using var env = new Env();
        var wand = env.Wand();
        int cancelledSpell = -1;
        object? cancelSource = null;
        env.Triggers.RegisterCharEvent("EVENTSPLAYER", "SpellTargetCancel", (_, args) =>
        {
            cancelledSpell = (int)args.N1;
            cancelSource = args.O1;
            return TriggerResult.Default;
        });

        env.Client.HandleDoubleClick(wand.Uid.Value);
        Assert.True(env.Client.Targets.CursorActive);
        Assert.True(env.Player.TryGetTag("WAND_UID", out _));

        env.Client.HandleTargetResponse(0, env.Client.ActiveTargetCursorId, 0, -1, -1, 0, 0);

        Assert.False(env.Player.TryGetTag("WAND_UID", out _));
        Assert.Equal((int)SpellType.Heal, cancelledSpell);
        Assert.Same(wand, cancelSource);
    }

    [Fact]
    public void ACastFromOwnPowerIsNeverPricedByAWandLeftBound()
    {
        using var env = new Env();
        var wand = env.Wand();
        env.Player.SetTag("WAND_UID", wand.Uid.Value.ToString());

        env.Client.HandleCastSpell(SpellType.Heal, 0);

        Assert.False(env.Player.TryGetTag("WAND_UID", out _));
    }

    [Fact]
    public void OpeningASecondWandCursorKeepsTheNewCastBoundToItsWand()
    {
        using var env = new Env();
        var first = env.Wand();
        var second = env.Wand();

        env.Client.HandleDoubleClick(first.Uid.Value);
        env.Client.HandleDoubleClick(second.Uid.Value);

        Assert.True(env.Player.TryGetTag("WAND_UID", out string? bound));
        Assert.Equal(second.Uid.Value.ToString(), bound);
        Assert.Equal((int)SpellType.Heal, env.Client.Targets.SpellCancelId);
    }

    // ---- Magic Lock / Unlock ----

    private static Item Chest(GameWorld world, uint code)
    {
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.More1 = code;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        return chest;
    }

    private static void GiveKey(GameWorld world, Character ch, uint code)
    {
        var key = world.CreateItem();
        key.ItemType = ItemType.Key;
        key.More1 = code;
        ch.Backpack!.AddItem(key);
    }

    [Fact]
    public void MagicLockWantsTheKeyAndNeverLocksAnOwnedThing()
    {
        using var env = new Env();
        var said = new List<string>();
        var chest = Chest(env.World, 0x40000077);

        Assert.False(ActiveSkillEngine.SetMagicLock(env.Player, chest, said.Add));
        Assert.Equal(ItemType.Container, chest.ItemType);
        Assert.Contains(ServerMessages.Get(Msg.LockContNoKey), said);

        GiveKey(env.World, env.Player, 0x40000077);
        chest.SetAttr(ObjAttributes.Owned);
        Assert.False(ActiveSkillEngine.SetMagicLock(env.Player, chest, said.Add));
        Assert.Contains(ServerMessages.Get(Msg.LockMayNot), said);

        chest.ClearAttr(ObjAttributes.Owned);
        Assert.True(ActiveSkillEngine.SetMagicLock(env.Player, chest, said.Add));
        Assert.Equal(ItemType.ContainerLocked, chest.ItemType);
        Assert.False(ActiveSkillEngine.SetMagicLock(env.Player, chest, said.Add));
        Assert.Contains(ServerMessages.Get(Msg.LockAlreadyIs), said);
    }

    [Fact]
    public void UnlockOnADoorNeedsItsKeyWhenMagicUnlockDoorIsZero()
    {
        int saved = Item.MagicUnlockDoor;
        try
        {
            using var env = new Env();
            Item.MagicUnlockDoor = 0;
            var door = env.World.CreateItem();
            door.ItemType = ItemType.DoorLocked;
            env.World.PlaceItem(door, new Point3D(101, 100, 0, 0));
            var said = new List<string>();

            int difficulty = ActiveSkillEngine.UseLockPick(env.World, env.Player, door,
                test: true, fail: false, said.Add, new Random(1));

            Assert.Equal(-1, difficulty);
            Assert.Equal(ItemType.DoorLocked, door.ItemType);
            Assert.Contains(ServerMessages.Get(Msg.UnlockDoorNeedsKey), said);
        }
        finally
        {
            Item.MagicUnlockDoor = saved;
        }
    }

    // ---- lifting ----

    [Theory]
    [InlineData(ObjAttributes.Static, false)]
    [InlineData(ObjAttributes.Invis, false)]
    [InlineData(ObjAttributes.Invis | ObjAttributes.Move_Always, true)]
    public void AGroundItemIsLiftedOnlyWhenItIsMovable(ObjAttributes attrs, bool movable)
    {
        using var env = new Env();
        var thing = env.World.CreateItem();
        thing.SetAttr(attrs);
        env.World.PlaceItem(thing, new Point3D(101, 100, 0, 0));

        Assert.Equal(movable, ItemMoveRules.CanMove(env.Player, thing, out _));
    }

    [Fact]
    public void ASpellFieldOnTheGroundIsNeverLiftedButTheSameAttrsInAPackAre()
    {
        using var env = new Env();
        var field = env.World.CreateItem();
        field.ItemType = ItemType.Spell;
        env.World.PlaceItem(field, new Point3D(101, 100, 0, 0));
        Assert.False(ItemMoveRules.CanMove(env.Player, field, out _));

        var hidden = env.World.CreateItem();
        hidden.SetAttr(ObjAttributes.Invis);
        env.Player.Backpack!.AddItem(hidden);
        Assert.True(ItemMoveRules.CanMove(env.Player, hidden, out _));

        env.Player.AllMove = true;
        Assert.True(ItemMoveRules.CanMove(env.Player, field, out _));
    }

    // ---- equip ----

    [Fact]
    public void TheEquipPacketsLayerByteIsReplacedByTheItemsOwn()
    {
        using var env = new Env();
        const int helmId = 0x7FE1;
        DefinitionLoader.SetItemDef(helmId, new ItemDef(ResourceId.Invalid) { DispIndex = (ushort)helmId, Layer = Layer.Helm });
        var helm = env.World.CreateItem();
        helm.BaseId = helmId;
        helm.ItemType = ItemType.Armor;
        helm.ContainedIn = env.Player.Uid;

        // A forged packet names the backpack's layer.
        env.Client.HandleItemEquip(helm.Uid.Value, (byte)Layer.Pack, env.Player.Uid.Value);

        Assert.Same(helm, env.Player.GetEquippedItem(Layer.Helm));
        Assert.NotSame(helm, env.Player.GetEquippedItem(Layer.Pack));
    }

    [Fact]
    public void AnUnwearableItemIsNotEquippedWhateverLayerTheClientNames()
    {
        using var env = new Env();
        var gold = env.World.CreateItem();
        gold.ItemType = ItemType.Gold;
        gold.Amount = 100;
        gold.ContainedIn = env.Player.Uid;
        var pack = env.Player.Backpack;

        env.Client.HandleItemEquip(gold.Uid.Value, (byte)Layer.Helm, env.Player.Uid.Value);

        Assert.Null(env.Player.GetEquippedItem(Layer.Helm));
        Assert.Same(pack, env.Player.GetEquippedItem(Layer.Pack));
    }

    // ---- blacksmith menu ----

    [Fact]
    public void IngotsOpenTheSmithMenuOnlyCarriedAndWithAHammerInHand()
    {
        using var env = new Env();
        var ingots = env.World.CreateItem();
        ingots.ItemType = ItemType.Ingot;
        ingots.Amount = 10;
        env.World.PlaceItem(ingots, new Point3D(101, 100, 0, 0));

        TestHarness.ClearQueuedPackets(env.Client.NetState);
        env.Client.HandleDoubleClick(ingots.Uid.Value);
        Assert.Contains(ServerMessages.Get(Msg.SmithingReach), env.Said());

        env.Player.Backpack!.AddItem(ingots);
        env.Client.HandleDoubleClick(ingots.Uid.Value);
        Assert.Contains(ServerMessages.Get(Msg.SmithingHammer), env.Said());

        var hammer = env.World.CreateItem();
        hammer.ItemType = ItemType.WeaponMaceSmith;
        env.Player.Equip(hammer, Layer.OneHanded);
        env.Client.HandleDoubleClick(ingots.Uid.Value);
        Assert.Contains(ServerMessages.Get(Msg.SmithingForge), env.Said());
    }
}
