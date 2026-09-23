using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report: a dragon recast Invisibility ("An Lor Xen") on itself every few
/// seconds and never fought. Upstream casts a good, character-targeted spell only
/// when it is needed (NPC_FightCast, CCharNPCAct_Magic.cpp:349): a heal under the
/// heal threshold, a cure while poisoned, a BLESS spell whose effect layer is free.
/// Invisibility (TARG_CHAR|GOOD in the pack) is none of those.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcGoodSpellSuitsTests
{
    private static readonly Layer BlessLayer = (Layer)32;

    private static NpcAI Ai(SphereNet.Game.World.GameWorld world) => new(world, new SphereConfig())
    {
        ResolveNpcSpellFlags = s => s switch
        {
            SpellType.Invisibility => SpellFlag.TargChar | SpellFlag.Good,
            SpellType.Strength => SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Bless,
            SpellType.Heal => SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Heal,
            _ => SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Damage,
        },
        ResolveNpcSpellLayer = s => s == SpellType.Strength ? BlessLayer : Layer.None,
    };

    [Fact]
    public void InvisibilityIsNeverTheFallbackAgainstAReflectingTarget()
    {
        var world = TestHarness.CreateWorld();
        var ai = Ai(world);
        var dragon = world.CreateCharacter();
        dragon.Hits = dragon.MaxHits = 500;
        dragon.NpcSpellAdd(SpellType.Invisibility);
        dragon.NpcSpellAdd(SpellType.MagicArrow);
        var player = world.CreateCharacter();
        player.Hits = player.MaxHits = 100;
        player.SetStatFlag(StatFlag.Reflection);   // harmful spells are ruled out

        for (int i = 0; i < 50; i++)
        {
            var (spell, _) = ai.ChooseBestSpell(dragon, player, 3);
            Assert.NotEqual(SpellType.Invisibility, spell);
        }
    }

    [Fact]
    public void ABlessingIsCastOnlyWhileItsEffectIsMissing()
    {
        var world = TestHarness.CreateWorld();
        var ai = Ai(world);
        var mage = world.CreateCharacter();
        mage.Hits = mage.MaxHits = 100;
        mage.NpcSpellAdd(SpellType.Strength);
        var enemy = world.CreateCharacter();
        enemy.Hits = enemy.MaxHits = 100;
        enemy.SetStatFlag(StatFlag.Reflection);

        var (spell, target) = ai.ChooseBestSpell(mage, enemy, 3);
        Assert.Equal(SpellType.Strength, spell);
        Assert.Same(mage, target);

        var memory = world.CreateItem();
        mage.Equip(memory, BlessLayer);                 // already blessed

        (spell, _) = ai.ChooseBestSpell(mage, enemy, 3);
        Assert.Equal(SpellType.None, spell);
    }
}
