using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// MEMORYFINDTYPE / MEMORYFIND and FINDLAYER as Source-X resolves them
/// (CChar::r_GetRef): the argument is an expression - a leading 0 makes a number
/// hex, a defname gives its value - and a memory ref is the memory ITEM itself,
/// with its own UID, its flags in COLOR and its LINK to a character or an item.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MemoryRefAndLayerArgParityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_memref_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        DefinitionLoader.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Console : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "console";
        public IScriptObj? GetSourceChar() => null;
    }

    private static (GameWorld World, Character Owner, Character Pet) PetWorld()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = world.CreateCharacter();
        pet.Name = "Wolf";
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));
        return (world, owner, pet);
    }

    private static string Get(Character ch, string key)
    {
        Assert.True(ch.TryGetProperty(key, out var value), key);
        return value;
    }

    [Fact]
    public void NumericMemoryType_IsHex_AndFollowsTheLink()
    {
        var (_, _, pet) = PetWorld();

        Assert.Equal("Owner", Get(pet, "MEMORYFINDTYPE.02.LINK.NAME"));   // 02 = MEMORY_IPET
        Assert.Equal("Owner", Get(pet, "MEMORYFINDTYPE.memory_ipet.LINK.NAME"));
    }

    [Fact]
    public void MemoryFind_TakesTheLinkedUid()
    {
        var (_, owner, pet) = PetWorld();

        Assert.Equal("Owner", Get(pet, $"MEMORYFIND.0{owner.Uid.Value:X}.LINK.NAME"));
        Assert.Equal("0", Get(pet, "MEMORYFIND.0DEAD.ISVALID"));
    }

    [Fact]
    public void TheMemoryRefIsTheMemoryItem_WithItsOwnUidAndFlags()
    {
        var (world, owner, pet) = PetWorld();

        string memUid = Get(pet, "MEMORYFINDTYPE.02");
        var memory = world.FindObject(new Serial(Convert.ToUInt32(memUid, 16))) as Item;
        Assert.NotNull(memory);
        Assert.Equal(ItemType.EqMemoryObj, memory!.ItemType);
        Assert.NotEqual(owner.Uid, memory.Uid);
        Assert.Equal(memUid, Get(pet, "MEMORYFINDTYPE.02.UID"));
        // Source-X keeps the memory flags in the item's colour (GetMemoryTypes()).
        Assert.True(memory.TryGetProperty("COLOR", out var color));
        Assert.Equal(color, Get(pet, "MEMORYFINDTYPE.02.COLOR"));
        Assert.Equal(MemoryType.IPet, memory.GetMemoryTypes() & MemoryType.IPet);
    }

    [Fact]
    public void RemovingTheOwnerMemory_ThroughTheRef_ClearsThePetFlag()
    {
        var (world, _, pet) = PetWorld();
        var memory = pet.FindMemoryByTypeArg("02")!;

        Assert.True(pet.TryExecuteCommand("MEMORYFINDTYPE.02.REMOVE", "", new Console()));

        Assert.Null(pet.FindMemoryByTypeArg("02"));
        Assert.Null(world.FindObject(memory.Uid));
        Assert.False(pet.IsStatFlag(StatFlag.Pet));
    }

    [Fact]
    public void GuildMemory_LinksTheStone_AndItsPosition()
    {
        var world = TestHarness.CreateWorld();
        var stone = world.CreateItem();
        stone.BaseId = 0x0EDD;
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(200, 200, 0, 0));
        var leader = world.CreateCharacter();
        leader.IsPlayer = true;
        world.PlaceCharacter(leader, new Point3D(100, 100, 0, 0));
        leader.Memory_AddObjTypes(stone.Uid, MemoryType.Guild);

        Assert.Equal($"0{stone.Uid.Value:X8}", Get(leader, "MEMORYFINDTYPE.0400.LINK"));
        Assert.True(stone.TryGetProperty("P", out var stonePos));
        Assert.Equal(stonePos, Get(leader, "MEMORYFINDTYPE.memory_guild.LINK.P"));
    }

    /// <summary>A memory a script was handed has a UID, but it still persists only
    /// through its owner's MEMORY= record - a WORLDITEM for it as well would load
    /// back as a second, stray item.</summary>
    [Fact]
    public void ARegisteredMemoryIsSavedOnlyThroughItsOwner()
    {
        var (world, _, pet) = PetWorld();
        var memory = pet.FindMemoryByTypeArg("02")!;
        Assert.True(memory.Uid.IsValid);

        Directory.CreateDirectory(_dir);
        var saver = new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SphereNet.Core.Configuration.SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(world, _dir));
        string saved = string.Concat(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain($"SERIAL=0{memory.Uid.Value:X8}", saved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MEMORY=", saved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindLayerArgument_WithALeadingZero_IsHex()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = ch.Dex = ch.Int = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var cape = world.CreateItem();
        cape.BaseId = 0x1515;
        Assert.True(ch.Equip(cape, Layer.Cape));   // layer 20
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Strength, Name = "Strength",
            Flags = SpellFlag.Bless | SpellFlag.TargChar, DurationBase = 600, EffectBase = 10,
        });
        var engine = new SpellEngine(world, registry);
        engine.ApplyDirectEffect(ch, ch, SpellType.Strength, 500);
        var memory = ch.FindLayer(SpellLayers.Stats)!;

        Assert.Equal($"0{memory.Uid.Value:X8}", Get(ch, "FINDLAYER(020)"));
        Assert.True(ch.TryExecuteCommand("FINDLAYER.020.COLOR", "0455", new Console()));

        Assert.Equal(0x455, (ushort)memory.Hue);
        Assert.NotEqual(0x455, (ushort)cape.Hue);
    }

    [Fact]
    public void FindLayerParenForm_AcceptsALayerDefname()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "layers.scp");
        File.WriteAllLines(file, ["[DEFNAME layers]", "layer_cape 20"]);
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var cape = world.CreateItem();
        cape.BaseId = 0x1515;
        Assert.True(ch.Equip(cape, Layer.Cape));

        Assert.Equal($"0{cape.Uid.Value:X8}", Get(ch, "FINDLAYER(layer_cape)"));
        Assert.True(ch.TryExecuteCommand("FINDLAYER(layer_cape).COLOR", "0455", new Console()));
        Assert.Equal(0x455, (ushort)cape.Hue);
    }
}
