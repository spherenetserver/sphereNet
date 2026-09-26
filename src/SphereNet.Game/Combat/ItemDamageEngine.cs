using SphereNet.Core.Enums;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Combat;

/// <summary>Source-X CItem::OnTakeDamage (CItem.cpp:5792-5987): the one place an item
/// takes damage - durability wear, broken arrows, exploding potions, torn webs. The
/// DAMAGE verb on an item, a blade smashing an item and a gathering tool's wear all
/// arrive here, as they all call OnTakeDamage upstream.</summary>
public static class ItemDamageEngine
{
    /// <summary>INT32_MAX: the item was destroyed (CItem.cpp:5802).</summary>
    public const int Destroyed = int.MaxValue;

    /// <summary>g_Rand.GetVal(n): 0..n-1. Replaceable for deterministic tests.</summary>
    public static Func<int, int> RandVal = DefaultRand;

    internal static int DefaultRand(int n) => n <= 0 ? 0 : Random.Shared.Next(n);

    private const ushort ExplodeFxId = 0x36B0;   // ITEMID_FX_EXPLODE_3
    private const ushort ExplodeSound = 0x307;   // CItem::OnExplosion (CItem.cpp:6024)

    /// <summary>Damage an item. Returns the amount of damage done, 0 for none and
    /// <see cref="Destroyed"/> when the item is gone.</summary>
    public static int OnTakeDamage(Item item, int damage, Character? source, DamageType type)
    {
        if (damage <= 0 || item.IsDeleted)
            return 0;

        // IsTypeArmorWeapon (CItem.cpp:5807): the armour/weapon family carries hits.
        bool hasMaxHits = ObjBase.IsTypeArmor(item.ItemType) || ObjBase.IsTypeWeapon(item.ItemType);
        if (hasMaxHits && item.HitsMax > 0)
        {
            // SELFREPAIR (CItem.cpp:5808-5821): a roll under it mends 2 instead.
            long selfRepair = item.TryGetProperty("SELFREPAIR", out string sr) &&
                long.TryParse(sr, out long srv) ? srv : 0;
            if (selfRepair > RandVal(10))
            {
                item.HitsCur = Math.Min(item.HitsMax, item.HitsCur + 2);
                return 0;
            }
        }

        // @Damage (CItem.cpp:5823-5829): ARGN1 damage, ARGN2 type; RETURN 1 cancels.
        if (CombatEngine.OnItemDamaged?.Invoke(item, damage, source, type) == true || item.IsDeleted)
            return 0;

        switch (item.ItemType)
        {
            case ItemType.Clothing:
                // Normal cloth takes special damage from fire (CItem.cpp:5833-5839).
                if ((type & DamageType.Fire) != 0)
                    return WearArmorWeapon(item, source);
                break;

            case ItemType.WeaponArrow:
            case ItemType.WeaponBolt:
                // CItem.cpp:5841-5857: a miss (1) usually survives, a hit rarely.
                if (damage == 1)
                {
                    if (RandVal(5) != 0)
                        return 0;
                }
                else if (RandVal(3) == 0)
                {
                    return 1;
                }
                item.Delete();
                return Destroyed;

            case ItemType.Potion:
                if (ResolvePotionSpell(item) == SpellType.Explosion)
                    return ExplodePotion(item, source);
                return 1;

            case ItemType.Web:
            {
                // CItem.cpp:5886-5906. m_itWeb.m_wHitsCur is MORE1.
                var console = source != null ? ObjBase.ResolveClientConsole?.Invoke(source) : null;
                if ((type & (DamageType.Fire | DamageType.HitBlunt | DamageType.HitSlash | DamageType.God)) == 0)
                {
                    console?.SysMessage(ServerMessages.Get(Msg.WebNoeffect));
                    return 0;
                }
                if ((uint)damage > item.More1 || (type & DamageType.Fire) != 0)
                {
                    console?.SysMessage(ServerMessages.Get(Msg.WebDestroy));
                    item.Delete();
                    return Destroyed;
                }
                console?.SysMessage(ServerMessages.Get(Msg.WebWeaken));
                item.More1 -= (uint)damage;
                return 1;
            }
        }

        if (hasMaxHits)
            return WearArmorWeapon(item, source);

        // Don't know how to calc damage for this (CItem.cpp:5985).
        return 0;
    }

