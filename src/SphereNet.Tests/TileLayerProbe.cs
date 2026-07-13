using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class TileLayerProbe
{
    private readonly ITestOutputHelper _out;
    public TileLayerProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void EndToEnd_RealPackRealTiledata_DressesNpc()
    {
        const string tilePath = @"C:\UOSoft\Spherenet\tiledata.mul";
        string? packPath = ScriptTestBootstrap.GetExternalScriptPackPath();
        if (!File.Exists(tilePath) || packPath == null) { _out.WriteLine("missing pack or tiledata"); return; }

        // Real definitions
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        var files = ScriptTestBootstrap.GetScriptFiles(packPath, ScriptPackProfile.Full);
        ScriptTestBootstrap.LoadFiles(stack.Resources, files);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        // Real tiledata
        var map = new MapDataManager(Path.GetDirectoryName(tilePath)!);
        map.Load();
        _out.WriteLine($"tiledata robe Quality = {map.GetItemTileData(0x1F03).Quality}");

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.ResolveDefName = defname =>
        {
            var rid = stack.Resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };

        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using (var w = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                w.BeginRecord("WORLDCHAR");
                w.WriteProperty("SERIAL", "01000101");
                w.WriteProperty("BODY", "0190");
                w.WriteProperty("P", "1000,1000,0");
                w.EndRecord();

                // Exactly how the 56T save stores worn gear: defname header,
                // CONT to the char, NO LAYER, NO ID.
                string[] worn = { "i_robe", "i_backpack", "i_shoes_plain", "i_torch",
                                  "i_fishing_pole", "i_hair_ponytail" };
                uint serial = 0x040000101;
                foreach (string def in worn)
                {
                    w.BeginRecord($"WORLDITEM {def}");
                    w.WriteProperty("SERIAL", serial.ToString("X"));
                    w.WriteProperty("CONT", "01000101");
                    w.EndRecord();
                    serial++;
                }
            }

            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
            loader.ResolveItemDef = defname =>
            {
                var rid = stack.Resources.ResolveDefName(defname);
                if (rid.IsValid && rid.Type == ResType.ItemDef)
                {
                    var def = DefinitionLoader.GetItemDef(rid.Index);
                    return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
                }
                return 0;
            };
            loader.ResolveEquipLayerFromTile = baseId => map.GetItemTileData(baseId).Quality;

            loader.Load(world, tmp);

            var ch = world.FindChar(new Serial(0x1000101));
            Assert.NotNull(ch);
            // Nothing should have fallen to the ground under the NPC.
            int onGround = world.GetAllObjects().OfType<Item>()
                .Count(i => !i.ContainedIn.IsValid && i.Position.X == 1000 && i.Position.Y == 1000);
            _out.WriteLine($"items on ground under NPC: {onGround}");
            _out.WriteLine($"robe layer22={ch!.GetEquippedItem((Layer)22)!=null} shoes layer3={ch.GetEquippedItem((Layer)3)!=null} " +
                $"pack={ch.Backpack!=null} hair layer11={ch.GetEquippedItem((Layer)11)!=null}");
            Assert.NotNull(ch.GetEquippedItem((Layer)22));  // robe
            Assert.NotNull(ch.Backpack);
            Assert.Equal(0, onGround);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
