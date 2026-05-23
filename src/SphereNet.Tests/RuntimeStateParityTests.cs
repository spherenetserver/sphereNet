using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

public class RuntimeStateParityTests
{
    [Fact]
    public void StatLock_UsesNativeFields_NotTags()
    {
        var ch = new Character();
        ch.TrySetProperty("STATLOCK.0", "2");
        ch.TrySetProperty("STATLOCK.1", "1");

        Assert.Equal(2, ch.GetStatLock(0));
        Assert.Equal(1, ch.GetStatLock(1));
        Assert.True(ch.TryGetProperty("StatLock[0]", out string bracketValue));
        Assert.Equal("2", bracketValue);
        Assert.False(ch.TryGetTag("STATLOCK.0", out _));
    }

    [Fact]
    public void StatLock_MigrateFromLegacyTags()
    {
        var ch = new Character();
        ch.SetTag("STATLOCK.2", "1");

        Assert.Equal(1, ch.GetStatLock(2));
        Assert.False(ch.TryGetTag("STATLOCK.2", out _));
    }

    [Fact]
    public void StatLock_TagWrite_RoutesToNativeField()
    {
        var ch = new Character();

        ch.SetTag("STATLOCK.0", "2");

        Assert.Equal(2, ch.GetStatLock(0));
        Assert.True(ch.TryGetProperty("TAG.STATLOCK.0", out string tagValue));
        Assert.Equal("0", tagValue);
        Assert.False(ch.TryGetTag("STATLOCK.0", out _));
    }

    [Fact]
    public void CastState_UsesNativeFields()
    {
        var ch = new Character();
        ch.BeginCast(SpellType.Heal, new Serial(0x40000001), new Point3D(100, 200, 5, 0));
        ch.SetCastTimerEnd(Environment.TickCount64 + 5000);
        ch.SpellPrecast = true;

        Assert.True(ch.IsCasting);
        Assert.True(ch.TryGetCastingSpell(out SpellType spell));
        Assert.Equal(SpellType.Heal, spell);
        Assert.True(ch.IsCastTimerActive(Environment.TickCount64));
        Assert.Equal(new Serial(0x40000001), ch.CastTargetUid);

        ch.ClearCastState();
        Assert.False(ch.IsCasting);
        Assert.False(ch.SpellPrecast);
    }

    [Fact]
    public void SkillPending_UsesNativeFields()
    {
        var ch = new Character();
        ch.BeginSkillPending(17, 5000, 1000, new Serial(0x40000002), new Point3D(10, 20, 0, 0));

        Assert.True(ch.HasActiveSkillPending());
        Assert.Equal(17, ch.SkillPendingId);
        Assert.True(ch.TryGetSkillPendingPoint(out Point3D pt));
        Assert.Equal(10, pt.X);

        Assert.Equal(17, ch.ClearActiveSkillPending());
        Assert.False(ch.HasActiveSkillPending());
    }

    [Fact]
    public void Item_RuneMark_UsesMoreP()
    {
        var item = new Item();
        item.SetRuneMark(new Point3D(200, 210, 5, 0));

        Assert.True(item.TryGetRuneMark(out Point3D mark));
        Assert.Equal(200, mark.X);
        Assert.Equal(210, mark.Y);
        Assert.False(item.TryGetTag("RUNE_X", out _));
    }

    [Fact]
    public void Item_MigrateRuneFromTags()
    {
        var item = new Item();
        item.SetTag("RUNE_X", "150");
        item.SetTag("RUNE_Y", "160");
        item.SetTag("RUNE_Z", "3");
        item.SetTag("RUNE_MAP", "1");

        item.MigrateRuneFromTags();

        Assert.True(item.TryGetRuneMark(out Point3D mark));
        Assert.Equal(150, mark.X);
        Assert.Equal(1, mark.Map);
        Assert.False(item.TryGetTag("RUNE_Y", out _));
    }

    [Fact]
    public void Item_RuneTagWrite_RoutesToMoreP()
    {
        var item = new Item();

        item.TrySetProperty("TAG.RUNE_X", "150");
        item.TrySetProperty("TAG.RUNE_Y", "160");
        item.TrySetProperty("TAG.RUNE_Z", "3");
        item.TrySetProperty("TAG.RUNE_MAP", "1");

        Assert.True(item.TryGetRuneMark(out Point3D mark));
        Assert.Equal(new Point3D(150, 160, 3, 1), mark);
        Assert.True(item.TryGetProperty("RUNE_X", out string runeX));
        Assert.Equal("150", runeX);
        Assert.False(item.TryGetTag("RUNE_X", out _));
    }
}
