using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

public class EngineTagsTests
{
    [Fact]
    public void IsEphemeral_BlocksSpellCastingTag()
    {
        Assert.True(EngineTags.IsEphemeral("SPELL_CASTING"));
        Assert.True(EngineTags.IsEphemeral("SKILL_PENDING_ID"));
        Assert.False(EngineTags.IsEphemeral("MY_CUSTOM_FLAG"));
    }

    [Fact]
    public void TrySetProperty_BlocksEphemeralTagWrites()
    {
        var ch = new Character();
        Assert.True(ch.TrySetProperty("TAG.SPELL_CASTING", "4"));
        Assert.False(ch.TryGetTag("SPELL_CASTING", out _));

        ch.SetTag("SPELL_CASTING", "4");
        Assert.True(ch.TryGetTag("SPELL_CASTING", out string? v));
        Assert.Equal("4", v);
    }

    [Fact]
    public void StripEphemeral_RemovesRuntimeTags()
    {
        var ch = new Character();
        ch.SetTag("SPELL_CASTING", "1");
        ch.SetTag("CURRENT_REGION", "Britain");
        ch.SetTag("MYQUEST", "active");

        int removed = EngineTags.StripEphemeral(ch);
        Assert.Equal(2, removed);
        Assert.True(ch.TryGetTag("MYQUEST", out string? q));
        Assert.Equal("active", q);
    }

    [Fact]
    public void Item_Hits_UseNativeFields_NotTags()
    {
        var item = new Item();
        item.TrySetProperty("HITS", "30");
        item.TrySetProperty("MAXHITS", "50");

        Assert.Equal(30, item.HitsCur);
        Assert.Equal(50, item.HitsMax);
        Assert.False(item.TryGetTag("HITS", out _));
    }

    [Fact]
    public void Item_MigrateHitsFromTags_ImportsLegacySave()
    {
        var item = new Item();
        item.SetTag("HITS", "25");
        item.SetTag("HITSMAX", "50");

        item.MigrateHitsFromTags();

        Assert.Equal(25, item.HitsCur);
        Assert.Equal(50, item.HitsMax);
        Assert.False(item.TryGetTag("HITS", out _));
    }
}
