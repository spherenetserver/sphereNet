using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.PlayerBots;

/// <summary>
/// What a fighter holds and drinks. A bot wields the weapon it does the most damage
/// with: of those it carries, the one whose damage at the engine's own figures (the
/// weapon's damage, its swing delay) weighs most at the bot's skill for it - a
/// swordsman does not walk about with a mace because that is what it found. It looks
/// its arms over when it checks its gear and after going through a body. With Parrying
/// and a one-handed weapon it keeps its shield on. It bandages itself only with real
/// Healing skill, and drinks what it carries when it matters: heal, cure, refresh when
/// out of breath, strength or agility before a hard fight - behind the potion delay a
/// player has. An archer out of arrows takes the blade it carries, or leaves. All of
/// it through the bot's client: the equip macro, the double click, the target cursor.
/// </summary>
public sealed partial class PlayerBotEngine
{
    /// <summary>Healing skill (tenths) under which a bot does not bandage itself: the
    /// attempt would waste the bandage.</summary>
    internal const int MinHealingSkill = 300;
    /// <summary>Parrying skill (tenths) from which a shield is worth the hand.</summary>
    internal const int MinParrySkill = 300;
    /// <summary>How much better another weapon must be before the bot changes.</summary>
    private const double BetterWeaponBy = 1.12;

    public long WindowWeaponSwaps { get; private set; }
    public long WindowShieldsUp { get; private set; }
    public long WindowPotions { get; private set; }
    public long WindowBandages { get; private set; }

    // ------------------------------------------------------------------
    //  Potions
    // ------------------------------------------------------------------

    /// <summary>The spell a potion conveys (its MORE1), 0 for a bottle that names none.
    /// A bottle of an older system is read by its name.</summary>
    internal static SpellType PotionSpell(Item potion)
    {
        if (potion.ItemType != ItemType.Potion) return SpellType.None;
        var spell = ItemDamageEngine.ResolvePotionSpell(potion);
        if (spell != SpellType.None) return spell;
        string name = potion.GetName();
        if (name.Contains("heal", StringComparison.OrdinalIgnoreCase)) return SpellType.GreaterHeal;
        if (name.Contains("cure", StringComparison.OrdinalIgnoreCase)) return SpellType.Cure;
        if (name.Contains("refresh", StringComparison.OrdinalIgnoreCase)) return SpellType.Refresh;
        if (name.Contains("strength", StringComparison.OrdinalIgnoreCase)) return SpellType.Strength;
        if (name.Contains("agility", StringComparison.OrdinalIgnoreCase)) return SpellType.Agility;
        return SpellType.None;
    }

    /// <summary>A potion of one of <paramref name="spells"/> in the pack (the first
    /// named kind first), not one held for a player.</summary>
    private Item? FindPotion(Character ch, params SpellType[] spells)
    {
        if (ch.Backpack is not { } pack) return null;
        Item? best = null;
        int bestAt = int.MaxValue;
        foreach (var item in _world.GetContainerContents(pack.Uid))
        {
            if (item.IsDeleted || item.ItemType != ItemType.Potion) continue;
            int at = Array.IndexOf(spells, PotionSpell(item));
            if (at < 0 || at >= bestAt || HeldFor(ch, item) != 0) continue;
            best = item;
            bestAt = at;
        }
        return best;
    }

    /// <summary>Potions of a kind in the pack (for the shopping list).</summary>
    internal int CountPotions(Character ch, SpellType spell)
    {
        if (ch.Backpack is not { } pack) return 0;
        int n = 0;
        foreach (var item in _world.GetContainerContents(pack.Uid))
            if (!item.IsDeleted && item.ItemType == ItemType.Potion && SameDraught(PotionSpell(item), spell))
                n += Math.Max(1, (int)item.Amount);
        return n;
    }

    /// <summary>Heal and Greater Heal bottles are one need: either mends.</summary>
    private static bool SameDraught(SpellType a, SpellType b) =>
        a == b || (a is SpellType.Heal or SpellType.GreaterHeal && b is SpellType.Heal or SpellType.GreaterHeal);

