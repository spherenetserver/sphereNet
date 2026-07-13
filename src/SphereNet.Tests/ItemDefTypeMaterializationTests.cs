using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A legacy save stores neither TYPE nor TDATA — an item's type is defined by its
/// ITEMDEF. The raw _type field therefore stayed Normal on a loaded instance, so
/// the &lt;TYPE&gt; script read and the ~20 code paths that read _type directly
/// (IsStaticBlock, spellbook/book/map/ship/multi/container checks) saw t_normal,
/// even though the lazy ItemType getter resolved correctly. Fix: the TYPE property
/// resolves through the def, and MaterializeDefinitionType (run after load) copies
/// the def's TYPE/TDATA onto the instance so a legacy item behaves like a
/// script-created one.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemDefTypeMaterializationTests
{
    [Fact]
    public void TypeProperty_Resolves_AndMaterializeCopiesTypeOntoField()
    {
        var lf = LoggerFactory.Create(_ => { });
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>());

        // Numeric-header ITEMDEF (index == graphic 0x64) so a bare BaseId resolves
        // the def directly, with a type that a raw-_type reader (IsStaticBlock)
        // keys on.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_typemat_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, "[ITEMDEF 064]\r\nDEFNAME=i_test_door\r\nTYPE=t_door\r\n");
        try
        {
            res.LoadResourceFile(tmp);
            new DefinitionLoader(res, new SpellRegistry()).LoadAll();

            var def = DefinitionLoader.GetItemDef(0x64);
            Assert.NotNull(def);
            Assert.Equal(ItemType.Door, def!.Type);

            var world = new GameWorld(lf);
            world.InitMap(0, 6144, 4096);
            ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            var item = world.CreateItem();
            item.BaseId = 0x64; // legacy graphic only; raw _type left at Normal

            // The gap: a direct _type read misses the door before materialization.
            Assert.False(item.IsStaticBlock);

            // Fix 1: the <TYPE> property resolves through the def regardless of _type.
            Assert.True(item.TryGetProperty("TYPE", out string typeVal));
            Assert.Equal("t_door", typeVal);

            // Fix 2: materialization copies the def type onto the field, so the raw
            // read now sees the door — exactly as a script-created item would.
            item.MaterializeDefinitionType();
            Assert.True(item.IsStaticBlock);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
