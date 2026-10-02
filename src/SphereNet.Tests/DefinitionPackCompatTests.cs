using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The definition changes measured on real packs: a Sphere 56T custom-version pack
/// (C:\56T\scripts, or SPHERENET_56T_DIR) and a Source-X pack (SPHERENET_SOURCEX_DIR,
/// or oldSphere/sphere-x). Both skip cleanly when the data is absent.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DefinitionPackCompatTests
{
    private readonly ITestOutputHelper _out;
    public DefinitionPackCompatTests(ITestOutputHelper output) => _out = output;

    private static string? Pack56T()
    {
        string root = Environment.GetEnvironmentVariable("SPHERENET_56T_DIR") ?? @"C:\56T";
        string scripts = Path.Combine(root, "scripts");
        return Directory.Exists(scripts) ? scripts : null;
    }

    private static string? PackSourceX()
    {
        string? root = Environment.GetEnvironmentVariable("SPHERENET_SOURCEX_DIR") ??
            TestRepo.Optional("oldSphere/sphere-x");
        if (root == null) return null;
        string scripts = Path.Combine(root, "scripts");
        return Directory.Exists(scripts) ? scripts : null;
    }

    private static ResourceHolder LoadPack(string root)
    {
        var holder = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = root
        };
        var files = ScriptResourceManifest.Resolve(root, _ => { });
        foreach (var f in files) holder.RegisterKnownResourceFile(f);
        foreach (var f in files) holder.LoadResourceFile(f);
        var pending = holder.DrainPendingResourceFiles();
        while (pending.Count > 0)
        {
            foreach (var f in pending) holder.LoadResourceFile(f);
            pending = holder.DrainPendingResourceFiles();
        }
        new DefinitionLoader(holder, new SpellRegistry()).LoadAll();
        return holder;
    }

    [Fact]
    public void Sphere56TPack_DefinitionsInheritAndSpawnsResolve()
    {
        string? scripts = Pack56T();
        if (Gate.Missing(_out, "external script pack", scripts == null)) return;
        var holder = LoadPack(scripts!);

        // Spawn members written "c_name  weight" all name a creature.
        int members = 0, unresolved = 0;
        foreach (var link in holder.GetAllResources())
        {
            if (link is not SpawnGroupDef group) continue;
            foreach (var (name, _) in group.Members)
            {
                members++;
                var rid = holder.ResolveDefName(name);
                bool numeric = SphereNet.Core.Types.ScriptNumber.TryParseToken(name, out long n) &&
                    DefinitionLoader.GetCharDef((int)n) != null;
                if (!numeric && !(rid.IsValid && rid.Type is ResType.CharDef or ResType.Spawn))
                    unresolved++;
            }
        }
        _out.WriteLine($"spawn members {members}, unresolved {unresolved}");
        Assert.True(members > 1000);
        Assert.True(unresolved * 50 < members, $"{unresolved} of {members} spawn members name nothing");

        // A creature copying another body's basics has that body's sound.
        int copied = 0, silent = 0;
        foreach (var (_, def) in DefinitionLoader.AllCharDefs)
        {
            if (!def.HasIdBase) continue;
            copied++;
            if (def.SoundBase == 0) silent++;
        }
        _out.WriteLine($"chardefs with ID= base {copied}, silent {silent}");
        Assert.True(copied > 100);
        Assert.True(silent * 4 < copied);

        // Named weapons on an ID= base carry its damage.
        int weapons = 0, harmless = 0;
        foreach (var (_, def) in DefinitionLoader.AllItemDefs)
        {
            if (!def.HasIdBase || def.Type is < ItemType.WeaponMaceSmith or > ItemType.WeaponFence) continue;
            weapons++;
            if (def.AttackMax == 0) harmless++;
        }
        _out.WriteLine($"ID= weapons {weapons}, without damage {harmless}");
        Assert.True(weapons > 50);
        Assert.True(harmless * 10 < weapons);

        // [STARTS] replaces its list per section.
        Assert.NotEmpty(holder.Starts);
        Assert.All(holder.Starts, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
    }

    [Fact]
    public void SourceXPack_DefinitionsKeepTheirShape()
    {
        string? scripts = PackSourceX();
        if (Gate.Missing(_out, "external script pack", scripts == null)) return;
        var holder = LoadPack(scripts!);

        int stubs = 0, redirected = 0;
        foreach (var (_, def) in DefinitionLoader.AllItemDefs)
        {
            if (def.DupItemId == 0) continue;
            stubs++;
            if (def.DupeMasterIndex != 0) redirected++;
        }
        _out.WriteLine($"DUPEITEM stubs {stubs}, sharing their master {redirected}");
        Assert.True(stubs > 1000);
        Assert.True(redirected * 10 > stubs * 9);

        // A numbered weapon's damage is its own section's.
        int weapons = 0;
        foreach (var (index, def) in DefinitionLoader.AllItemDefs)
        {
            if (index > 0xFFFF || def.AttackMax == 0 || def.DupeMasterIndex != 0 ||
                def.Type is < ItemType.WeaponMaceSmith or > ItemType.WeaponFence) continue;
            weapons++;
            Assert.Equal((def.AttackMin, Math.Max(def.AttackMin, def.AttackMax)),
                CombatEngine.WeaponDamageFromDefinition((ushort)index));
        }
        Assert.True(weapons > 50);
        Assert.NotEmpty(holder.Starts);
    }
}
