using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A legacy [WORLDITEM i_xxx] whose ITEMDEF has a non-numeric header (keyed by a
/// synthetic index above 0xFFFF) must still resolve its Type/TData/MORE. The old
/// loader kept only the 16-bit graphic BaseId, so ResolveInstanceDefIndex fell back
/// to GetItemDef(BaseId) — a different def or none — and the instance read Type 0
/// (the same 0xFFFF-truncation class as the Spawn_FFFF body bug). The loader now
/// pins the source defname as an ITEMDEF tag so the real def is recovered.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LegacyItemSyntheticTypeTests
{
    private readonly ITestOutputHelper _out;
    public LegacyItemSyntheticTypeTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void LegacyItem_WithNonNumericItemDef_ResolvesRealType()
    {
        const string scripts = @"C:\sphereNetServer\scripts";
        if (!Directory.Exists(scripts)) { _out.WriteLine("no scripts"); return; }

        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (var f in ScriptResourceManifest.Resolve(scripts)) res.LoadResourceFile(f);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        // A def whose header is non-numeric (synthetic index > 0xFFFF) and whose
        // graphic maps to a DIFFERENT def. Verified by ItemDefSyntheticIndexProbe:
        // I_POUCH_TRAPPED is Type=Script but its graphic 0x9B0 resolves to Container.
        const string defName = "i_pouch_trapped";
        var rid = res.ResolveDefName(defName);
        Assert.True(rid.IsValid && rid.Type == ResType.ItemDef, "test def missing from pack");
        var def = DefinitionLoader.GetItemDef(rid.Index);
        Assert.NotNull(def);
        _out.WriteLine($"{defName}: index=0x{rid.Index:X} disp=0x{def!.DispIndex:X} type={def.Type}");
        Assert.True(rid.Index > 0xFFFF, "expected a synthetic index for the regression to be meaningful");
        Assert.NotEqual(ItemType.Normal, def.Type);

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.ResolveDefName = ResolveGraphic;

        ushort ResolveGraphic(string dn)
        {
            var r = res.ResolveDefName(dn);
            if (r.IsValid && r.Type == ResType.ItemDef)
            {
                var d = DefinitionLoader.GetItemDef(r.Index);
                return d != null && d.DispIndex > 0 ? d.DispIndex : (ushort)r.Index;
            }
            return 0;
        }

        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_syntype_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using (var w = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                w.BeginRecord($"WORLDITEM {defName}");
                w.WriteProperty("SERIAL", "40000201");
                w.WriteProperty("P", "1000,1000,0");
                w.EndRecord();
            }

            var loader = new WorldLoader(lf)
            {
                ResolveItemDef = ResolveGraphic,
                ResolveItemDefFullIndex = dn =>
                {
                    var r = res.ResolveDefName(dn);
                    return r.IsValid && r.Type == ResType.ItemDef ? r.Index : 0;
                },
            };
            loader.Load(world, tmp);

            var item = world.GetAllObjects().OfType<Item>().FirstOrDefault();
            Assert.NotNull(item);
            _out.WriteLine($"loaded item: baseId=0x{item!.BaseId:X} type={item.ItemType}");
            // The fix: Type comes from the real synthetic-keyed def, not the graphic.
            Assert.Equal(def.Type, item.ItemType);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
