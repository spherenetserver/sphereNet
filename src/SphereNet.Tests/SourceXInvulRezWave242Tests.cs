using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Wave 242 — Source-X CChar::OnTakeDamage bounces every blow while the target
/// carries STATF_INVUL (unless flagged DAMAGE_GOD), and CItemCorpse::
/// IsCorpseResurrectable refuses a corpse that is not a top-level ground object.
/// </summary>
public sealed class SourceXInvulRezWave242Tests
{
    private static Character MakeChar()
    {
        var ch = new Character
        {
            Name = "Dummy", MaxHits = 100, Hits = 100, Str = 100,
            Position = new Point3D(100, 100, 0, 0),
        };
        return ch;
    }

    [Fact]
    public void IsDamageImmune_HonorsInvulAndGodFlag()
    {
        var ch = MakeChar();
        Assert.False(CombatEngine.IsDamageImmune(ch));           // no flag → vulnerable

        ch.SetStatFlag(StatFlag.Invul);
        Assert.True(CombatEngine.IsDamageImmune(ch));             // invul → immune
        Assert.True(CombatEngine.IsDamageImmune(ch, DamageType.Fire));
        Assert.False(CombatEngine.IsDamageImmune(ch, DamageType.God)); // god pierces invul
    }

    [Fact]
    public void ApplyScriptDamage_InvulTarget_BouncesWithNoHitLoss()
    {
        var ch = MakeChar();
        ch.SetStatFlag(StatFlag.Invul);

        int dealt = CombatEngine.ApplyScriptDamage(ch, 40, DamageType.Physical);

        Assert.Equal(0, dealt);
        Assert.Equal(100, ch.Hits);
    }

    [Fact]
    public void ApplyScriptDamage_GodDamage_PiercesInvul()
    {
        var ch = MakeChar();
        ch.SetStatFlag(StatFlag.Invul);

        int dealt = CombatEngine.ApplyScriptDamage(ch, 40, DamageType.God);

        Assert.Equal(40, dealt);
        Assert.Equal(60, ch.Hits);
    }

    [Fact]
    public void Healing_CorpseInsideContainer_RejectsAsNotTopLevel()
    {
        var world = new GameWorld(NullLoggerFactory.Instance);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var healer = MakeChar();
        healer.IsPlayer = true;
        healer.SetSkill(SkillType.Healing, 800);

        // The rez target is a ghost that has manifested (war mode) next to its corpse.
        var ghost = MakeChar();
        ghost.IsPlayer = true;
        ghost.SetStatFlag(StatFlag.Dead);
        ghost.SetStatFlag(StatFlag.War);

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.Position = new Point3D(100, 100, 0, 0);
        // Tuck the corpse inside a container — it is no longer a ground object.
        // Both items must be world-registered so the container carries a valid Uid.
        var box = world.CreateItem();
        box.ItemType = ItemType.Container;
        box.Position = new Point3D(100, 100, 0, 0);
        corpse.ContainedIn = box.Uid;

        var sink = new StubSink(healer, world);
        sink.Pack[ItemType.Bandage] = new Item { ItemType = ItemType.Bandage };

        bool ok = ActiveSkillEngine.Healing(sink, ghost, SkillType.Healing, corpse);

        Assert.False(ok);
        Assert.Contains(sink.Log, t => t == ServerMessages.Get(Msg.HealingCorpseg));
        Assert.False(sink.Resurrected);
    }

    private sealed class StubSink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; } = new(1);
        public GameWorld World { get; }
        public List<string> Log { get; } = new();
        public Dictionary<ItemType, Item> Pack { get; } = new();
        public bool Resurrected { get; private set; }

        public StubSink(Character self, GameWorld world) { Self = self; World = world; }

        public void SysMessage(string text) => Log.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Log.Add(text);
        public void Emote(string text) => Log.Add(text);
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => Pack.TryGetValue(type, out var i) ? i : null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
        public void ResurrectTarget(Character target) => Resurrected = true;
    }
}
