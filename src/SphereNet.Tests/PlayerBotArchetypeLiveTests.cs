using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The player bot archetypes (playerbots/scripts) made against the Scripts-X pack they
/// are written for: the clothes a COLOR= line follows are dyed, so a crowd of bots is
/// not one colour. Skips when the pack is not in the repository.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotArchetypeLiveTests(ITestOutputHelper outp)
{
    /// <summary>The Scripts-X pack (gitignored reference tree), or null.</summary>
    private static string? Pack() => TestRepo.Optional("oldSphere/Scripts-X-main");

    private static (GameWorld World, TriggerDispatcher Dispatcher, ResourceHolder Resources) Load(string pack)
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = pack };
        foreach (string file in Directory.EnumerateFiles(pack, "*.scp", SearchOption.AllDirectories))
            resources.LoadResourceFile(file);
        foreach (string file in Directory.EnumerateFiles(TestRepo.Tracked("playerbots/scripts"), "*.scp"))
            resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.ResolveDefName = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            if (!rid.IsValid || rid.Type != ResType.ItemDef) return 0;
            var d = DefinitionLoader.GetItemDef(rid.Index);
            return d != null && d.DispIndex > 0 ? d.DispIndex : (ushort)rid.Index;
        };
        return (world, dispatcher, resources);
    }

    /// <summary>Every archetype comes out armed, every item it names exists in the
    /// pack, and a crowd of one archetype is not dressed alike.</summary>
    [Theory]
    [InlineData("c_playerbot_warrior_m", 10)]
    [InlineData("c_playerbot_warrior_f", 10)]
    [InlineData("c_playerbot_archer_m", 8)]
    [InlineData("c_playerbot_archer_f", 8)]
    [InlineData("c_playerbot_mage_m", 8)]
    [InlineData("c_playerbot_mage_f", 8)]
    [InlineData("c_playerbot_townsfolk_m", 8)]
    [InlineData("c_playerbot_townsfolk_f", 8, false)]
    public void ArchetypesComeOutArmedAndVaried(string archetype, int minDistinct, bool armed = true)
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var (world, dispatcher, resources) = Load(pack!);
        try
        {
            var rid = resources.ResolveDefName(archetype);
            Assert.True(rid.IsValid);
            var outfits = new HashSet<string>();
            for (int i = 0; i < 15; i++)
            {
                var ch = world.CreateCharacter();
                ch.CharDefIndex = rid.Index;
                ch.BaseId = 0x190; ch.BodyId = 0x190;
                world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
                dispatcher.FireNpcLoadScript(ch, restock: true);
                // No tiledata here, so what would be worn may sit in the pack.
                var things = Enum.GetValues<Layer>().Select(ch.GetEquippedItem).OfType<Item>()
                    .Concat(world.GetContainerContents(ch.Backpack?.Uid ?? Serial.Invalid))
                    .Where(it => it.BaseId != 0x0E75)
                    .ToList();
                Assert.All(things, it => Assert.NotEqual(0, it.BaseId));
                if (armed) Assert.Contains(things, it => it.ItemType is ItemType.WeaponSword or ItemType.WeaponMaceSharp or
                    ItemType.WeaponMaceSmith or ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or
                    ItemType.WeaponAxe or ItemType.WeaponFence or ItemType.WeaponBow or ItemType.WeaponXBow or
                    ItemType.Spellbook || it.BaseId is 0x0F51 or 0x0F52);
                outfits.Add(string.Join(",", things.Where(it => it.Amount <= 1)
                    .Select(it => $"{it.BaseId:X4}/{it.Hue.Value:X4}").Order()));
            }
            outp.WriteLine($"{archetype}: {outfits.Count} distinct of 15");
            Assert.True(outfits.Count >= minDistinct, $"{archetype}: only {outfits.Count} distinct outfits of 15");
        }
        finally
        {
            ObjBase.ResolveWorld = null;
            Item.ResolveWorld = null;
            Item.ResolveDefName = null;
        }
    }

    /// <summary>Every item an archetype names is an ITEMDEF of the pack: one that is not
    /// is skipped without a word when the bot is made.</summary>
    [Fact]
    public void EveryItemTheArchetypesNameExistsInThePack()
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var (_, _, resources) = Load(pack!);
        try
        {
            var names = new HashSet<string>();
            foreach (string file in Directory.EnumerateFiles(TestRepo.Tracked("playerbots/scripts"), "*.scp"))
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"\bi_[a-z0-9_]+"))
                    names.Add(m.Value);
            Assert.True(names.Count > 30, $"only {names.Count} item names read");
            var missing = names.Where(n =>
            {
                var rid = resources.ResolveDefName(n);
                return !rid.IsValid || rid.Type != ResType.ItemDef;
            }).Order().ToList();
            Assert.True(missing.Count == 0, "not in the pack: " + string.Join(", ", missing));
        }
        finally
        {
            ObjBase.ResolveWorld = null;
            Item.ResolveWorld = null;
            Item.ResolveDefName = null;
        }
    }

    [Fact]
    public void ArchetypeClothesFollowingAColourLineAreDyed()
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var (world, dispatcher, resources) = Load(pack!);
        try
        {
            var rid = resources.ResolveDefName("c_playerbot_mage_m");
            Assert.True(rid.IsValid);
            int dyed = 0, robes = 0;
            for (int i = 0; i < 20; i++)
            {
                var ch = world.CreateCharacter();
                ch.CharDefIndex = rid.Index;
                ch.BaseId = 0x190; ch.BodyId = 0x190;
                world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
                dispatcher.FireNpcLoadScript(ch, restock: true);
                // No tiledata here, so worn things may sit in the pack: find the robe anywhere.
                var robe = ch.GetEquippedItem(Layer.Robe) ??
                           world.GetContainerContents(ch.Backpack?.Uid ?? Serial.Invalid).FirstOrDefault(i => i.BaseId == 0x1F03);
                outp.WriteLine($"robe hue {robe?.Hue.Value:X4}");
                if (robe == null) continue;
                robes++;
                if (robe.Hue.Value != 0) dyed++;
            }
            Assert.Equal(20, robes);
            Assert.Equal(20, dyed);
        }
        finally
        {
            ObjBase.ResolveWorld = null;
            Item.ResolveWorld = null;
            Item.ResolveDefName = null;
        }
    }
}