    /// <summary>The forcedamage branch (CItem.cpp:5912-5982): one hit point per blow,
    /// destroyed at its last.</summary>
    private static int WearArmorWeapon(Item item, Character? source)
    {
        // SphereNet keeps an item's hits in their own fields, not in MORE1 as
        // upstream's m_itArmor does, so gear whose def and save name no HITPOINTS
        // reads 0 here. Upstream would destroy it at once (HitsCur <= 1); doing that
        // would erase every imported piece that carried its hits some other way, so an
        // item without a pool takes no wear.
        if (item.HitsMax <= 0)
            return 0;

        var wearer = item.GetTopLevelObj() as Character;
        if (item.HitsCur <= 1)
        {
            item.HitsCur = 0;
            if (CombatEngine.BreakOnZeroHits)
            {
                if (CombatEngine.OnItemBroken != null)
                    CombatEngine.OnItemBroken(item);   // emotes item_dmg_destroyed, removes
                else
                    item.Delete();
            }
            return Destroyed;
        }

        item.HitsCur--;

        // The PRIV_DETAIL commentary (CItem.cpp:5952-5980).
        if (source is { DetailView: true })
        {
            string msg = wearer != null && !ReferenceEquals(wearer, source)
                ? ServerMessages.GetFormatted(Msg.ItemDmgDamage1, wearer.GetName(), item.GetName())
                : ServerMessages.GetFormatted(Msg.ItemDmgDamage2, item.GetName());
            ObjBase.ResolveClientConsole?.Invoke(source)?.SysMessage(msg);
        }
        if (wearer != null && !ReferenceEquals(wearer, source) && wearer.DetailView)
        {
            string? msg = null;
            if (item.HitsCur < item.HitsMax / 2)
            {
                int percent = RepairPercent(item);
                if (wearer.GetSkill(SkillType.ArmsLore) / 10 > percent)
                    msg = ServerMessages.GetFormatted(Msg.ItemDmgDamage3, item.GetName(), RepairDesc(item));
            }
            msg ??= ServerMessages.GetFormatted(Msg.ItemDmgDamage4, item.GetName());
            ObjBase.ResolveClientConsole?.Invoke(wearer)?.SysMessage(msg);
        }
        return 2;
    }

    /// <summary>Armor_GetRepairPercent (CItem.cpp:5765-5772).</summary>
    private static int RepairPercent(Item item)
    {
        int max = item.HitsMax, cur = item.HitsCur;
        if (max == 0 || max < cur)
            return 100;
        return Skills.Information.InfoSkillEngine.IMulDiv(cur, 100, max);
    }

    /// <summary>Armor_GetRepairDesc (CItem.cpp:5774-5790).</summary>
    private static string RepairDesc(Item item)
    {
        int max = item.HitsMax, cur = item.HitsCur;
        string key = cur > max ? Msg.ItemstatusPerfect
            : cur == max ? Msg.ItemstatusFull
            : cur > max / 2 ? "itemstatus_scratched"
            : cur > max / 3 ? "itemstatus_wellworn"
            : cur > 3 ? "itemstatus_badly"
            : "itemstatus_fall_apart";
        return ServerMessages.Get(key);
    }

    /// <summary>The spell a potion conveys (m_itPotion.m_Type): numeric MORE1, else
    /// the MORE1_DEFNAME routing tag.</summary>
    internal static SpellType ResolvePotionSpell(Item potion)
    {
        if (potion.More1 is > 0 and < 1000)
            return (SpellType)potion.More1;
        if (potion.TryGetTag("MORE1_DEFNAME", out string? name) && !string.IsNullOrWhiteSpace(name) &&
            Definitions.DefinitionLoader.StaticResources?.ResolveDefName(name) is { IsValid: true } rid &&
            rid.Type == ResType.SpellDef)
            return (SpellType)rid.Index;
        return 0;
    }

    /// <summary>The explosion-potion branch (CItem.cpp:5859-5884) and the explosion it
    /// leaves (CItem::OnExplosion, :5989-6025). Upstream parks an IT_EXPLOSION item at
    /// the potion's top-level point with a one-tick decay; SphereNet has no such item
    /// type, so the blast is dealt here: every character within 2 tiles with line of
    /// sight takes the s_explosion effect at the potion's MORE2 strength as fire
    /// damage, with SRC as the attacker. One potion of the stack is used up.</summary>
    private static int ExplodePotion(Item potion, Character? source)
    {
        var spellDef = Character.ResolveSpellDef?.Invoke(SpellType.Explosion);
        if (spellDef == null)
            return 0;

        int quality = (int)Math.Clamp(potion.More2, 0, int.MaxValue);
        int damage = (ushort)spellDef.GetEffect(quality);
        var flags = (spellDef.Flags & SpellFlag.NoUnparalyze) != 0
            ? DamageType.Fire | DamageType.NoUnparalyze
            : DamageType.Fire;
        var point = potion.GetTopLevelPosition();

        // ConsumeAmount (:5881).
        if (potion.Amount > 1)
            potion.Amount--;
        else
            potion.Delete();

        var world = ObjBase.ResolveWorld?.Invoke();
        if (world != null)
        {
            foreach (var ch in world.GetCharsInRange(point, 2).ToList())
            {
                if (ch.IsDeleted || ch.IsDead || !world.CanSeeLOS(ch.Position, point))
                    continue;
                CombatEngine.ApplyScriptDamage(ch, damage, flags, source, 0, 100, 0, 0, 0);
            }
        }

        ObjBase.BroadcastNearby?.Invoke(point, 18,
            new PacketEffect(2, 0, 0, ExplodeFxId, point.X, point.Y, point.Z,
                point.X, point.Y, point.Z, 9, 10, true, false), 0);
        ObjBase.BroadcastNearby?.Invoke(point, 18,
            new PacketSound(ExplodeSound, point.X, point.Y, point.Z), 0);
        return Destroyed;
    }
}
