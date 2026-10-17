using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Source-X parity for the "information" skills dispatched from CClientTarg.cpp
/// after a skill-target click: Anatomy, AnimalLore, ArmsLore, EvalInt, Forensics,
/// ItemID, TasteID. Each method preserves the upstream branch order, constant
/// lookup tables, and message delivery channel (SysMessage vs. addObjMessage),
/// so the text sequence the client sees is byte-identical to Source-X.
///
/// Engine entry points never touch the network directly -- they emit via the
/// <see cref="IInfoSkillSink"/> passed in, which lets unit tests record the
/// exact message stream produced for any (skill, target) pair.
///
/// Skill values are read in the 0..1000 scale SphereNet's <c>GetSkill</c>
/// returns, matching Source-X's iSkillLevel convention.
/// </summary>
public static class InfoSkillEngine
{
    private const int SKTRIG_QTY = 1;

    // ---------------------------------------------------------------- Anatomy

    /// <summary>
    /// Source-X <c>CClient::OnSkill_Anatomy</c>. Buckets STR/DEX into 10-slot
    /// description tables and renders a single overhead line on the target,
    /// optionally followed by an "aura of magic" note for conjured creatures.
    /// </summary>
    public static int Anatomy(IInfoSkillSink sink, Character target, int iSkillLevel, bool fTest = false)
    {
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.NonAlive));
            return -1;
        }

        if (fTest)
        {
            if (target == sink.Self) return 2;
            return sink.Random.Next(60);
        }

        // Stat_GetAdjusted - with what is worn and cast on them (CClientTarg.cpp:1170-1182).
        string strDesc = StatBandDesc(Combat.CombatEngine.EffectiveStr(target), s_anatomyStr);
        string dexDesc = StatBandDesc(Combat.CombatEngine.EffectiveDex(target), s_anatomyDex);

        string result = ServerMessages.GetFormatted(
            Msg.AnatomyResult, target.Name, strDesc, dexDesc);
        sink.ObjectMessage(target, result);

        if (target.IsStatFlag(StatFlag.Conjured))
            sink.ObjectMessage(target, ServerMessages.Get(Msg.AnatomyMagic));

        return iSkillLevel;
    }

    private static readonly string[] s_anatomyStr =
    {
        Msg.AnatomyStr1, Msg.AnatomyStr2, Msg.AnatomyStr3, Msg.AnatomyStr4, Msg.AnatomyStr5,
        Msg.AnatomyStr6, Msg.AnatomyStr7, Msg.AnatomyStr8, Msg.AnatomyStr9, Msg.AnatomyStr10,
    };
    private static readonly string[] s_anatomyDex =
    {
        Msg.AnatomyDex1, Msg.AnatomyDex2, Msg.AnatomyDex3, Msg.AnatomyDex4, Msg.AnatomyDex5,
        Msg.AnatomyDex6, Msg.AnatomyDex7, Msg.AnatomyDex8, Msg.AnatomyDex9, Msg.AnatomyDex10,
    };

    // ---------------------------------------------------------- Animal Lore

    /// <summary>
    /// Source-X <c>CClient::OnSkill_AnimalLore</c>. Emits up to three overhead
    /// lines: trade-name identification, master-relationship line, food level.
    /// </summary>
    public static int AnimalLore(IInfoSkillSink sink, Character target, GameWorld world, int iSkillLevel, bool fTest = false)
    {
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.NonAlive));
            return -1;
        }

        if (fTest)
        {
            if (target == sink.Self) return 2;
            if (target.IsPlayer) return sink.Random.Next(10);
            return sink.Random.Next(60);
        }

        string pronoun = target.GetPronoun();
        string possess = target.GetPossessPronoun();

        // Individual name -> "Rex, the large horse."
        if (target.IsIndividualName())
        {
            sink.ObjectMessage(target, ServerMessages.GetFormatted(
                Msg.AnimalloreResult, target.Name, target.GetTradeName()));
        }

        // Master relationship.
        Character? owner = target.GetPetOwner(world);
        string masterLine;
        if (owner == null)
        {
            masterLine = ServerMessages.GetFormatted(Msg.AnimalloreFree, pronoun, possess);
        }
        else
        {
            string ownerName = owner == sink.Self
                ? ServerMessages.Get(Msg.AnimalloreMasterYou)
                : owner.Name;
            masterLine = ServerMessages.GetFormatted(Msg.AnimalloreMaster, pronoun, ownerName);
        }
        sink.ObjectMessage(target, masterLine);

        // Food state. Conjured creatures always render the same line.
        string foodText = target.IsStatFlag(StatFlag.Conjured)
            ? ServerMessages.Get(Msg.AnimalloreConjured)
            : GetFoodLevelMessage(target, ownerOwned: owner != null);

        sink.ObjectMessage(target, ServerMessages.GetFormatted(
            Msg.AnimalloreFood, pronoun, foodText));

        return 0;
    }

    /// <summary>
    /// Returns one of the MSG_PET_FOOD_* bands based on the character's current
    /// food counter: Source-X CChar::Food_GetLevelMessage (CCharStatus.cpp:830-884)
    /// bands FOOD against the creature's own MAXFOOD, IMulDiv(food, 8, max), and a
    /// creature with no food stat is "unaffected". A fixed /60 stood in for the max.
    /// </summary>
    internal static string GetFoodLevelMessage(Character ch, bool ownerOwned)
    {
        int max = ch.MaxFood;
        if (max == 0)
            return ServerMessages.Get(Msg.PetHappyUnaffected);

        // 8 bands (MSG_PET_FOOD_1..8 / MSG_FOOD_LVL_1..8 for free wildlife).
        string[] keys = ownerOwned
            ? new[]
            {
                Msg.MsgPetFood1, Msg.MsgPetFood2, Msg.MsgPetFood3, Msg.MsgPetFood4,
                Msg.MsgPetFood5, Msg.MsgPetFood6, Msg.MsgPetFood7, Msg.MsgPetFood8,
            }
            : new[]
            {
                Msg.MsgFoodLvl1, Msg.MsgFoodLvl2, Msg.MsgFoodLvl3, Msg.MsgFoodLvl4,
                Msg.MsgFoodLvl5, Msg.MsgFoodLvl6, Msg.MsgFoodLvl7, Msg.MsgFoodLvl8,
            };

        int idx = Math.Clamp(IMulDiv(ch.Food, 8, max), 0, keys.Length - 1);
        return ServerMessages.Get(keys[idx]);
    }

    // ---------------------------------------------------------------- ItemID

    /// <summary>
    /// Source-X <c>CClient::OnSkill_ItemID</c>. For characters it prints the
    /// trade-name line; for items it stamps <c>ATTR_IDENTIFIED</c>, prints
    /// estimated gold value, and -- above 40% skill -- the made-of resource list.
    /// </summary>
    public static int ItemID(IInfoSkillSink sink, object? target, int iSkillLevel, bool fTest = false)
    {
        if (target is Character ch)
        {
            if (fTest) return 1;
            sink.SysMessage(ServerMessages.GetFormatted(Msg.ItemidResult, ch.Name));
            return 1;
        }

        if (target is not Item item)
            return -1;

        if (fTest)
        {
            // Already identified -> easier check.
            return item.IsAttr(ObjAttributes.Identified) ? sink.Random.Next(20) : sink.Random.Next(60);
        }

        item.SetAttr(ObjAttributes.Identified);

        // GetVendorPrice(-15, 0) (CClientTarg.cpp:1146): OVERRIDE.VALUE, else the item's
        // price, else its value at its quality - less 15%. The average of the value range
        // was shown, at full price.
        long price = Trade.VendorEngine.GetVendorPrice(item, -15, forSelling: false);
        if (price <= 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.ItemidNoval));
        }
        else
        {
            sink.SysMessage(ServerMessages.GetFormatted(
                Msg.ItemidGold, price * item.Amount, item.GetNameFull(true)));
        }

        // High-skill check: list base resources if any.
        // The resources as WriteNames writes them, "qty name, ..." (CResourceQty.cpp:253);
        // the raw resource ids were printed ("DefName:13413794").
        if (iSkillLevel > 40)
        {
            var def = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (def != null && def.BaseResources.Count > 0)
            {
                string resources = Definitions.ResourceText.Names(def.ResourcesRaw);
                if (resources.Length > 0)
                    sink.SysMessage(ServerMessages.Get(Msg.ItemidMadeof) + resources);
            }
        }

        return iSkillLevel;
    }

    internal static int EstimateVendorPrice(Item it)
    {
        var def = Definitions.DefinitionLoader.GetItemDef(it.BaseId);
        if (def == null) return 0;
        if (def.ValueMin == def.ValueMax) return def.ValueMin;
        return (def.ValueMin + def.ValueMax) / 2;
    }

    // -------------------------------------------------------------- EvalInt

    /// <summary>
    /// Source-X <c>CClient::OnSkill_EvalInt</c>. Prints the intellect band,
    /// then -- only at iSkillLevel > 400 -- a second line with magic-skill and
    /// current-mana descriptors.
    /// </summary>
    public static int EvalInt(IInfoSkillSink sink, Character target, int iSkillLevel, bool fTest = false)
    {
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.NonAlive));
            return -1;
        }
        if (fTest)
        {
            if (target == sink.Self) return 2;
            return sink.Random.Next(60);
        }

        int iIntVal = Combat.CombatEngine.EffectiveInt(target);   // Stat_GetAdjusted
        int iIntEntry = Math.Clamp((iIntVal - 1) / 10, 0, s_evalIntInt.Length - 1);

        sink.SysMessage(ServerMessages.GetFormatted(
            Msg.EvalintResult, target.Name, ServerMessages.Get(s_evalIntInt[iIntEntry])));

        if (iSkillLevel > 400)
        {
            int magerySkill = SkillEngine.GetAdjustedSkill(target, SkillType.Magery);
            int necroSkill  = SkillEngine.GetAdjustedSkill(target, SkillType.Necromancy);
            int magicSkill  = Math.Max(magerySkill, necroSkill);

            int iMagicEntry = Math.Clamp(magicSkill / 200, 0, s_evalIntMag.Length - 1);
            int iManaEntry  = 0;
            if (iIntVal > 0)
                iManaEntry = IMulDiv(target.Mana, s_evalIntMan.Length - 1, iIntVal);
            iManaEntry = Math.Clamp(iManaEntry, 0, s_evalIntMan.Length - 1);

            sink.SysMessage(ServerMessages.GetFormatted(
                Msg.EvalintResult2,
                ServerMessages.Get(s_evalIntMag[iMagicEntry]),
                ServerMessages.Get(s_evalIntMan[iManaEntry])));
        }

        return iSkillLevel;
    }

    private static readonly string[] s_evalIntInt =
    {
        Msg.EvalintInt1, Msg.EvalintInt2, Msg.EvalintInt3, Msg.EvalintInt4, Msg.EvalintInt5,
        Msg.EvalintInt6, Msg.EvalintInt7, Msg.EvalintInt8, Msg.EvalintInt9, Msg.EvalintInt10,
    };
    private static readonly string[] s_evalIntMag =
    {
        Msg.EvalintMag1, Msg.EvalintMag2, Msg.EvalintMag3, Msg.EvalintMag4, Msg.EvalintMag5, Msg.EvalintMag6,
    };
    private static readonly string[] s_evalIntMan =
    {
        Msg.EvalintMan1, Msg.EvalintMan2, Msg.EvalintMan3, Msg.EvalintMan4, Msg.EvalintMan5, Msg.EvalintMan6,
    };

    // ------------------------------------------------------------- ArmsLore

    /// <summary>
    /// Source-X <c>CClient::OnSkill_ArmsLore</c>. Composite multi-clause
    /// SysMessage covering defense/damage, repair state, magic/newbie/repair
    /// flags, and -- for weapons -- a poison level band.
    /// </summary>
    public static int ArmsLore(IInfoSkillSink sink, Item? target, int iSkillLevel, bool fTest = false)
    {
        if (target == null || !target.IsTypeArmorWeapon())
        {
            sink.SysMessage(ServerMessages.Get(Msg.ArmsloreUnable));
            return -SKTRIG_QTY;
        }

        if (fTest) return sink.Random.Next(60);

        bool fWeapon = target.ArmsLoreShowsAsWeapon();
        int iHitsCur = target.GetHitsCur();
        int iHitsMax = target.GetHitsMax();

        string header = fWeapon
            ? ServerMessages.GetFormatted(Msg.ArmsloreDam, target.GetWeaponAttack())
            : ServerMessages.GetFormatted(Msg.ArmsloreDef, target.GetArmorDefense());

        string body = header + ServerMessages.GetFormatted(Msg.ArmsloreRep, target.GetArmorRepairDesc());

        if (iHitsCur <= 3 || iHitsMax <= 3)
            body += ServerMessages.Get(Msg.ArmsloreRep0);

        if (target.IsAttr(ObjAttributes.Magic))
            body += ServerMessages.Get(Msg.ItemMagic);
        else if (target.IsAttr(ObjAttributes.Newbie) || target.IsAttr(ObjAttributes.Move_Never))
            body += ServerMessages.Get(Msg.ItemNewbie);

        if (!ArmorIsRepairable(target))
            body += ServerMessages.Get(Msg.ItemRepair);

        // Weapon poison level.
        if (fWeapon)
        {
            int poisonSkill = target.GetPoisonSkill();
            if (poisonSkill > 0)
            {
                int level = IMulDiv(poisonSkill, s_armsLorePoison.Length, 100);
                level = Math.Clamp(level, 0, s_armsLorePoison.Length - 1);
                body += " " + ServerMessages.Get(s_armsLorePoison[level]);
            }
        }

        sink.SysMessage(body);
        return iSkillLevel;
    }

    /// <summary>CItem::Armor_IsRepairable (CItem.cpp:4846-4894): never with
    /// ATTR_CANNOTREPAIR; always with CAN_I_REPAIR (the definition's REPAIR / CAN, the
    /// instance's CANMASK); otherwise plate, chain and ring armor, shields, the maces,
    /// swords, fencing weapons, axes, throwing weapons, whips and crossbows are, and
    /// clothing, bone and leather armor and wooden bows are not. Every item without
    /// REPAIR=1 was called unrepairable.</summary>
    internal static bool ArmorIsRepairable(Item item)
    {
        if (item.IsAttr(ObjAttributes.CannotRepair))
            return false;
        var def = Definitions.DefinitionLoader.GetItemDef(Definitions.ItemDefHelper.ResolveInstanceDefIndex(item));
        ulong can = (ulong)(def?.Can ?? CanFlags.None);
        if (def?.Repair == true)
            can |= (ulong)CanFlags.I_Repair;
        if (((can ^ item.CanMask) & (ulong)CanFlags.I_Repair) != 0)
            return true;
        return item.ItemType switch
        {
            ItemType.Shield or ItemType.Armor or ItemType.ArmorChain or ItemType.ArmorRing => true,
            ItemType.WeaponMaceCrook or ItemType.WeaponMacePick or ItemType.WeaponMaceSmith or
                ItemType.WeaponMaceStaff or ItemType.WeaponMaceSharp or ItemType.WeaponSword or
                ItemType.WeaponFence or ItemType.WeaponAxe or ItemType.WeaponThrowing or
                ItemType.WeaponWhip or ItemType.WeaponXBow => true,
            _ => false,
        };
    }

    private static readonly string[] s_armsLorePoison =
    {
        Msg.ArmslorePsn1, Msg.ArmslorePsn2, Msg.ArmslorePsn3, Msg.ArmslorePsn4, Msg.ArmslorePsn5,
        Msg.ArmslorePsn6, Msg.ArmslorePsn7, Msg.ArmslorePsn8, Msg.ArmslorePsn9, Msg.ArmslorePsn10,
    };

    // ------------------------------------------------------------ Forensics

    /// <summary>
    /// Source-X <c>CClient::OnSkill_Forensics</c> (CClientTarg.cpp:1191-1245). START:
    /// difficulty 2 on one's own corpse (m_uidLink), else rand(60). Then the corpse
    /// state (sleeping body, carved, a death time) and the matching carve/timer
    /// clause; <paramref name="secondsSinceDeath"/> is null for a corpse with no death
    /// time, and a death time of under a second is still told.
    /// </summary>
    public static int Forensics(IInfoSkillSink sink, Item? corpse, Character? killer, long? secondsSinceDeath,
        bool isCorpseSleeping, bool isCarved, int iSkillLevel, bool fTest = false)
    {
        if (corpse == null || corpse.ItemType != ItemType.Corpse)
        {
            sink.SysMessage(ServerMessages.Get(Msg.ForensicsCorpse));
            return -SKTRIG_QTY;
        }
        if (!sink.Self.CanTouch(corpse))
        {
            sink.SysMessage(ServerMessages.Get(Msg.ForensicsReach));
            return -SKTRIG_QTY;
        }

        if (fTest)
            return corpse.TryGetTag("OWNER_UID", out string? owner) && ScriptNumber.TryParseUInt(owner, out uint ownerUid) &&
                   ownerUid == sink.Self.Uid.Value
                ? 2
                : sink.Random.Next(60);

        string? killerName = killer?.Name;

        if (isCorpseSleeping)
        {
            sink.SysMessage(ServerMessages.GetFormatted(
                Msg.ForensicsAlive, killerName ?? "It"));
            return 1;
        }

        string body;
        if (isCarved)
        {
            body = ServerMessages.GetFormatted(Msg.ForensicsCarve1, corpse.Name);
            body += killerName != null
                ? ServerMessages.GetFormatted(Msg.ForensicsCarve2, killerName)
                : ServerMessages.Get(Msg.ForensicsFailname);
        }
        else if (secondsSinceDeath.HasValue)
        {
            body = ServerMessages.GetFormatted(Msg.ForensicsTimer, corpse.Name, secondsSinceDeath.Value);
            body += killerName != null
                ? ServerMessages.GetFormatted(Msg.ForensicsName, killerName)
                : ServerMessages.Get(Msg.ForensicsFailname);
        }
        else
        {
            // No death time: nothing to say (an empty line upstream).
            return iSkillLevel;
        }

        sink.SysMessage(body);
        return iSkillLevel;
    }

    // -------------------------------------------------------------- TasteID

    /// <summary>
    /// Source-X <c>CClient::OnSkill_TasteID</c>. Extracts the poison skill
    /// embedded in a potion / food / blade and maps it onto the shared
    /// ArmsLore poison table; falls back to TASTEID_RESULT for everything else.
    /// </summary>
    public static int TasteID(IInfoSkillSink sink, object target, int iSkillLevel, bool fTest = false)
    {
        if (target is not Item item)
        {
            // Said at START too (OnSkill_TasteID has no fTest guard here, :1258-1270).
            if (target is Character ch && ch == sink.Self)
                sink.SysMessage(ServerMessages.Get(Msg.TasteidSelf));
            else
                sink.SysMessage(ServerMessages.Get(Msg.TasteidChar));
            return -SKTRIG_QTY;
        }

        // CanUse(pItem, true): within touch, and movable (:1273).
        if (!sink.Self.CanTouch(item) ||
            Objects.ObjBase.ResolveWorld?.Invoke()?.FindObject(item.Uid) == item &&
            !ItemMoveRules.CanMove(sink.Self, item, out _))
        {
            sink.SysMessage(ServerMessages.Get(Msg.TasteidUnable));
            return -SKTRIG_QTY;
        }

        int poisonLevel = 0;
        switch (item.ItemType)
        {
            case ItemType.Potion:
                // A poison potion: m_itPotion.m_Type (MORE1) is SPELL_Poison and its
                // strength m_dwSkillQuality (MORE2) (:1281-1285).
                if (item.More1 == (uint)SpellType.Poison ||
                    item.TryGetTag("POTION_SPELL", out string? spell) &&
                    string.Equals(spell, "Poison", StringComparison.OrdinalIgnoreCase))
                {
                    poisonLevel = (int)Math.Min(item.More2, (uint)int.MaxValue);
                }
                break;
            case ItemType.Food:
            case ItemType.FoodRaw:
            case ItemType.Fruit:
            case ItemType.MeatRaw:
                poisonLevel = item.GetPoisonSkill() * 10;
                break;
            case ItemType.WeaponMaceSharp:
            case ItemType.WeaponSword:
            case ItemType.WeaponFence:
            case ItemType.WeaponAxe:
                // Every bladed weapon carries a poison (:1292-1298); only the sharp mace
                // was asked.
                poisonLevel = item.GetPoisonSkill() * 10;
                break;
            default:
                if (!fTest)
                    sink.SysMessage(ServerMessages.GetFormatted(Msg.TasteidResult, item.GetNameFull(false)));
                return 1;
        }

        if (fTest) return sink.Random.Next(60);

        if (poisonLevel > 0)
        {
            int level = IMulDiv(poisonLevel, s_armsLorePoison.Length, 1000);
            level = Math.Clamp(level, 0, s_armsLorePoison.Length - 1);
            sink.SysMessage(ServerMessages.Get(s_armsLorePoison[level]));
        }
        else
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.TasteidResult, item.GetNameFull(false)));
        }

        return iSkillLevel;
    }

    // ----------------------------------------------------------- primitives

    /// <summary>CChar::CanTouch (CCharStatus.cpp:1290-1420) for an object in the world:
    /// within 2 tiles, in sight, and not inside a container that cannot be searched -
    /// it was a 3-tile reach of SphereNet's own. An object outside any world (an
    /// engine-only check) keeps the plain reach-and-sight rule.</summary>
    private static bool CanTouch(this Character self, Objects.ObjBase target)
    {
        if (self == target) return true;
        if (Objects.ObjBase.ResolveWorld?.Invoke() is { } live && live.FindObject(target.Uid) == target)
            return Scripting.ScriptTouchAccess.CanTouch(self, target);
        var (tx, ty, tz, map) = ResolvePosition(target);
        if (map != self.MapIndex) return false;
        int dx = Math.Abs(self.Position.X - tx);
        int dy = Math.Abs(self.Position.Y - ty);
        int dist = Math.Max(dx, dy);
        if (dist > 3) return false;
        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        return world == null || world.CanSeeLOSFor(self, self.Position,
            new Point3D((short)tx, (short)ty, (sbyte)tz, map));
    }

    private static (int X, int Y, int Z, byte Map) ResolvePosition(Objects.ObjBase obj)
    {
        return obj switch
        {
            Character c => (c.Position.X, c.Position.Y, c.Position.Z, c.MapIndex),
            Item i      => ResolveItemPosition(i),
            _           => (0, 0, 0, byte.MaxValue),
        };
    }

    private static (int X, int Y, int Z, byte Map) ResolveItemPosition(Item item)
    {
        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        if (world == null || !item.ContainedIn.IsValid)
            return (item.Position.X, item.Position.Y, item.Position.Z, item.MapIndex);

        var seen = new HashSet<uint>();
        for (int depth = 0; depth < 32 && seen.Add(item.Uid.Value); depth++)
        {
            var holder = world.FindObject(item.ContainedIn);
            if (holder is Character ch)
                return (ch.Position.X, ch.Position.Y, ch.Position.Z, ch.MapIndex);
            if (holder is Item parent)
            {
                item = parent;
                if (!item.ContainedIn.IsValid)
                    return (item.Position.X, item.Position.Y, item.Position.Z, item.MapIndex);
                continue;
            }
            break;
        }
        return (0, 0, 0, byte.MaxValue);
    }

    private static string StatBandDesc(int stat, string[] bandKeys)
    {
        int idx = Math.Clamp((stat - 1) / 10, 0, bandKeys.Length - 1);
        return ServerMessages.Get(bandKeys[idx]);
    }

    /// <summary>Source-X IMulDiv (common.h:192): ((a*b) + c/2) / c - IsNegative(a*b) -
    /// a ROUNDED division, not the truncating one this used to be.</summary>
    internal static int IMulDiv(int a, int b, int c)
    {
        if (c == 0) return 0;
        long ab = (long)a * b;
        return (int)((ab + c / 2) / c - (ab < 0 ? 1 : 0));
    }
}
