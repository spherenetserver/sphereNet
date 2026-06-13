using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;

namespace SphereNet.Game.Death;

/// <summary>
/// Corpse and loot system. Maps to CChar::Death and CItemCorpse in Source-X.
/// Handles death processing, corpse creation, loot drop, and decay.
/// </summary>
public sealed class DeathEngine
{
    private readonly GameWorld _world;

    /// <summary>Corpse decay time for players (in seconds).</summary>
    public int CorpseDecayPlayer { get; set; } = 900; // 15 minutes

    /// <summary>Corpse decay time for NPCs (in seconds).</summary>
    public int CorpseDecayNPC { get; set; } = 300; // 5 minutes

    /// <summary>Whether looting others' corpses is a criminal act.</summary>
    public bool LootingIsACrime { get; set; } = true;

    /// <summary>Fired when a character is killed.</summary>
    public event Action<Character, Character?>? OnDeath;

    /// <summary>Party manager reference for loot rights.</summary>
    public Party.PartyManager? PartyManager { get; set; }

    /// <summary>Optional script trigger dispatcher for corpse item hooks.</summary>
    public TriggerDispatcher? TriggerDispatcher { get; set; }

    /// <summary>Host hook: dismount a mounted victim before the corpse is
    /// made (Source-X CChar::Death runs Horse_UnMount first). Wired to the
    /// mount engine + appearance broadcasts; null in bare test setups.</summary>
    public Action<Character>? DismountHook { get; set; }

