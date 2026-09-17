using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A training aid's own practice cap.
///
/// Upstream reads OVERRIDE.PracticeMax.SKILL_&lt;n&gt; off the ITEM before falling back
/// to the shard's SKILLPRACTICEMAX (CCharUse.cpp:369 and 531). The key is built by
/// string concatenation, which is why it appears as a literal nowhere in the
/// reference source and was easy to miss here: this engine read only the global, so
/// the live pack's gothic training dummies - which declare 60.0 for each weapon skill
/// - stopped teaching at the shard default of 30.0 like every other dummy.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PracticeMaxOverrideTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_pm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private static int PracticeMaxFor(Item aid, SkillType skill)
    {
        var m = typeof(SphereNet.Game.Clients.ClientItemUseHandler)
            .GetMethod("PracticeMaxFor", System.Reflection.BindingFlags.Static |
                                         System.Reflection.BindingFlags.NonPublic)!;
        return (int)m.Invoke(null, [aid, skill])!;
    }

    private Item DummyWith(string defBody)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllText(file,
            "[ITEMDEF 01286]" + Nl +
            "DEFNAME=i_probe_dummy" + Nl +
            "TYPE=t_train_dummy" + Nl +
            defBody);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var dummy = world.CreateItem();
        dummy.BaseId = 0x1286;
        return dummy;
    }

    /// <summary>With no key the aid uses the shard cap, which is what every plain
    /// dummy relies on.</summary>
    [Fact]
    public void APlainAidUsesTheShardCap()
    {
        var dummy = DummyWith("");
        Assert.Equal(SphereNet.Game.Clients.ClientItemUseHandler.SkillPracticeMax,
            PracticeMaxFor(dummy, SkillType.Swordsmanship));
    }

    /// <summary>The pack's own line: 60.0 for swordsmanship, read off the definition,
    /// and per skill - the skills it does not name keep the shard cap.</summary>
    [Fact]
    public void ADeclaredCapWinsForTheSkillItNames()
    {
        var dummy = DummyWith("TAG.OVERRIDE.PRACTICEMAX.SKILL_40=60.0" + Nl);

        Assert.Equal(600, PracticeMaxFor(dummy, SkillType.Swordsmanship));
        Assert.Equal(SphereNet.Game.Clients.ClientItemUseHandler.SkillPracticeMax,
            PracticeMaxFor(dummy, SkillType.Fencing));
    }

    /// <summary>And an instance tag wins over the definition, the way the key chain
    /// runs upstream.</summary>
    [Fact]
    public void AnInstanceTagWinsOverTheDefinition()
    {
        var dummy = DummyWith("TAG.OVERRIDE.PRACTICEMAX.SKILL_40=60.0" + Nl);
        dummy.SetTag("OVERRIDE.PRACTICEMAX.SKILL_40", "90.0");

        Assert.Equal(900, PracticeMaxFor(dummy, SkillType.Swordsmanship));
    }
}
