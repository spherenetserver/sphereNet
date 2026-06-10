using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Body-type animation translation table (port of the reference
/// GenerateAnimate semantics): humanoid action indices must be remapped to
/// the actor body's own anim.mul group before hitting the 0x6E wire.
/// </summary>
public class BodyAnimTranslationTests
{
    private static readonly Random Seeded = new(1234);

    private const ushort MonsterBody = 0x0C;   // dragon-range high-detail body
    private const ushort AnimalBody = 0xC8;    // 200 — first low-detail body
    private const ushort HumanBody = 0x190;    // 400

    [Fact]
    public void HumanoidBody_PassesThroughUnchanged()
    {
        foreach (AnimationType anim in Enum.GetValues<AnimationType>())
        {
            Assert.Equal((ushort)anim,
                BodyAnimTranslator.Translate(HumanBody, (ushort)anim, Seeded));
        }
    }

    [Fact]
    public void BodyClassBoundaries_MatchReferenceRanges()
    {
        Assert.True(BodyAnimTranslator.IsMonsterBody(0));
        Assert.True(BodyAnimTranslator.IsMonsterBody(199));
        Assert.True(BodyAnimTranslator.IsAnimalBody(200));
        Assert.True(BodyAnimTranslator.IsAnimalBody(399));
        Assert.True(BodyAnimTranslator.IsHumanoidBody(400));
        Assert.True(BodyAnimTranslator.IsHumanoidBody(401));
        Assert.True(BodyAnimTranslator.IsHumanoidBody(605)); // elf-range bodies use the humanoid set
    }

    [Fact]
    public void MonsterGetHit_MapsToGetHitOrBlockGroups()
    {
        var seen = new HashSet<ushort>();
        for (int i = 0; i < 64; i++)
            seen.Add(BodyAnimTranslator.Translate(MonsterBody, (ushort)AnimationType.GetHit, Seeded));
        Assert.Subset(new HashSet<ushort> { 0x0A, 0x0F, 0x10 }, seen);
        Assert.Contains((ushort)0x0A, seen);
    }

    [Fact]
    public void MonsterAttacks_MapToAttackGroups4To6()
    {
        var attackInputs = new[]
        {
            AnimationType.AttackWeapon, AnimationType.Attack1HPierce,
            AnimationType.Attack1HBash, AnimationType.Attack2HBash,
            AnimationType.Attack2HSlash, AnimationType.Attack2HPierce,
            AnimationType.AttackBow, AnimationType.AttackXBow,
            AnimationType.AttackWrestle,
        };
        foreach (var input in attackInputs)
        {
            ushort outAction = BodyAnimTranslator.Translate(MonsterBody, (ushort)input, Seeded);
            Assert.InRange(outAction, (ushort)4, (ushort)6);
        }
    }

    [Fact]
    public void MonsterCastAndDeath_MapPerReferenceTable()
    {
        Assert.Equal(0x0C, BodyAnimTranslator.Translate(MonsterBody, (ushort)AnimationType.CastDirected, Seeded));
        Assert.Equal(0x0B, BodyAnimTranslator.Translate(MonsterBody, (ushort)AnimationType.CastArea, Seeded));
        Assert.Equal(0x02, BodyAnimTranslator.Translate(MonsterBody, (ushort)AnimationType.DieBackward, Seeded));
        Assert.Equal(0x03, BodyAnimTranslator.Translate(MonsterBody, (ushort)AnimationType.DieForward, Seeded));
    }

    [Fact]
    public void AnimalTable_MapsPerReferenceTable()
    {
        Assert.Equal(0x07, BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.GetHit, Seeded));
        Assert.Equal(0x05, BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.CastDirected, Seeded));
        Assert.Equal(0x03, BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.Eat, Seeded));
        Assert.Equal(0x08, BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.DieBackward, Seeded));
        Assert.Equal(0x0C, BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.DieForward, Seeded));
        Assert.Equal(0x0B, BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.Bow, Seeded));

        var seen = new HashSet<ushort>();
        for (int i = 0; i < 64; i++)
            seen.Add(BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.AttackWrestle, Seeded));
        Assert.Subset(new HashSet<ushort> { 0x05, 0x06 }, seen);
    }

    [Fact]
    public void UnknownActions_FallBackToWalkGroup()
    {
        Assert.Equal(0x00, BodyAnimTranslator.Translate(MonsterBody, (ushort)AnimationType.HorseAttack, Seeded));
        Assert.Equal(0x00, BodyAnimTranslator.Translate(AnimalBody, (ushort)AnimationType.HorseAttack, Seeded));
    }
}