    /// <summary>Whether an item answers a shopping need: its type, and for a potion
    /// need (<c>Potion:Spell</c>) the spell in the bottle.</summary>
    internal static bool AnswersNeed(BotNeed need, Item item) =>
        item.ItemType == need.Type &&
        (need.Spell == SpellType.None || SameDraught(PotionSpell(item), need.Spell));

    /// <summary>Drink a potion when it matters. One at a time, behind the delay the
    /// engine keeps between two potions.</summary>
    private bool TryDrink(BotMind mind, long now, int hp, Character? foe)
    {
        var ch = mind.Char;
        var client = mind.Client;
        var tac = mind.Tac;
        if (client == null || now < tac.NextPotionMs || ch.IsCasting) return false;
        tac.NextPotionMs = now + 1200;
        if (ClientWorldFeaturesHandler.HasPotionUsedMarker(ch)) return false;

        Item? potion = null;
        string why = "";
        // Poison first: it undoes every heal. A caster with the mana cures by spell.
        if (ch.IsPoisoned && !(mind.IsCaster && CanCast(mind, SpellType.Cure)))
        {
            potion = FindPotion(ch, SpellType.Cure, SpellType.ArchCure);
            why = "cure";
        }
        if (potion == null && hp < 45 && !ch.IsPoisoned)
        {
            potion = FindPotion(ch, SpellType.GreaterHeal, SpellType.Heal);
            why = "heal";
        }
        // Out of breath: it can neither run nor swing.
        if (potion == null && ch.MaxStam > 0 && ch.Stam * 100 / ch.MaxStam < 25)
        {
            potion = FindPotion(ch, SpellType.Refresh);
            why = "refresh";
        }
        // A caster run dry in a fight, with a mana potion on it.
        if (potion == null && foe != null && mind.IsCaster && ch.MaxMana > 0 && ch.Mana * 100 / ch.MaxMana < 20)
        {
            potion = FindPotion(ch, SpellType.Mana);
            why = "mana";
        }
        // Before a hard fight, once: strength for a blade, agility for a bow.
        if (potion == null && foe != null && !tac.Boosted && !mind.IsCaster && hp >= 60 &&
            ThreatNow(mind, foe, now).Toughness >= ch.MaxHits)
        {
            tac.Boosted = true;
            potion = mind.IsArcher
                ? FindPotion(ch, SpellType.Agility, SpellType.Strength)
                : FindPotion(ch, SpellType.Strength, SpellType.Agility);
            why = "boost";
        }
        if (potion == null) return false;
        client.HandleDoubleClick(potion.Uid.Value);
        WindowPotions++;
        mind.Note(now, $"drinks a {why} potion");
        tac.NextPotionMs = now + 2000;
        return true;
    }

    // ------------------------------------------------------------------
    //  Bandages
    // ------------------------------------------------------------------

    private bool CanBandageSelf(Character ch) =>
        ch.GetSkill(SkillType.Healing) >= MinHealingSkill && FindInPack(ch, i => i.ItemType == ItemType.Bandage) != null;

    /// <summary>A bandage on itself, for a bot with the skill to make it count.</summary>
    private bool TryBandageSelf(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (now < mind.NextHealMs) return false;
        if (ch.GetSkill(SkillType.Healing) < MinHealingSkill) return false;
        if (!TryBandage(mind, ch)) return false;
        mind.NextHealMs = now + 9000;
        WindowBandages++;
        return true;
    }

