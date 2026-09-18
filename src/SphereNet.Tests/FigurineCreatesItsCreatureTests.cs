using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A figurine with nothing stored in it MAKES the creature it names.
///
/// Upstream's Use_Figurine creates the NPC when no creature is linked: from
/// m_itFigurine.m_ID (MORE1) or, when that is zero, from the definition's TDATA3 via
/// FindCharTrack (CCharUse.cpp:1152); the double-click then consumes the figurine
/// (CCharUse.cpp:1748).
///
/// That is the only kind a vendor sells - a shrunk mount bought off the shelf has never
/// held a creature, it names one - and the path was missing, so every purchased
/// figurine answered "This figurine is not yours" and could not be opened.
///
/// The stored-pet read was wrong too: upstream keeps that uid in MORE2
/// (m_itFigurine.m_UID, CItem.h:403) while MORE1 is the creature id, so reading MORE1
/// as a uid looked a creature id up as a character and found nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class FigurineCreatesItsCreatureTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_fig_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Clients.GameClient Client,
                                SphereNet.Game.Objects.Characters.Character Me);

    /// <summary>A pack-shaped figurine: TYPE=t_figurine with the creature in TDATA3,
    /// exactly as the shipped i_char_icons.scp writes it.</summary>
    private Bench Build()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "f.scp");
        File.WriteAllLines(file, [
            "[CHARDEF 00c8]", "DEFNAME=c_horse", "NAME=a horse", "",
            "[ITEMDEF 020e1]", "DEFNAME=i_pet_horse", "NAME=horse",
            "TYPE=t_figurine", "TDATA3=c_horse",
        ]);
        using var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8811);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.Str = 100; me.Dex = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me);
    }

    private static Item Figurine(Bench b)
    {
        var fig = b.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(fig, 0x20E1));
        Assert.True(b.Me.Backpack!.TryAddItem(fig));
        return fig;
    }

    /// <summary>The vendor case: nothing stored, TDATA3 names the creature.</summary>
    [Fact]
    public void AFreshFigurineMakesTheCreatureItsDefinitionNames()
    {
        var b = Build();
        var fig = Figurine(b);

        b.Client.HandleDoubleClick(fig.Uid.Value);

        var pet = b.World.GetCharsInRange(b.Me.Position, 3).FirstOrDefault(c => !c.IsPlayer);
        Assert.NotNull(pet);
        Assert.Equal(0xC8, pet!.CharDefIndex);
        Assert.Equal(b.Me.Uid, pet.OwnerSerial);
        Assert.True(fig.IsDeleted, "the figurine is consumed by the double-click");
    }

    /// <summary>MORE1 names the creature directly, and wins over the definition.</summary>
    [Fact]
    public void More1NamesTheCreature()
    {
        var b = Build();
        var fig = Figurine(b);
        fig.More1 = 0xC8;

        b.Client.HandleDoubleClick(fig.Uid.Value);

        var pet = b.World.GetCharsInRange(b.Me.Position, 3).FirstOrDefault(c => !c.IsPlayer);
        Assert.NotNull(pet);
        Assert.Equal(0xC8, pet!.CharDefIndex);
    }

    /// <summary>A figurine linked to somebody else is still refused - that check is
    /// the one thing this path must not lose.</summary>
    [Fact]
    public void SomebodyElsesFigurineIsStillRefused()
    {
        var b = Build();
        var fig = Figurine(b);
        var other = b.World.CreateCharacter();
        b.World.PlaceCharacter(other, new Point3D(120, 120, 0, 0));
        fig.Link = other.Uid;

        b.Client.HandleDoubleClick(fig.Uid.Value);

        Assert.False(fig.IsDeleted);
        Assert.DoesNotContain(b.World.GetCharsInRange(b.Me.Position, 3), c => !c.IsPlayer);
    }

    /// <summary>A figurine that names nothing at all still says so rather than
    /// creating an empty creature.</summary>
    [Fact]
    public void AFigurineThatNamesNothingCreatesNothing()
    {
        var b = Build();
        var fig = b.World.CreateItem();
        fig.ItemType = ItemType.Figurine;
        Assert.True(b.Me.Backpack!.TryAddItem(fig));

        b.Client.HandleDoubleClick(fig.Uid.Value);

        Assert.False(fig.IsDeleted);
        Assert.DoesNotContain(b.World.GetCharsInRange(b.Me.Position, 3), c => !c.IsPlayer);
    }
}
