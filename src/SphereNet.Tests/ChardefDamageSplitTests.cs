using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A creature deals the elemental damage its CHARDEF declares, however it was born.
///
/// The split was written out twice - once in the spawner, once in the client's NPC
/// creation - and NOT in the shared helper both of them call for the rest of the
/// definition. So a creature born any other way dealt pure physical damage however
/// much elemental its chardef declared: a GM .add, a script NEWNPC, anything reaching
/// TryApplyDefName. The shipped packs declare it on about forty creatures per element
/// (DAMENERGY 44, DAMFIRE 36, DAMCOLD 35, DAMPOISON 35).
///
/// An unset DAMPHYSICAL is the remainder the elemental percents leave of 100 (Source-X
/// OnTakeDamage), not the 100 a pure-physical creature keeps.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ChardefDamageSplitTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_ds_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Character FromDef(params string[] defLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "c.scp");
        var lines = new List<string> { "[CHARDEF c_probe_drake]", "ID=0003c", "NAME=probe drake" };
        lines.AddRange(defLines);
        File.WriteAllLines(file, lines);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        Assert.True(CharDefHelper.TryApplyDefName(ch, "c_probe_drake", resources));
        world.PlaceCharacter(ch, new Point3D(120, 120, 0, 0));
        return ch;
    }

    /// <summary>The path that had no split at all.</summary>
    [Fact]
    public void ADefinitionsElementalShareReachesTheCreature()
    {
        var ch = FromDef("DAMFIRE=20", "DAMENERGY=30");

        Assert.Equal(20, ch.DamFire);
        Assert.Equal(30, ch.DamEnergy);
        // 100 less what the elements took, not the pure-physical 100.
        Assert.Equal(50, ch.DamPhysical);
    }

    /// <summary>A declared DAMPHYSICAL is taken as written, remainder or not.</summary>
    [Fact]
    public void ADeclaredPhysicalShareWins()
    {
        var ch = FromDef("DAMFIRE=20", "DAMPHYSICAL=70");

        Assert.Equal(20, ch.DamFire);
        Assert.Equal(70, ch.DamPhysical);
    }

    /// <summary>A creature declaring none keeps the pure-physical default.</summary>
    [Fact]
    public void ACreatureWithNoElementalShareStaysPhysical()
    {
        var ch = FromDef("STR=100");

        Assert.Equal(0, ch.DamFire);
        Assert.Equal(100, ch.DamPhysical);
    }

    /// <summary>Elements summing past 100 leave no negative physical share.</summary>
    [Fact]
    public void OversubscribedElementsDoNotGoNegative()
    {
        var ch = FromDef("DAMFIRE=60", "DAMCOLD=60");

        Assert.Equal(0, ch.DamPhysical);
    }

    /// <summary>And it survives a save, because the split is stored on the character
    /// rather than re-derived from the definition at load.</summary>
    [Fact]
    public void ItOutlivesASave()
    {
        var ch = FromDef("DAMFIRE=20", "DAMENERGY=30");
        ch.Name = "probe drake";

        string sdir = Path.Combine(_dir, "save");
        Directory.CreateDirectory(sdir);
        var live = ObjBase.ResolveWorld!.Invoke()!;
        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(live, sdir);

        var reloaded = new GameWorld(LoggerFactory.Create(_ => { }));
        reloaded.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => reloaded;
        Item.ResolveWorld = () => reloaded;
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, sdir);

        var back = reloaded.GetAllCharactersSnapshot().FirstOrDefault(c => c.Name == "probe drake");
        Assert.NotNull(back);
        Assert.Equal(20, back!.DamFire);
        Assert.Equal(30, back.DamEnergy);
        Assert.Equal(50, back.DamPhysical);
    }
}