    private bool TryBandage(BotMind mind, Character patient)
    {
        var ch = mind.Char;
        var client = mind.Client;
        // Healing shares the character's active skill slot with Veterinary and
        // every other delayed skill. Skill_Start cancels that slot (Source-X).
        if (client == null || ch.IsCasting || ch.HasActiveSkillPending() || client.HasPendingTarget) return false;
        var bandage = FindInPack(ch, i => i.ItemType == ItemType.Bandage);
        if (bandage == null) return false;
        client.HandleDoubleClick(bandage.Uid.Value);
        if (!client.HasPendingTarget || client.Targets.ItemUid != bandage.Uid) return false;
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, patient.Uid.Value, patient.X, patient.Y, patient.Z, 0);
        return true;
    }

    /// <summary>Whether the bot has anything left to mend itself with.</summary>
    private bool HasHealMeans(BotMind mind)
    {
        var ch = mind.Char;
        if (mind.IsCaster && (CanCast(mind, SpellType.GreaterHeal) || CanCast(mind, SpellType.Heal))) return true;
        return CanBandageSelf(ch) || FindPotion(ch, SpellType.GreaterHeal, SpellType.Heal) != null;
    }

    /// <summary>Out of a fight and hurt: a bot looks after itself before it moves on -
    /// a bandage with the skill for it, a caster's heal with mana to spare. A potion is
    /// kept for when it is needed.</summary>
    private bool PatchUp(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (mind.Client == null || ch.MaxHits <= 0 || now < mind.NextHealMs) return false;
        int hp = ch.Hits * 100 / ch.MaxHits;
        if (hp >= 80) return false;
        if (mind.IsCaster && !ch.IsCasting && ch.MaxMana > 0 && ch.Mana * 100 / ch.MaxMana >= 30)
        {
            var heal = hp < 55 && CanCast(mind, SpellType.GreaterHeal) ? SpellType.GreaterHeal
                : CanCast(mind, SpellType.Heal) ? SpellType.Heal
                : CanCast(mind, SpellType.GreaterHeal) ? SpellType.GreaterHeal : SpellType.None;
            if (heal != SpellType.None)
            {
                CastOn(mind, heal, ch, now);
                mind.NextHealMs = now + 3500;
                return true;
            }
        }
        if (TryBandageSelf(mind, now)) return true;
        mind.NextHealMs = now + 5000;
        return false;
    }

    // ------------------------------------------------------------------
    //  Weapon, shield, ammunition
    // ------------------------------------------------------------------

    /// <summary>Whether the weapon has what it shoots: true for a blade, a throwing
    /// weapon and a bow whose definition asks for no ammunition.</summary>
    internal static bool HasAmmoFor(Character ch, Item? weapon)
    {
        if (weapon == null || !CombatHelper.IsRangedWeapon(weapon) || CombatHelper.IsThrowingWeapon(weapon)) return true;
        var spec = CombatHelper.ResolveAmmoSpec(CombatHelper.GetWeaponDef(weapon), weapon.ItemType, Item.ResolveDefName);
        return !spec.RequiresAmmo ||
               CombatHelper.FindAmmoInContainer(ch.Backpack, spec.BaseId, spec.FallbackType) != null;
    }

    /// <summary>Whether the bot carries a bow of this kind (in hand or in its pack):
    /// what it needs arrows or bolts for, also while it holds a blade for want of them.</summary>
    internal bool CarriesBow(Character ch, ItemType bow)
    {
        if (ch.FightWeapon()?.ItemType == bow) return true;
        return FindInPack(ch, i => i.ItemType == bow && HeldFor(ch, i) == 0) != null;
    }

    /// <summary>Damage a second the bot would do with <paramref name="weapon"/> (null:
    /// its fists): the engine's damage and swing delay for it, at the bot's skill.</summary>
    internal double WeaponWorth(Character ch, Item? weapon)
    {
        if (weapon != null && CombatHelper.IsRangedWeapon(weapon) && !HasAmmoFor(ch, weapon)) return 0.1;
        var (min, max) = CombatEngine.CalcWeaponDamage(ch, weapon);
        int delay = Math.Max(250, CombatEngine.GetSwingDelayMs(ch, weapon));
        int skill = ch.GetSkill(CombatEngine.GetWeaponSkill(ch, weapon));
        return (min + max) / 2.0 * HitGuess(skill) * 1000.0 / delay;
    }

    /// <summary>The weapons in the pack the bot may wield: its own, light enough.</summary>
    private IEnumerable<Item> PackWeapons(Character ch)
    {
        if (ch.Backpack is not { } pack) yield break;
        foreach (var item in _world.GetContainerContents(pack.Uid))
        {
            if (item.IsDeleted || !item.IsWeaponType || HeldFor(ch, item) != 0) continue;
            if (!ch.CanEquip(item, item.IsTwoHanded ? Layer.TwoHanded : Layer.OneHanded, out var denial) &&
                denial == Character.EquipDenial.TooWeak)
                continue;
            yield return item;
        }
    }

    /// <summary>The best hand-to-hand weapon in the pack, for an archer out of arrows.</summary>
    private Item? FindMeleeWeapon(Character ch)
    {
        Item? best = null;
        double bestWorth = 0;
        foreach (var item in PackWeapons(ch))
        {
            if (CombatHelper.IsRangedWeapon(item)) continue;
            double worth = WeaponWorth(ch, item);
            if (worth > bestWorth) { best = item; bestWorth = worth; }
        }
        return best;
    }

    /// <summary>A bot with nothing better may fight with staff or fists: the best of its
    /// Wrestling and its skill for a hand weapon it holds or carries (a cast puts the
    /// staff in the pack), at 50 or more.</summary>
    private bool CanBrawl(BotMind mind)
    {
        var ch = mind.Char;
        int best = ch.GetSkill(SkillType.Wrestling);
        var weapon = ch.FightWeapon();
        if (weapon != null && !CombatHelper.IsRangedWeapon(weapon))
            best = Math.Max(best, ch.GetSkill(CombatEngine.GetWeaponSkill(ch, weapon)));
        if (best >= 500) return true;
        foreach (var item in PackWeapons(ch))
            if (!CombatHelper.IsRangedWeapon(item) && ch.GetSkill(CombatEngine.GetWeaponSkill(ch, item)) >= 500)
                return true;
        return false;
    }

    /// <summary>Hand to hand with the best it has: the hand weapon in its pack when
    /// that beats what it holds, bare fists when it wrestles better than it wields.</summary>
    private void ArmToBrawl(BotMind mind)
    {
        var ch = mind.Char;
        var held = ch.FightWeapon();
        double heldWorth = held != null && CombatHelper.IsRangedWeapon(held) ? 0 : WeaponWorth(ch, held);
        double fists = WeaponWorth(ch, null);
        var packed = FindMeleeWeapon(ch);
        if (packed != null && WeaponWorth(ch, packed) > Math.Max(heldWorth, fists) * 1.05)
            Wield(mind, packed);
        else if (held != null && fists > heldWorth * 1.05)
            mind.Client?.HandleUnequipMacro([(ushort)held.EquipLayer]);
        mind.Tac.Power = null;
    }

    /// <summary>Put <paramref name="item"/> from the pack in hand, through the equip
    /// macro a client sends. True when it is worn afterwards.</summary>
    private bool Wield(BotMind mind, Item item)
    {
        if (mind.Client == null) return false;
        mind.Client.HandleEquipMacro([item.Uid.Value]);
        mind.Tac.Power = null;
        return item.IsEquipped;
    }

    /// <summary>The bot looks its arms over (out of a fight): the best weapon it
    /// carries goes in hand, and a shield on the other arm when it parries and the
    /// weapon leaves the hand free. True when something changed.</summary>
    private bool CheckArms(BotMind mind, long now)
    {
        var ch = mind.Char;
        var tac = mind.Tac;
        if (mind.Client == null || ch.IsDead || !mind.IsFighter) return false;
        if (mind.Mode is not (BotMode.Linger or BotMode.Travel))
        {
            // Busy (going through a body): looked at again as soon as it is done.
            if (tac.ArmsDirty) mind.NextGearCheckMs = Math.Min(mind.NextGearCheckMs, now + 2000);
            return false;
        }
        tac.ArmsDirty = false;
        if (mind.Trade != null || ch.IsCasting) return false;
        if (ChooseWeapon(mind, now)) return true;
        return RaiseShield(mind, now);
    }

    /// <summary>Wield the weapon the bot does the most damage with, of those it
    /// carries. A caster keeps what it holds: its weapon is its book.</summary>
    private bool ChooseWeapon(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (mind.IsCaster) return false;
        var held = ch.FightWeapon();
        double heldWorth = WeaponWorth(ch, held);
        Item? best = null;
        double bestWorth = heldWorth * BetterWeaponBy;
        foreach (var item in PackWeapons(ch))
        {
            double worth = WeaponWorth(ch, item);
            if (worth > bestWorth) { best = item; bestWorth = worth; }
        }
        if (best == null) return false;
        string was = held?.GetName() ?? "its fists";
        if (!Wield(mind, best)) return false;
        WindowWeaponSwaps++;
        mind.Tac.OutOfAmmo = false;
        mind.Note(now, $"takes up {best.GetName()} in place of {was}");
        OnAppeared?.Invoke(ch);
        return true;
    }

    /// <summary>A shield from the pack on the free arm, for a bot that parries.</summary>
    private bool RaiseShield(BotMind mind, long now)
    {
        var ch = mind.Char;
        if (ch.GetSkill(SkillType.Parrying) < MinParrySkill) return false;
        if (ch.GetEquippedItem(Layer.TwoHanded) != null) return false;
        var weapon = ch.FightWeapon();
        if (weapon == null || weapon.IsTwoHanded || CombatHelper.IsRangedWeapon(weapon)) return false;
        var shield = FindInPack(ch, i => i.ItemType == ItemType.Shield && HeldFor(ch, i) == 0);
        if (shield == null || !Wield(mind, shield)) return false;
        WindowShieldsUp++;
        mind.Note(now, $"puts its {shield.GetName()} on");
        OnAppeared?.Invoke(ch);
        return true;
    }

    /// <summary>In a fight with a bow: are there arrows left? Out of them the bot takes
    /// the blade it carries; with none it is marked out of ammunition, and the fight
    /// code makes it brawl or leave. Looked at every second or so.</summary>
    private void CheckAmmo(BotMind mind, long now)
    {
        var tac = mind.Tac;
        if (now < tac.NextAmmoCheckMs) return;
        tac.NextAmmoCheckMs = now + 1000;
        var ch = mind.Char;
        var weapon = ch.FightWeapon();
        if (weapon == null || !CombatHelper.IsRangedWeapon(weapon) || HasAmmoFor(ch, weapon))
        {
            // A blade in hand for want of arrows stays the plan until the bow is back.
            if (weapon != null && CombatHelper.IsRangedWeapon(weapon)) tac.OutOfAmmo = false;
            return;
        }
        if (!tac.OutOfAmmo)
        {
            tac.OutOfAmmo = true;
            tac.Power = null;
            mind.Note(now, $"out of ammunition for its {weapon.GetName()}");
            // The next stay is cut short for a trip to the bowyer.
            mind.NextSupplyCheckMs = 0;
        }
        if (FindMeleeWeapon(ch) is { } blade && Wield(mind, blade))
        {
            WindowWeaponSwaps++;
            mind.Note(now, $"takes up {blade.GetName()}");
            OnAppeared?.Invoke(ch);
            // The fight goes on with the new weapon in hand.
            if (mind.Foe is { IsDead: false, IsDeleted: false } foe) mind.Client?.HandleAttack(foe.Uid.Value);
        }
    }

    /// <summary>Whether the bot fights at a distance right now: a bow with arrows.</summary>
    private static bool ShootsNow(BotMind mind)
    {
        var weapon = mind.Char.FightWeapon();
        return weapon != null && CombatHelper.IsRangedWeapon(weapon) && HasAmmoFor(mind.Char, weapon);
    }
}