    public DeathEngine(GameWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Process a character's death. Maps to CChar::Death in Source-X.
    /// Creates corpse, drops loot, handles NPC cleanup.
    /// </summary>
    public Item? ProcessDeath(Character victim, Character? killer = null)
    {
        if (victim.IsDead)
            return null;

        Character? effectiveKiller = killer;
        if (killer != null && killer.NpcMaster.IsValid)
        {
            var master = _world.FindChar(killer.NpcMaster);
            if (master != null && !master.IsDeleted)
                effectiveKiller = master;
        }

        // Kill the character
        victim.Kill();

        // Source-X CChar::Death order: the rider leaves the saddle before the
        // corpse is made — otherwise the mount-layer item is snapshotted into
        // the death state and the client keeps drawing a mounted body under
        // the ghost.
        if (victim.IsMounted)
            DismountHook?.Invoke(victim);

        // Karma/Fame changes for killer
        if (effectiveKiller != null)
        {
            ApplyKarmaFameChange(effectiveKiller, victim);

            // Experience award (Source-X ChangeExperience on kill): the
            // victim's own EXP value is the prize. Characters without EXP
            // grant nothing, so the system stays inert unless scripts or
            // CHARDEFs assign experience values.
            if (!victim.IsPlayer && victim.Exp > 0)
                effectiveKiller.ChangeExperience(victim.Exp);

            // PvP murder tracking — @MurderMark fires before the count is
            // recorded (Source-X Noto_Kill): a script may adjust the new count or
            // block the mark (and the criminal flag) entirely by returning null.
            if (victim.IsPlayer && effectiveKiller.IsPlayer)
            {
                int proposed = effectiveKiller.Kills + 1;
                int? finalCount = Character.OnMurderMark == null
                    ? proposed
                    : Character.OnMurderMark(effectiveKiller, victim, proposed);
                if (finalCount.HasValue)
                {
                    effectiveKiller.Kills = (short)Math.Clamp(finalCount.Value, 0, short.MaxValue);
                    effectiveKiller.SetCriminal(120_000); // 2 minutes criminal flag
                }
            }
        }

        // Source-X CChar::Death order is critical for players:
        //   1) MakeCorpse  (corpse.Amount = current/original body ID)
        //   2) Broadcast PacketDeath (0xAF) to nearby
        //   3) SetID(ghost) + SetHue(0)  ← only after the corpse exists
        // If we fire OnDeath here (which transitions the player to a ghost
        // body in OnCharacterDeath) BEFORE CreateCorpse runs, the corpse
        // will be created with amount=0x192 (ghost) instead of the player's
        // real body, and the corpse on the ground renders as a ghost shape
        // instead of a normal humanoid corpse. Source-X ordering avoids
        // exactly this.
        var corpse = CreateCorpse(victim);
        corpse.SetTag("OWNER_UID", victim.Uid.Value.ToString());
        corpse.SetTag("OWNER_UUID", victim.Uuid.ToString("D"));

        if (effectiveKiller != null)
        {
            corpse.SetTag("KILLER_UID", effectiveKiller.Uid.Value.ToString());
            corpse.SetTag("KILLER_UUID", effectiveKiller.Uuid.ToString("D"));
        }

        // Drop equipped items and backpack contents to corpse
        if (victim.IsPlayer)
            DropLootToCorpse(victim, corpse);
        else
            DropNpcLootToCorpse(victim, corpse);

        // @DeathCorpse — fired on the victim once the corpse exists and the
        // loot has been transferred (Source-X CChar::Death fires it right
        // after MakeCorpse, which moves the items itself), with the corpse
        // as the argument object.
        TriggerDispatcher?.FireCharTrigger(victim, CharTrigger.DeathCorpse, new TriggerArgs
        {
            CharSrc = victim,
            O1 = corpse
        });

        // Now that the corpse has snapshotted the original body, fire the
        // death callbacks so OnCharacterDeath / OnNpcKill can swap the
        // mobile to its ghost body and broadcast the new appearance.
        OnDeath?.Invoke(victim, effectiveKiller);

        // Set corpse decay timer. Using the Item.DecayTime field (not a
        // TAG) routes this through the sector-tick Item.OnTick path —
        // the same mechanism spell fields and summoned items use.
        // Source-X does the same via CItem::_SetTimeout on the corpse,
        // driven from its sector, with no central scanner.
        int decaySeconds = victim.IsPlayer ? CorpseDecayPlayer : CorpseDecayNPC;
        corpse.DecayTime = Environment.TickCount64 + decaySeconds * 1000;

        // For NPCs, remove the mobile from world state immediately so it no
        // longer blocks movement or lingers in sector/object queries after the
        // corpse has been created. Bonded pets stay as ghosts (like players).
        if (!victim.IsPlayer && !victim.IsBonded)
        {
            _world.DeleteObject(victim);
            victim.Delete();
        }

        return corpse;
    }

    /// <summary>Apply Karma/Fame changes when killer kills victim.
    /// Source-X: Calc_FameKill + Calc_KarmaKill + Calc_KarmaScale.</summary>
    private static void ApplyKarmaFameChange(Character killer, Character victim)
    {
        // Fame: Source-X Calc_FameKill — PC kill /10, NPC kill /200
        int rawFame = Math.Max(0, (int)victim.Fame);
        int fameGain = victim.IsPlayer ? rawFame / 10 : rawFame / 200;
        // Clamp the magnitude but DON'T force a minimum of 1 — a zero-fame victim
        // (rabbit, bird) must grant zero fame, otherwise players farm trash mobs
        // for fame one point at a time.
        fameGain = Math.Min(fameGain, 200);
        ApplyFame(killer, fameGain);

        // Source-X: no karma loss for killing criminal/red
        if (victim.IsCriminal || victim.IsMurderer)
        {
            if (victim.Karma < 0)
            {
                int gain = Math.Clamp(-victim.Karma / 10, 1, 50);
                gain = ScaleKarma(killer.Karma, gain);
                ApplyKarma(killer, gain);
            }
            return;
        }

        // Source-X Calc_KarmaKill: karma change = negative of victim karma
        int karmaChange = -victim.Karma;
        if (victim.IsPlayer)
        {
            if (karmaChange < 0 && karmaChange > -5000)
                karmaChange = -5000;
            karmaChange /= 10;
        }
        else
        {
            if (karmaChange < 0 && karmaChange > -1000)
                karmaChange = -1000;
            karmaChange /= 20;
        }

        karmaChange = ScaleKarma(killer.Karma, karmaChange);
        ApplyKarma(killer, karmaChange);
    }

    /// <summary>Apply a Fame delta after firing @FameChange (Source-X Noto_Fame).
    /// A script returning null cancels the change; otherwise the (possibly
    /// adjusted) delta is clamped into [0, 10000].</summary>
    private static void ApplyFame(Character killer, int delta)
    {
        if (delta == 0) return;
        if (Character.OnFameChanging != null)
        {
            int? adjusted = Character.OnFameChanging(killer, delta);
            if (adjusted == null) return;
            delta = adjusted.Value;
        }
        killer.Fame = (short)Math.Clamp(killer.Fame + delta, 0, 10000);
    }

    /// <summary>Apply a Karma delta after firing @KarmaChange (Source-X Noto_Karma).
    /// A script returning null cancels the change; otherwise the (possibly
    /// adjusted) delta is clamped into [-10000, 10000].</summary>
    private static void ApplyKarma(Character killer, int delta)
    {
        if (delta == 0) return;
        if (Character.OnKarmaChanging != null)
        {
            int? adjusted = Character.OnKarmaChanging(killer, delta);
            if (adjusted == null) return;
            delta = adjusted.Value;
        }
        killer.Karma = (short)Math.Clamp(killer.Karma + delta, -10000, 10000);
    }

    /// <summary>Source-X Calc_KarmaScale: good chars lose karma 2x faster, gain 0.5x.</summary>
    private static int ScaleKarma(short currentKarma, int change)
    {
        if (currentKarma > 0)
        {
            if (change < 0) return change * 2;        // losing karma: double penalty
            if (change > 0 && change < currentKarma / 64) return 0; // diminishing returns
            return change / 2;                         // gaining karma: halved
        }
        return change;
    }

    /// <summary>Create a corpse item at the victim's position.</summary>
    private Item CreateCorpse(Character victim)
    {
        var corpse = _world.CreateItem();
        corpse.BaseId = 0x2006; // ITEMID_CORPSE
        corpse.Amount = victim.BodyId; // body type for corpse display
        string victimName = victim.GetDisplayName();
        corpse.Name = ServerMessages.GetFormatted(Msg.CorpseName, "corpse", victimName);
        corpse.SetTag("CORPSE_NAME", victimName);
        corpse.ItemType = ItemType.Corpse;
        corpse.Hue = victim.Hue;

        _world.PlaceItem(corpse, victim.Position);
        return corpse;
    }

    /// <summary>Drop player equipment and backpack to corpse.</summary>
    private void DropLootToCorpse(Character victim, Item corpse)
    {
        // Unequip all items (except hair, beard, etc.)
        Layer[] dropLayers = [
            Layer.OneHanded, Layer.TwoHanded, Layer.Shoes, Layer.Pants, Layer.Shirt,
            Layer.Helm, Layer.Gloves, Layer.Ring, Layer.Talisman, Layer.Neck,
            Layer.Waist, Layer.Chest, Layer.Bracelet, Layer.Tunic, Layer.Earrings,
            Layer.Arms, Layer.Cape, Layer.Robe, Layer.Skirt, Layer.Legs
        ];

        foreach (var layer in dropLayers)
        {
            var item = victim.Unequip(layer);
            if (item != null)
            {
                if (item.IsAttr(ObjAttributes.Blessed) || item.IsAttr(ObjAttributes.Blessed2) ||
                    item.IsAttr(ObjAttributes.Newbie) || item.IsAttr(ObjAttributes.Nodropt))
                {
                    victim.Equip(item, layer);
                    continue;
                }

                item.SetTag("EQUIPLAYER", ((byte)layer).ToString());
                corpse.AddItem(item);
            }
        }

        var pack = victim.Backpack;
        if (pack != null)
        {
            var contents = new List<Item>(pack.Contents);
            foreach (var item in contents)
            {
                if (item.IsAttr(ObjAttributes.Blessed) || item.IsAttr(ObjAttributes.Blessed2) ||
                    item.IsAttr(ObjAttributes.Newbie) || item.IsAttr(ObjAttributes.Nodropt))
                    continue;

                pack.RemoveItem(item);
                corpse.AddItem(item);
            }
        }
    }

    /// <summary>Drop NPC loot to corpse (all inventory + level-based loot).</summary>
    private void DropNpcLootToCorpse(Character victim, Item corpse)
    {
        DropLootToCorpse(victim, corpse);

        // Roll the NPC's deferred plain loot (chardef ITEM= entries that
        // were intentionally not materialised at spawn) straight into the
        // corpse — Source-X CTRIG_CreateLoot parity, and the reason living
        // NPCs carry no transient loot in the world save.
        victim.MaterializeDeathLoot(corpse);

        // Generate loot based on NPC brain type / stats tier
        int tier = Math.Max(1, (victim.Str + victim.Dex + victim.Int) / 60);

        // Gold drop — use a floor-free tier so weak creatures (rabbits, birds:
        // combined stats < 60) drop no gold at all, instead of a guaranteed 5+.
        int goldTier = (victim.Str + victim.Dex + victim.Int) / 60;
        int goldAmount = goldTier > 0 ? Random.Shared.Next(goldTier * 5, goldTier * 25 + 1) : 0;
        if (goldAmount > 0)
        {
            var gold = _world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Name = "Gold";
            gold.ItemType = ItemType.Gold;
            gold.Amount = (ushort)Math.Min(goldAmount, 60000);
            corpse.AddItem(gold);
        }

        // Random reagent drops (for magic creatures)
        if (victim.Int > 30 && Random.Shared.Next(100) < 40)
        {
            ushort[] reagents = [0x0F7A, 0x0F7B, 0x0F84, 0x0F85, 0x0F86, 0x0F88, 0x0F8C, 0x0F8D];
            var reagent = _world.CreateItem();
            reagent.BaseId = reagents[Random.Shared.Next(reagents.Length)];
            reagent.Name = "reagent";
            reagent.Amount = (ushort)Random.Shared.Next(1, tier + 1);
            corpse.AddItem(reagent);
        }

        // Gem drops for higher tier NPCs
        if (tier >= 3 && Random.Shared.Next(100) < 25)
        {
            ushort[] gems = [0x0F13, 0x0F15, 0x0F16, 0x0F18, 0x0F25, 0x0F26];
            var gem = _world.CreateItem();
            gem.BaseId = gems[Random.Shared.Next(gems.Length)];
            gem.Name = "gem";
            gem.Amount = 1;
            corpse.AddItem(gem);
        }
    }

    /// <summary>
    /// Source-X "Resurrect with Corpse" — when a character is resurrected
    /// while standing on (or owning) their own corpse, automatically
    /// re-equip every item that was equipped at death (using the
    /// EQUIPLAYER tag DropLootToCorpse stamped on it) and dump the rest
    /// back into the backpack. The corpse is deleted once empty.
    ///
    /// Returns true iff a matching corpse was found and processed (the
    /// caller can then skip the "you are still naked" path). If the
    /// resurrected character has no backpack yet (rare — fresh char),
    /// remaining items fall to the ground at the corpse position so
    /// they aren't lost.
    ///
    /// Edge cases handled:
    ///   * The corpse may have decayed/been looted between death and
    ///     resurrect — search returns no match, return false.
    ///   * An equip slot may already be occupied (e.g. NPC healer
    ///     handed the player a robe) — fall back to the backpack.
    ///   * The character may be standing one tile off — we sweep the
    ///     character's tile only (matches Source-X CChar::ResurrectFromCorpse
    ///     which reads <c>g_World.GetItemsAt(GetTopPoint())</c>).
    /// </summary>
    public bool RestoreFromCorpse(Character resurrected)
    {
        Item? corpse = null;
        foreach (var item in _world.GetItemsInRange(resurrected.Position, 2))
        {
            if (item.ItemType != ItemType.Corpse) continue;

            if (item.TryGetTag("OWNER_UUID", out string? uuidStr) &&
                Guid.TryParse(uuidStr, out Guid uuid) &&
                uuid == resurrected.Uuid)
            {
                corpse = item;
                break;
            }

            if (item.TryGetTag("OWNER_UID", out string? ownerStr) &&
                uint.TryParse(ownerStr, out uint ownerUid) &&
                ownerUid == resurrected.Uid.Value)
            {
                corpse = item;
                break;
            }
        }
        if (corpse == null) return false;

        // Snapshot first — RemoveItem mutates the underlying list and
        // would otherwise invalidate the iterator after the first call.
        var contents = new List<Item>(corpse.Contents);
        var pack = resurrected.Backpack;

        foreach (var item in contents)
        {
            corpse.RemoveItem(item);

            Layer? targetLayer = null;
            if (item.TryGetTag("EQUIPLAYER", out string? layerStr) &&
                byte.TryParse(layerStr, out byte layerByte))
            {
                targetLayer = (Layer)layerByte;
                item.RemoveTag("EQUIPLAYER");
            }

            bool placed = false;
            if (targetLayer.HasValue && targetLayer.Value != Layer.None)
            {
                // Re-equip on the original layer if free. Equip()
                // returns false on out-of-range; double-equipping the
                // same layer is handled internally by unequipping the
                // previous item, but we keep the slot-occupied check
                // explicit so that unexpected new gear (e.g. a healer
                // robe) isn't silently dropped on the ground.
                if (resurrected.GetEquippedItem(targetLayer.Value) == null &&
                    resurrected.Equip(item, targetLayer.Value))
                {
                    placed = true;
                }
            }

            if (!placed)
            {
                if (pack != null)
                {
                    pack.AddItem(item);
                    placed = true;
                }
                else
                {
                    _world.PlaceItem(item, resurrected.Position);
                    placed = true;
                }
            }
        }

        // Drop any remaining tags so a half-looted corpse doesn't keep
        // the killer/owner metadata alive on the recycled UID.
        corpse.RemoveTag("OWNER_UID");
        corpse.RemoveTag("OWNER_UUID");
        corpse.RemoveTag("KILLER_UID");
        corpse.RemoveTag("KILLER_UUID");

        _world.DeleteObject(corpse);
        return true;
    }

    /// <summary>
    /// Check if looting a corpse is a criminal act.
    /// Maps to CChar::CheckCorpseCrime in Source-X.
    /// </summary>
    public bool IsLootingCriminal(Character looter, Item corpse)
    {
        if (!LootingIsACrime) return false;
        if (looter.PrivLevel >= PrivLevel.GM) return false;

        // Own corpse is not criminal — UUID check first, then Serial fallback
        if (corpse.TryGetTag("OWNER_UUID", out string? ownerUuidStr) &&
            Guid.TryParse(ownerUuidStr, out Guid ownerUuid) &&
            ownerUuid == looter.Uuid)
            return false;

        if (corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
            uint.TryParse(ownerStr, out uint ownerUid) &&
            ownerUid == looter.Uid.Value)
            return false;

        if (corpse.TryGetTag("OWNER_UID", out string? ownerUidStr) &&
            uint.TryParse(ownerUidStr, out uint ownerUid2))
        {
            var ownerSerial = new Serial(ownerUid2);

            // Party member with loot rights is not criminal
            if (PartyManager != null)
            {
                var party = PartyManager.FindParty(looter.Uid);
                if (party != null && party.IsMember(ownerSerial) && party.GetLootFlag(looter.Uid))
                    return false;
            }

            // Guild member is not criminal (same guild = shared loot rights)
            var guildMgr = Character.ResolveGuildManager?.Invoke(looter.Uid);
            if (guildMgr != null)
            {
                var looterGuild = guildMgr.FindGuildFor(looter.Uid);
                var ownerGuild = guildMgr.FindGuildFor(ownerSerial);
                if (looterGuild != null && ownerGuild != null && looterGuild == ownerGuild)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Carve a corpse (for hides, meat, etc.).
    /// Maps to @CarveCorpse trigger in Source-X.
    /// </summary>
    public List<Item> CarveCorpse(Character carver, Item corpse)
    {
        var results = new List<Item>();
        if (corpse.ItemType != ItemType.Corpse) return results;
        if (corpse.TryGetTag("CARVED", out _)) return results; // once per corpse (reference m_carved)

        if (TriggerDispatcher?.FireItemTrigger(corpse, ItemTrigger.CarveCorpse, new TriggerArgs
        {
            CharSrc = carver,
            ItemSrc = corpse
        }) == TriggerResult.True)
            return results;

        // Reference Use_CarveCorpse: the parts come from the victim chardef's
        // RESOURCES list. Player-corpse parts are renamed "<part> of <victim>"
        // and fall to the ground with a decay timer; creature parts go into
        // the corpse container.
        Character? owner = null;
        if (corpse.TryGetTag("OWNER_UID", out string? ownerUidStr2) && uint.TryParse(ownerUidStr2, out uint carveOwnerUid))
            owner = _world.FindChar(new Serial(carveOwnerUid));
        var charDef = owner != null
            ? Definitions.DefinitionLoader.GetCharDef(owner.CharDefIndex)
            : Definitions.DefinitionLoader.GetCharDef(corpse.Amount);
        bool playerCorpse = owner?.IsPlayer ?? false;
        string victimName = corpse.TryGetTag("CORPSE_NAME", out string? vn) && !string.IsNullOrWhiteSpace(vn)
            ? vn!
            : owner?.GetDisplayName() ?? "";

        var resources = Definitions.DefinitionLoader.StaticResources;
        if (charDef != null && resources != null && charDef.CarveResources.Count > 0)
        {
            foreach (var (rid, amount, defName) in charDef.CarveResources)
            {
                int defIndex = Definitions.TemplateEngine.ResolveItemDefIndex(resources, defName);
                if (defIndex == 0) continue;
                ushort dispId = Definitions.TemplateEngine.ResolveDispId(resources, defName);
                if (dispId == 0) continue;

                var part = _world.CreateItem();
                part.BaseId = dispId;
                var idef = Definitions.DefinitionLoader.GetItemDef(defIndex);
                if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
                    part.Name = idef.Name;
                if (amount > 1)
                    part.Amount = (ushort)Math.Min(amount, ushort.MaxValue);

                if (playerCorpse)
                {
                    if (!string.IsNullOrEmpty(victimName))
                        part.Name = ServerMessages.GetFormatted(Msg.CorpseName, part.GetName(), victimName);
                    _world.PlaceItemWithDecay(part, corpse.Position);
                }
                else
                {
                    corpse.AddItem(part);
                }
                results.Add(part);
            }
        }

        if (results.Count == 0)
        {
            // Def carries no RESOURCES — keep the legacy random rolls so plain
            // creatures still yield something to the carver.
            if (Random.Shared.Next(100) < 70)
            {
                var hides = _world.CreateItem();
                hides.BaseId = 0x1079; // hides
                hides.Name = "hides";
                hides.Amount = (ushort)Random.Shared.Next(1, 4);
                AddToPackOrGround(carver, hides);
                results.Add(hides);
            }

            var meat = _world.CreateItem();
            meat.BaseId = 0x09F1; // raw ribs
            meat.Name = "raw ribs";
            meat.Amount = (ushort)Random.Shared.Next(1, 3);
            AddToPackOrGround(carver, meat);
            results.Add(meat);

            if (Random.Shared.Next(100) < 20)
            {
                var bones = _world.CreateItem();
                bones.BaseId = 0x0ECA; // bone pile
                bones.Name = "bones";
                bones.Amount = 1;
                AddToPackOrGround(carver, bones);
                results.Add(bones);
            }
        }

        corpse.SetTag("CARVED", "1");
        return results;
    }

    private void AddToPackOrGround(Character ch, Item item)
    {
        var pack = ch.Backpack;
        if (pack != null)
            pack.AddItem(item);
        else
            _world.PlaceItem(item, ch.Position);
    }

    // Corpse decay is now driven by the per-item Item.DecayTime /
    // Item.OnTick path (see Program.cs wiring of Item.OnCorpseDecay).
    // The full-world scan that used to live here burned ~100 ms per
    // tick on busy worlds and fought the sector-sleep design.
}
