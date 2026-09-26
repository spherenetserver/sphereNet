using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

// Source-X wand/scroll layout (CItem.h:218-224): MOREX = spell, MOREY = spell
// level, MORE2 = wand charges (255 unlimited); ATTR_MAGIC is required to cast
// (CCharSpell.cpp:2408-2445). The pack writes MOREX=s_<spell> and MOREY=50.0.
[Collection("DefinitionLoaderSerial")]
public sealed class MagicItemFieldTests
{
    private static void LoadSpells()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_wand_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, "[SPELL 18]\nDEFNAME=s_fireball\nNAME=Fireball\n");
        var res = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? ""
        };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void MoreX_TakesASpellDefname_AndMoreY_AFixedPointLevel()
    {
        LoadSpells();
        var world = TestHarness.CreateWorld();
        var wand = world.CreateItem();

        Assert.True(wand.TrySetProperty("MOREX", "s_fireball"));
        Assert.True(wand.TrySetProperty("MOREY", "50.0"));

        Assert.Equal(SpellType.Fireball, SpellEngine.MagicItemSpell(wand));
        Assert.Equal(500, wand.MoreP.Y);
    }

    [Theory]
    [InlineData(3u, 2u)]
    [InlineData(255u, 255u)] // unlimited
    [InlineData(0u, 0u)]     // empty stays empty
    public void AWandSpendsOneChargeFromMore2(uint before, uint after)
    {
        var world = TestHarness.CreateWorld();
        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.MoreP = new Point3D((short)SpellType.Fireball, 0, 0, 0);
        wand.More2 = before;

        SpellEngine.ConsumeWandCharge(wand);

        Assert.Equal(after, wand.More2);
        Assert.Equal(before > 0, SpellEngine.WandHasCharge(wand));
        Assert.Equal(SpellType.Fireball, SpellEngine.MagicItemSpell(wand)); // an empty wand keeps its spell
    }

    private static (GameClient Client, Character Player, GameWorld World) ClientEnv()
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var state = TestHarness.CreateActiveNetState(lf, 19950);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Mana = player.MaxMana = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);
        return (client, player, world);
    }

    [Fact]
    public void AScrollWithoutTheMagicAttributeCastsNothing()
    {
        var (client, player, world) = ClientEnv();
        var scroll = world.CreateItem();
        scroll.ItemType = ItemType.Scroll;
        scroll.MoreP = new Point3D((short)SpellType.Fireball, 0, 0, 0);
        player.Backpack!.AddItem(scroll);

        client.HandleDoubleClick(scroll.Uid.Value);
        Assert.False(player.TryGetTag("SCROLL_UID", out _));

        scroll.SetAttr(ObjAttributes.Magic);
        client.HandleDoubleClick(scroll.Uid.Value);
        Assert.True(player.TryGetTag("SCROLL_UID", out _));
    }

    [Fact]
    public void AWandWithNoChargesCastsNothing()
    {
        var (client, player, world) = ClientEnv();
        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.SetAttr(ObjAttributes.Magic);
        wand.MoreP = new Point3D((short)SpellType.Fireball, 0, 0, 0);
        player.Backpack!.AddItem(wand);

        client.HandleDoubleClick(wand.Uid.Value);
        Assert.False(player.TryGetTag("WAND_UID", out _));

        wand.More2 = 5;
        client.HandleDoubleClick(wand.Uid.Value);
        Assert.True(player.TryGetTag("WAND_UID", out _));
    }
}
