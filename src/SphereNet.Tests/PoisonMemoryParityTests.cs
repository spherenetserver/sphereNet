using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Poison as Source-X keeps it: one IT_SPELL memory of SPELL_Poison worn on
/// LAYER_FLAG_Poison (CChar::SetPoison, CCharAct.cpp:4175), ticked by
/// Spell_Equip_OnTick (CCharSpell.cpp:1806), cured by deleting it (SetPoisonCure).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PoisonMemoryParityTests
{
    private static (GameWorld World, Character Victim, Character Poisoner) Setup(bool osi = false)
    {
        Character.MagicFlags = osi ? (int)MagicConfigFlags.OsiFormulas : 0;
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Character.ResolveCharByUid = uid => world.FindChar(uid);
        var victim = world.CreateCharacter();
        victim.MaxHits = 100; victim.Hits = 100;
        world.PlaceCharacter(victim, new Point3D(100, 100, 0, 0));
        var poisoner = world.CreateCharacter();
        world.PlaceCharacter(poisoner, new Point3D(101, 100, 0, 0));
        return (world, victim, poisoner);
    }

    [Fact]
    public void SetPoisonWearsTheSpellMemory()
    {
        var (_, victim, poisoner) = Setup();
        Assert.True(victim.SetPoison(600, 12, poisoner));

        var mem = victim.GetEquippedItem(Layer.FlagPoison);
        Assert.NotNull(mem);
        Assert.Equal(ItemType.Spell, mem!.ItemType);
        Assert.Equal((short)SpellType.Poison, mem.MoreP.X);   // MOREX = spell
        Assert.Equal((short)600, mem.MoreP.Y);                // MOREY = strength
        Assert.Equal(12u, mem.More2);                         // MORE2 = ticks
        Assert.Equal(poisoner.Uid, mem.Link);                 // LINK = poisoner
        Assert.True(victim.IsStatFlag(StatFlag.Poisoned));
        Assert.InRange(mem.Timeout - Environment.TickCount64, 1, 2000);  // first tick in 1-2 s
    }

    [Fact]
    public void ANewPoisonReplacesTheOldOne()
    {
        var (_, victim, poisoner) = Setup();
        victim.SetPoison(900, 18, poisoner);
        var first = victim.GetEquippedItem(Layer.FlagPoison);
        victim.SetPoison(100, 2, poisoner);   // weaker, and still replaces

        var second = victim.GetEquippedItem(Layer.FlagPoison);
        Assert.NotSame(first, second);
        Assert.True(first!.IsDeleted);
        Assert.Equal((short)100, second!.MoreP.Y);
        Assert.True(victim.IsStatFlag(StatFlag.Poisoned));
    }

    [Fact]
    public void RemovingTheMemoryIsTheCure()
    {
        var (world, victim, poisoner) = Setup();
        victim.SetPoison(600, 12, poisoner);
        world.DeleteObject(victim.GetEquippedItem(Layer.FlagPoison)!);   // a script's REMOVE

        Assert.Null(victim.GetEquippedItem(Layer.FlagPoison));
        Assert.False(victim.IsStatFlag(StatFlag.Poisoned));
    }

    [Fact]
    public void DeathCuresThePoison()
    {
        var (_, victim, poisoner) = Setup();
        victim.SetPoison(600, 12, poisoner);
        victim.Kill();
        Assert.Null(victim.GetEquippedItem(Layer.FlagPoison));
        Assert.False(victim.IsStatFlag(StatFlag.Poisoned));
    }

    [Fact]
    public void OsiLadderAndDistanceFallOff()
    {
        var (world, victim, poisoner) = Setup(osi: true);
        victim.SetPoison(900, 0, poisoner);                  // > 850: greater (2)
        var mem = victim.GetEquippedItem(Layer.FlagPoison)!;
        Assert.Equal((short)2, mem.MoreP.Y);
        Assert.Equal(6u, mem.More2);

        var far = world.CreateCharacter();
        world.PlaceCharacter(far, new Point3D(106, 100, 0, 0)); // 6 tiles: -3 levels
        victim.SetPoison(900, 0, far);
        Assert.Equal((short)0, victim.GetEquippedItem(Layer.FlagPoison)!.MoreP.Y);

        victim.SetPoison(900, 0, null);                       // no source: lesser
        Assert.Equal((short)0, victim.GetEquippedItem(Layer.FlagPoison)!.MoreP.Y);
    }

    [Theory]
    [InlineData(500, 1, 1)]   // Lesser   (OSI 0)
    [InlineData(700, 1, 2)]   // Standard (OSI 1)
    [InlineData(900, 1, 3)]   // Greater  (OSI 2)
    [InlineData(1000, 1, 4)]  // Deadly   (OSI 3), 1 in 10 lethal
    public void OsiSkillBandsMapToTheReferenceLevels(int skill, int dist, int minLevel)
    {
        var (world, victim, _) = Setup(osi: true);
        var src = world.CreateCharacter();
        world.PlaceCharacter(src, new Point3D((short)(100 + dist), 100, 0, 0));
        victim.SetPoison(skill, 0, src);
        Assert.InRange((int)victim.PoisonLevel, minLevel, skill >= 1000 ? minLevel + 1 : minLevel);
    }

    [Fact]
    public void EvilOmenAddsALevelAndIsSpent()
    {
        var (_, victim, poisoner) = Setup(osi: true);
        victim.EvilOmenActive = true;
        victim.EvilOmenExpireTick = Environment.TickCount64 + 60_000;
        victim.SetPoison(900, 0, poisoner);                  // greater (OSI 2) + 1
        Assert.Equal((byte)4, victim.PoisonLevel);
        Assert.False(victim.EvilOmenActive);
    }

    [Fact]
    public void NonOsiTickWeakensAndCountsDown()
    {
        var (_, victim, poisoner) = Setup();
        victim.SetPoison(600, 3, poisoner);                   // band 2 (greater)
        var mem = victim.GetEquippedItem(Layer.FlagPoison)!;

        int dealt = victim.ProcessPoisonTick(Environment.TickCount64 + 10_000);

        // max(sm_iPoisonMax[2]=6, maxhits * 2*2 / 100 = 4) = 6
        Assert.Equal(6, dealt);
        Assert.Equal(94, victim.Hits);
        Assert.Equal((short)550, mem.MoreP.Y);                // "gets weaker too"
        Assert.Equal(2u, mem.More2);
        Assert.InRange(mem.Timeout - Environment.TickCount64, 4000, 8000); // 5 + rand(4) s
    }

    [Fact]
    public void ThePoisonEndsWithItsLastTick()
    {
        var (_, victim, poisoner) = Setup();
        victim.SetPoison(600, 1, poisoner);
        victim.ProcessPoisonTick(Environment.TickCount64 + 10_000);
        Assert.Null(victim.GetEquippedItem(Layer.FlagPoison));
        Assert.False(victim.IsStatFlag(StatFlag.Poisoned));
    }

    [Fact]
    public void TheWorldTimerTicksTheMemoryThroughItsWearer()
    {
        var (_, victim, poisoner) = Setup();
        victim.SetPoison(600, 3, poisoner);
        var mem = victim.GetEquippedItem(Layer.FlagPoison)!;
        mem.SetTimeout(Environment.TickCount64 - 1);

        Assert.True(mem.OnTick());
        Assert.Equal(94, victim.Hits);
        Assert.Equal(2u, mem.More2);
    }

    [Fact]
    public void APoisonTickDoesNotDisturb()
    {
        var (_, victim, poisoner) = Setup();
        DamageType seen = 0;
        CombatEngine.OnDirectCharacterDamageApplied = (_, _, _, type) => seen = type;
        victim.SetPoison(600, 3, poisoner);
        victim.ProcessPoisonTick(Environment.TickCount64 + 10_000);
        Assert.True(seen.HasFlag(DamageType.NoDisturb));
        Assert.True(seen.HasFlag(DamageType.Poison));
    }

    [Fact]
    public void AnOldPoisonRecordBecomesTheMemoryOnTheFirstTick()
    {
        var (_, victim, _) = Setup();
        Assert.True(victim.TrySetProperty("POISON", "3|5|1500|0"));
        Assert.True(victim.IsStatFlag(StatFlag.Poisoned));
        Assert.Null(victim.GetEquippedItem(Layer.FlagPoison));   // not during load

        victim.Poison.MaterializeRestore();

        var mem = victim.GetEquippedItem(Layer.FlagPoison);
        Assert.NotNull(mem);
        Assert.Equal(5u, mem!.More2);
        Assert.Equal((byte)3, victim.PoisonLevel);
    }

    [Fact]
    public void CureChanceFollowsTheOsiTable()
    {
        var (_, victim, poisoner) = Setup(osi: true);
        victim.SetPoison(100, 0, poisoner);                   // lesser (0) is always cured
        var lesser = victim.GetEquippedItem(Layer.FlagPoison);
        for (int i = 0; i < 20; i++)
            Assert.True(CharacterPoisonState.CureChance(lesser, 0, isGm: false));

        Assert.False(CharacterPoisonState.CureChance(null, 1000, isGm: true));  // nothing to cure
        victim.SetPoison(900, 0, poisoner);
        Assert.True(CharacterPoisonState.CureChance(victim.GetEquippedItem(Layer.FlagPoison), 0, isGm: true));
    }
}
