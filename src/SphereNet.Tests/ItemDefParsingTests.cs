using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

public class ItemDefParsingTests
{
    [Fact]
    public void LoadFromKey_Id_ParsesBareSphereHex()
    {
        var def = new ItemDef(ResourceId.Invalid);

        def.LoadFromKey("ID", "0F5E");

        Assert.Equal(0x0F5E, def.DispIndex);
    }

    [Fact]
    public void LoadFromKey_Dam_ParsesBraceRange()
    {
        var def = new ItemDef(ResourceId.Invalid);

        def.LoadFromKey("DAM", "{10 20}");

        Assert.Equal(10, def.AttackMin);
        Assert.Equal(20, def.AttackMax);
    }

    [Fact]
    public void LoadFromKey_Type_ParsesWeaponSword()
    {
        var def = new ItemDef(ResourceId.Invalid);

        def.LoadFromKey("TYPE", "t_weapon_sword");

        Assert.Equal(ItemType.WeaponSword, def.Type);
    }
}
