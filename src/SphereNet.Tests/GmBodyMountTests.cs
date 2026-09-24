using SphereNet.Game.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>CREID_EQUIP_GM_ROBE (0x3DB, the c_man_gm body) is a human body upstream
/// (CCharBase::IsHumanID), so a GM in it may ride; it was refused every mount.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GmBodyMountTests
{
    [Theory]
    [InlineData(0x0190, true)]
    [InlineData(0x0191, true)]
    [InlineData(0x03DB, true)]
    [InlineData(0x00C8, false)] // a horse cannot ride a horse
    public void MountCapableBodies(int body, bool capable)
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.BodyId = (ushort)body;

        Assert.Equal(capable, CharDefHelper.IsMountCapable(ch));
    }
}
