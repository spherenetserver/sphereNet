using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Persistence.Load;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ItemDefSyntheticIndexProbe
{
    private readonly ITestOutputHelper _out;
    public ItemDefSyntheticIndexProbe(ITestOutputHelper o) => _out = o;

    // Confirms the "type/more come out 0" report: an ITEMDEF with a NON-numeric
    // header ([ITEMDEF i_xxx]) is keyed in _itemDefs by a synthetic index > 0xFFFF,
    // but a legacy [WORLDITEM i_xxx] load stores only the 16-bit BaseId graphic and
    // no ITEMDEF/SCRIPTDEF tag — so ResolveDefinition() falls back to
    // GetItemDef(BaseId), which cannot find the synthetic-keyed def. Type/TData/More
    // then resolve to 0 exactly like Spawn_FFFF's body clamp.
    [Fact]
    public void RealPack_NonNumericItemDefs_AreNotFoundByDispGraphic()
    {
        const string scripts = @"C:\sphereNetServer\scripts";
        if (!Directory.Exists(scripts)) { _out.WriteLine("no scripts"); return; }

        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = scripts };
        foreach (var f in ScriptResourceManifest.Resolve(scripts)) res.LoadResourceFile(f);
        new DefinitionLoader(res, new SpellRegistry()).LoadAll();

        int synthetic = 0, syntheticWithType = 0, dispMiss = 0;
        var examples = new System.Collections.Generic.List<string>();
        foreach (var (index, def) in DefinitionLoader.AllItemDefs)
        {
            if (index <= 0xFFFF) continue; // numeric-header def, BaseId == key, fine
            synthetic++;
            bool hasMeta = def.Type != ItemType.Normal || def.TData1 != 0 ||
                           def.TData2 != 0 || def.TData3 != 0 || def.TData4 != 0;
            if (!hasMeta) continue;
            syntheticWithType++;

            // What a legacy item would resolve its Type through: GetItemDef(BaseId),
            // where BaseId = DispIndex (the graphic). If that misses the def, the
            // instance reads Type/TData 0.
            ushort disp = def.DispIndex;
            var viaDisp = disp != 0 ? DefinitionLoader.GetItemDef(disp) : null;
            if (viaDisp == null || viaDisp.Type != def.Type)
            {
                dispMiss++;
                if (examples.Count < 20)
                    examples.Add($"idx=0x{index:X} '{def.DefName}' disp=0x{disp:X} type={def.Type} " +
                        $"-> GetItemDef(disp)={(viaDisp == null ? "NULL" : viaDisp.Type.ToString())}");
            }
        }

        _out.WriteLine($"synthetic-index itemdefs (non-numeric header) = {synthetic}");
        _out.WriteLine($"  ...carrying a Type/TData                    = {syntheticWithType}");
        _out.WriteLine($"  ...whose Type is LOST via GetItemDef(disp)  = {dispMiss}");
        foreach (var e in examples) _out.WriteLine("  " + e);
    }
}
