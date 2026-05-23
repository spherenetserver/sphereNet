using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static SphereNet.Game.Clients.GameClient? FindGameClient(Character ch)
    {
        if (!ch.IsPlayer) return null;
        foreach (var c in _clients.Values)
            if (c.Character == ch) return c;
        return null;
    }

    /// <summary>Resolve a defmessage by key, returning empty string if missing.</summary>
    private static string SafeMsg(string key)
    {
        try { return SphereNet.Game.Messages.ServerMessages.Get(key) ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Pre-empt service-NPC well-known keywords before the SPEECH script
    /// chain runs. Returns true when a service action was dispatched
    /// (vendor menu opened, bank box opened, withdrawal completed, ...);
    /// false when the brain doesn't match or none of the keywords applied
    /// — in which case OnNpcHearSpeech keeps walking the chain.
    /// We also send the matching defmessage (NpcVendorBuyfast / "Here are
    /// thy N gold piece(s)." / ...) so the NPC speaks the same line a
    /// real Source-X server would.
    /// </summary>
    private static bool TryDispatchServiceKeyword(Character speaker, Character npc, string text)
    {
        string lower = text.ToLowerInvariant();
        string lowerName = (npc.Name ?? "").ToLowerInvariant();
        NpcBrainType brain = npc.NpcBrain;

        // Mirror the legacy widening: NPC=NPC_HUMAN service NPCs whose
        // names carry the role keyword should still respond as the
        // matching brain (banker / vendor / healer / stable).
        if (brain is NpcBrainType.Human or NpcBrainType.None)
        {
            if (lowerName.Contains("banker")) brain = NpcBrainType.Banker;
            else if (lowerName.Contains("vendor") || lowerName.Contains("shopkeep") ||
                     lowerName.Contains("merchant")) brain = NpcBrainType.Vendor;
        }

        if (brain == NpcBrainType.Vendor)
        {
            if (lower.Contains("buy") || lower.Contains("purchase"))
            {
                var gc = FindGameClient(speaker);
                _log.LogDebug(
                    "[svc_kw] VENDOR_BUY speaker={Speaker} npc={Npc} client={HasClient}",
                    speaker.Name, npc.Name, gc != null);
                gc?.OpenVendorBuy(npc);
                NpcSpeak(npc, SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorBuyfast)
                    ?? "Take a look at my goods.");
                return true;
            }
            if (lower.Contains("sell"))
            {
                var gc = FindGameClient(speaker);
                _log.LogDebug(
                    "[svc_kw] VENDOR_SELL speaker={Speaker} npc={Npc} client={HasClient}",
                    speaker.Name, npc.Name, gc != null);
                gc?.OpenVendorSell(npc);
                NpcSpeak(npc, SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorSellfast)
                    ?? "Show me what you have to sell.");
                return true;
            }
        }

        if (brain == NpcBrainType.Banker)
        {
            int withdrawAmount = TryParseAmountAfter(lower, "withdraw");
            int checkAmount = TryParseAmountAfter(lower, "check");
            bool wantBank = lower.Contains("bank") || lower == "deposit"
                            || lower.StartsWith("deposit ");

            if (lower.Contains("balance"))
            {
                long banked = CountBankGold(speaker);
                NpcSpeak(npc, $"Thou hast {banked} gold piece(s) in our care.");
                return true;
            }
            if (withdrawAmount > 0)
            {
                long banked = CountBankGold(speaker);
                if (banked < withdrawAmount)
                {
                    NpcSpeak(npc, $"You have only {banked} gold piece(s) in our care.");
                    return true;
                }
                RemoveBankGold(speaker, withdrawAmount);
                DepositGoldToBackpack(speaker, withdrawAmount);
                NpcSpeak(npc, $"Here are thy {withdrawAmount} gold piece(s).");
                FindGameClient(speaker)?.OpenBankBox();
                return true;
            }
            if (checkAmount > 0)
            {
                long banked = CountBankGold(speaker);
                if (banked < checkAmount)
                {
                    NpcSpeak(npc, $"You have only {banked} gold piece(s) in our care.");
                    return true;
                }
                if (!DepositBankCheckToBackpack(speaker, checkAmount))
                {
                    NpcSpeak(npc, "I am unable to issue a check for that amount right now.");
                    return true;
                }
                RemoveBankGold(speaker, checkAmount);
                NpcSpeak(npc, $"Here is thy check for {checkAmount} gold piece(s).");
                return true;
            }
            if (wantBank)
            {
                _log.LogDebug("[svc_kw] BANK_OPEN speaker={Speaker} npc={Npc}",
                    speaker.Name, npc.Name);
                FindGameClient(speaker)?.OpenBankBox();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Service NPC keyword response. We don't yet have a dedicated NPC
    /// overhead-speech broadcast, so the line is delivered as a system
    /// </summary>
    private static void NpcSpeak(Character npc, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var speechPacket = new PacketSpeechUnicodeOut(
            npc.Uid.Value,
            npc.BodyId,
            0x06,
            npc.SpeechColor != 0 ? npc.SpeechColor : (ushort)0x03B2,
            3,
            "TRK",
            npc.Name ?? "",
            line);
        BroadcastNearby(npc.Position, 14, speechPacket, 0);
    }

    /// <summary>
    /// Parse an integer amount that follows a keyword in a speech string,
    /// e.g. TryParseAmountAfter("withdraw 100", "withdraw") returns 100.
    /// Returns 0 when the keyword is missing, no amount follows, or the
    /// amount is non-positive. Tolerant of extra whitespace and trailing
    /// punctuation ("withdraw 100 gold").
    /// </summary>
    private static int TryParseAmountAfter(string text, string keyword)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return 0;
        int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int cur = idx + keyword.Length;
        while (cur < text.Length && !char.IsDigit(text[cur])) cur++;
        int start = cur;
        while (cur < text.Length && char.IsDigit(text[cur])) cur++;
        if (cur == start) return 0;
        if (!int.TryParse(text.AsSpan(start, cur - start), out int amount)) return 0;
        return amount > 0 ? amount : 0;
    }

    /// <summary>Total gold (item type Gold or 0x0EED) inside a character's bank box.</summary>
    private static long CountBankGold(Character ch)
    {
        var bank = ch.GetEquippedItem(SphereNet.Core.Enums.Layer.BankBox);
        if (bank == null) return 0;
        long total = 0;
        foreach (var item in _world.GetContainerContents(bank.Uid))
        {
            if (item.ItemType == SphereNet.Core.Enums.ItemType.Gold || item.BaseId == 0x0EED)
                total += item.Amount;
        }
        return total;
    }

    /// <summary>
    /// Withdraw N gold from a character's bank box. Walks gold piles from
    /// largest first (mirrors Source-X behaviour where the smallest number
    /// of stacks is consumed). Caller must check CountBankGold first.
    /// </summary>
    private static void RemoveBankGold(Character ch, int amount)
    {
        var bank = ch.GetEquippedItem(SphereNet.Core.Enums.Layer.BankBox);
        if (bank == null || amount <= 0) return;
        int remaining = amount;
        foreach (var item in _world.GetContainerContents(bank.Uid).ToList())
        {
            if (remaining <= 0) break;
            if (item.ItemType != SphereNet.Core.Enums.ItemType.Gold && item.BaseId != 0x0EED)
                continue;

            if (item.Amount <= remaining)
            {
                remaining -= item.Amount;
                item.Delete();
            }
            else
            {
                item.Amount -= (ushort)remaining;
                remaining = 0;
            }
        }
    }

    /// <summary>
    /// Drop a fresh gold pile into a character's backpack. Splits into 60k
    /// stacks (UO max amount per pile) so very large withdrawals still fit.
    /// </summary>
    private static void DepositGoldToBackpack(Character ch, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null || amount <= 0) return;
        while (amount > 0)
        {
            ushort slice = (ushort)Math.Min(amount, 60000);
            var gold = _world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Name = "Gold";
            gold.ItemType = SphereNet.Core.Enums.ItemType.Gold;
            gold.Amount = slice;
            pack.AddItem(gold);
            amount -= slice;
        }
    }

    private static bool DepositBankCheckToBackpack(Character ch, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null || amount <= 0)
            return false;

        var rid = _resources.ResolveDefName("i_bankcheck");
        if (!rid.IsValid || rid.Type != ResType.ItemDef)
            return false;

        var item = _world.CreateItem();
        var itemDef = DefinitionLoader.GetItemDef(rid.Index);
        ushort dispId = 0;
        if (itemDef != null)
        {
            if (itemDef.DispIndex != 0) dispId = itemDef.DispIndex;
            else if (itemDef.DupItemId != 0) dispId = itemDef.DupItemId;
        }
        if (dispId == 0 && rid.Index <= 0xFFFF)
            dispId = (ushort)rid.Index;
        if (dispId == 0)
            return false;

        item.BaseId = dispId;
        item.Name = string.IsNullOrWhiteSpace(itemDef?.Name) ? $"Bank check ({amount})" : itemDef!.Name;
        item.Price = amount;
        item.SetTag("BANKCHECK_AMOUNT", amount.ToString());
        pack.AddItem(item);
        return true;
    }

    /// <summary>
    /// Source-X CClient::Event_TalkBroadcast region keyword check. Fires exactly
    /// once per player utterance — currently handles "guards" / "help guards"
    /// inside REGION_FLAG_GUARDED zones. Future global keywords (e.g. "i resign
    /// from my guild" outside guild stones) hook in here too.
    /// </summary>
    private static void OnPlayerSpeech(Character speaker, string text, TalkMode mode)
    {
        if (string.IsNullOrEmpty(text)) return;
        string lower = text.ToLowerInvariant();
        bool calledGuards = lower.Contains("guards") || lower == "help" || lower.Contains("help guards");
        if (!calledGuards) return;

        var region = _world.FindRegion(speaker.Position);
        if (region == null || !region.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
        {
            _log.LogDebug("[guards] {Speaker} called guards but region={Region} guarded={Guarded} at {Pos}",
                speaker.Name, region?.Name ?? "(none)",
                region?.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded) ?? false,
                speaker.Position);
            return;
        }

        var hostiles = FindAllGuardTargets(speaker);

        var gc = FindGameClient(speaker);
        if (hostiles.Count == 0)
        {
            gc?.SysMessage("All looks quiet here.");
            return;
        }

        int killCount = 0;
        foreach (var hostile in hostiles)
        {
            if (hostile.IsDeleted || hostile.IsDead) continue;

            // Alert existing patrol guards in range toward this hostile
            _npcAI.AlertGuardsInRange(speaker.Position, hostile);

            var summonedGuard = FindNearbyGuardResponder(speaker.Position) ?? SummonCityGuardNear(hostile, region.Name);
            if (summonedGuard != null && !summonedGuard.IsDeleted)
            {
                summonedGuard.FightTarget = hostile.Uid;
                if (summonedGuard.Position.GetDistanceTo(hostile.Position) > 1)
                {
                    _world.MoveCharacter(summonedGuard, hostile.Position);
                    BroadcastCharacterAppear(summonedGuard);
                }
            }
            if (_config.GuardsInstantKill)
            {
                BroadcastLightningStrike(hostile);
                _deathEngine.ProcessDeath(hostile, summonedGuard);
                if (summonedGuard != null)
                {
                    summonedGuard.FightTarget = Serial.Invalid;
                    summonedGuard.RemoveTag("GUARD_YELLED");
                }
                killCount++;
            }
        }

        gc?.SysMessage(killCount > 0 ? "Guards strike down your attacker." : "The guards have been called.");
    }

    private static List<Character> FindAllGuardTargets(Character speaker)
    {
        var results = new List<Character>();
        foreach (var ch in _world.GetCharsInRange(speaker.Position, 14))
        {
            if (ch == speaker || ch.IsDead || ch.IsDeleted) continue;
            if (ch.PrivLevel >= PrivLevel.Counsel) continue;
            if (ch.NpcBrain == NpcBrainType.Guard) continue;

            if (ch.IsPlayer)
            {
                bool isCriminal = ch.IsCriminal || ch.IsStatFlag(StatFlag.Criminal);
                bool isMurderer = _config.GuardsOnMurderers && ch.IsMurderer;
                if (isCriminal || isMurderer) results.Add(ch);
                continue;
            }
            if (ch.NpcMaster.IsValid) continue;

            bool hostileNpc = ch.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon;
            if (hostileNpc) results.Add(ch);
        }
        return results;
    }

    private static void BroadcastLightningStrike(Character target)
    {
        var effect = new PacketEffect(1, 0, target.Uid.Value, 0,
            target.X, target.Y, (short)(target.Z + 20),
            target.X, target.Y, target.Z,
            6, 15, true, false);
        var sound = new PacketSound(0x0029, target.X, target.Y, target.Z);
        BroadcastNearby(target.Position, 18, effect, 0);
        BroadcastNearby(target.Position, 18, sound, 0);
    }

    private static Character? FindGuardTarget(Character speaker)
    {
        Character? bestPlayer = null;
        int bestPlayerDist = int.MaxValue;
        Character? bestNpc = null;
        int bestNpcDist = int.MaxValue;
        int scanned = 0;

        foreach (var ch in _world.GetCharsInRange(speaker.Position, 14))
        {
            scanned++;
            if (ch == speaker || ch.IsDead || ch.IsDeleted)
            {
                _log.LogDebug("[guard_scan] skip 0x{Uid:X} '{Name}' reason={Reason}",
                    ch.Uid.Value, ch.Name ?? "?",
                    ch == speaker ? "self" : ch.IsDead ? "dead" : "deleted");
                continue;
            }
            if (ch.PrivLevel >= PrivLevel.Counsel)
            {
                _log.LogDebug("[guard_scan] skip 0x{Uid:X} '{Name}' reason=staff", ch.Uid.Value, ch.Name ?? "?");
                continue;
            }

            int dist = ch.Position.GetDistanceTo(speaker.Position);
            _log.LogDebug("[guard_scan] eval 0x{Uid:X} '{Name}' brain={Brain} isPlayer={IsPlayer} master={Master} dist={Dist}",
                ch.Uid.Value, ch.Name ?? "?", ch.NpcBrain, ch.IsPlayer, ch.NpcMaster.Value, dist);

            if (ch.IsPlayer)
            {
                bool isCriminal = ch.IsCriminal || ch.IsStatFlag(StatFlag.Criminal);
                bool isMurderer = _config.GuardsOnMurderers && ch.IsMurderer;
                if ((isCriminal || isMurderer) && dist < bestPlayerDist)
                {
                    bestPlayer = ch;
                    bestPlayerDist = dist;
                }
                continue;
            }

            if (ch.NpcMaster.IsValid)
            {
                var owner = _world.FindChar(ch.NpcMaster);
                if (owner != null && !owner.IsDeleted && !owner.IsDead && owner.IsPlayer &&
                    owner.PrivLevel < PrivLevel.Counsel)
                {
                    bool petAggressingSpeaker = ch.FightTarget == speaker.Uid;
                    if (!petAggressingSpeaker && ch.TryGetTag("ATTACK_TARGET", out string? attackUid) &&
                        uint.TryParse(attackUid, out uint auid))
                    {
                        petAggressingSpeaker = auid == speaker.Uid.Value;
                    }

                    bool ownerCriminal = owner.IsCriminal || owner.IsStatFlag(StatFlag.Criminal);
                    bool ownerMurderer = _config.GuardsOnMurderers && owner.IsMurderer;
                    if ((petAggressingSpeaker || ownerCriminal || ownerMurderer) && dist < bestPlayerDist)
                    {
                        bestPlayer = owner;
                        bestPlayerDist = dist;
                    }
                }
                continue;
            }
            if (ch.NpcBrain == NpcBrainType.Guard)
                continue;

            bool hostileNpc = ch.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon;
            if (hostileNpc && dist < bestNpcDist)
            {
                bestNpc = ch;
                bestNpcDist = dist;
            }
        }

        _log.LogDebug("[guard_scan] scanned={Scanned} bestPlayer={BP} bestNpc={BN}",
            scanned, bestPlayer?.Name ?? "(none)", bestNpc?.Name ?? "(none)");
        return bestPlayer ?? bestNpc;
    }

    private static Character ResolveEffectiveOffender(Character offender)
    {
        if (offender.NpcMaster.IsValid)
        {
            var owner = _world.FindChar(offender.NpcMaster);
            if (owner != null && !owner.IsDeleted)
                return owner;
        }
        return offender;
    }

    private static Character? FindNearbyGuardResponder(Point3D center)
    {
        Character? nearest = null;
        int bestDist = int.MaxValue;
        foreach (var ch in _world.GetCharsInRange(center, 18))
        {
            if (ch.IsDeleted || ch.IsDead || ch.IsPlayer)
                continue;
            if (ch.NpcBrain != NpcBrainType.Guard)
                continue;
            int dist = ch.Position.GetDistanceTo(center);
            if (dist < bestDist)
            {
                nearest = ch;
                bestDist = dist;
            }
        }
        return nearest;
    }

    private static Character? SummonCityGuardNear(Character hostile, string regionName)
    {
        if (hostile.IsDeleted)
            return null;

        string defName = Random.Shared.Next(2) == 0 ? "C_GUARD" : "C_GUARD_F";
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != ResType.CharDef)
        {
            rid = _resources.ResolveDefName("C_GUARD");
            if (!rid.IsValid || rid.Type != ResType.CharDef)
            {
                _log.LogWarning("[guard] CHARDEF C_GUARD / C_GUARD_F not found in scripts, cannot summon guard");
                return null;
            }
        }

        var charDef = DefinitionLoader.GetCharDef(rid.Index);
        if (charDef == null)
        {
            _log.LogWarning("[guard] CharDef index 0x{Index:X} resolved but definition missing", rid.Index);
            return null;
        }

        var guard = _world.CreateCharacter();
        guard.IsPlayer = false;
        guard.CharDefIndex = rid.Index;

        ushort bodyId = charDef.DispIndex;
        if (bodyId == 0 && !string.IsNullOrWhiteSpace(charDef.DisplayIdRef))
        {
            var bodyRid = _resources.ResolveDefName(charDef.DisplayIdRef.Trim());
            if (bodyRid.IsValid)
            {
                var refDef = DefinitionLoader.GetCharDef(bodyRid.Index);
                if (refDef?.DispIndex > 0)
                    bodyId = refDef.DispIndex;
                else if (bodyRid.Index >= 0 && bodyRid.Index <= ushort.MaxValue)
                    bodyId = (ushort)bodyRid.Index;
            }
        }
        if (bodyId == 0) bodyId = 0x0190;
        guard.BodyId = bodyId;
        guard.BaseId = bodyId;

        if (!string.IsNullOrWhiteSpace(charDef.Name))
            guard.Name = DefinitionLoader.ResolveNames(charDef.Name);
        else
            guard.Name = string.IsNullOrWhiteSpace(regionName) ? "city guard" : $"{regionName} guard";

        int strVal = charDef.StrMax > 0 ? charDef.StrMax : Math.Max(1, charDef.StrMin);
        int dexVal = charDef.DexMax > 0 ? charDef.DexMax : Math.Max(1, charDef.DexMin);
        int intVal = charDef.IntMax > 0 ? charDef.IntMax : Math.Max(1, charDef.IntMin);
        guard.Str = (short)Math.Clamp(strVal, 1, short.MaxValue);
        guard.Dex = (short)Math.Clamp(dexVal, 1, short.MaxValue);
        guard.Int = (short)Math.Clamp(intVal, 1, short.MaxValue);
        int hits = charDef.HitsMax > 0 ? charDef.HitsMax : Math.Max(1, strVal);
        guard.MaxHits = (short)Math.Clamp(hits, 1, short.MaxValue);
        guard.Hits = guard.MaxHits;
        guard.MaxStam = guard.Dex;
        guard.Stam = guard.Dex;
        guard.MaxMana = guard.Int;
        guard.Mana = guard.Int;

        if (charDef.NpcBrain != NpcBrainType.None)
            guard.NpcBrain = charDef.NpcBrain;
        else
            guard.NpcBrain = NpcBrainType.Guard;

        guard.SetStatFlag(StatFlag.Invul);
        guard.SetTag("IS_CITY_GUARD", "1");
        guard.SetTag("GUARD_SPAWNED_AT", Environment.TickCount64.ToString());
        guard.FightTarget = hostile.Uid;

        _world.PlaceCharacter(guard, hostile.Position);

        _triggerDispatcher?.FireCharTrigger(guard, CharTrigger.Create, new TriggerArgs { CharSrc = guard });

        if (guard.NpcBrain == NpcBrainType.None)
            guard.NpcBrain = NpcBrainType.Guard;

        EquipGuardNewbieItems(guard, charDef);

        long lingerMs = Math.Max(1, _config.GuardLinger) * 1000L;
        long expireAt = Environment.TickCount64 + lingerMs;
        guard.SetTag("GUARD_EXPIRE_AT", expireAt.ToString());
        _summonedGuardExpiry[guard.Uid] = expireAt;

        BroadcastCharacterAppear(guard);
        return guard;
    }

    private static void EquipGuardNewbieItems(Character guard, CharDef charDef)
    {
        foreach (var entry in charDef.NewbieItems)
        {
            string defName = entry.DefName?.Trim() ?? "";
            if (defName.Length == 0) continue;

            var rid = _resources.ResolveDefName(defName);
            if (!rid.IsValid || rid.Type != ResType.ItemDef)
                continue;

            var itemDef = DefinitionLoader.GetItemDef(rid.Index);
            ushort dispId = 0;
            if (itemDef != null)
            {
                if (itemDef.DispIndex != 0) dispId = itemDef.DispIndex;
                else if (itemDef.DupItemId != 0) dispId = itemDef.DupItemId;
            }
            if (dispId == 0 && rid.Index <= 0xFFFF) dispId = (ushort)rid.Index;
            if (dispId == 0) continue;

            var item = _world.CreateItem();
            item.BaseId = dispId;
            if (itemDef != null && !string.IsNullOrWhiteSpace(itemDef.Name))
                item.Name = itemDef.Name;

            if (!string.IsNullOrWhiteSpace(entry.Color))
            {
                string cv = entry.Color!.Trim();
                var colorRid = _resources.ResolveDefName(cv);
                if (colorRid.IsValid)
                    item.Hue = new Core.Types.Color((ushort)colorRid.Index);
                else if (cv.StartsWith("0", StringComparison.Ordinal) &&
                         ushort.TryParse(cv, System.Globalization.NumberStyles.HexNumber, null, out ushort hue))
                    item.Hue = new Core.Types.Color(hue);
            }

            Layer layer = itemDef?.Layer ?? Layer.None;
            if (layer == Layer.None && _world.MapData != null)
            {
                var tile = _world.MapData.GetItemTileData(item.BaseId);
                if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 &&
                    tile.Quality > 0 && tile.Quality <= (byte)Layer.Horse)
                    layer = (Layer)tile.Quality;
            }

            if (layer != Layer.None)
                guard.Equip(item, layer);
        }
    }

    private static void OnNpcHearSpeech(Character speaker, Character npc, string text, TalkMode mode)
    {
        string lower = text.ToLowerInvariant();
        _log.LogDebug(
            "[npc_hear] {Speaker} -> {Npc} brain={Brain} text='{Text}'",
            speaker.Name, npc.Name, npc.NpcBrain, text);

        // Source-X global speech function hook — silent when missing.
        // Many imported script packs don't define this; warning on every
        // spoken line would drown the log.
        _triggerDispatcher?.Runner?.TryRunFunction(
            "f_onchar_speech",
            npc,
            null,
            new SphereNet.Scripting.Execution.TriggerArgs(speaker, (int)mode, 0, text)
            {
                Object1 = npc,
                Object2 = speaker
            },
            out _);

        // Fire trigger first — let scripts handle custom keywords
        var trigResult = _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCHearGreeting,
            new TriggerArgs { CharSrc = speaker, S1 = text });
        if (trigResult == TriggerResult.True)
        {
            _log.LogDebug("[npc_hear] {Npc} @NPCHearGreeting consumed text='{Text}'", npc.Name, text);
            return;
        }

        // Service-NPC well-known keywords (buy/sell/bank/balance/withdraw/
        // heal/stable/...) are handled by the built-in dispatcher BEFORE
        // the SPEECH script chain. Imported sphere packs ship TSPEECH=spk_jobSHOPKEEP
        // / spk_jobBANKER bodies whose verbs (actserv.dialog, ...) aren't
        // fully wired in our interpreter yet — letting the script "handle"
        // those keywords would silently swallow the request and the
        // vendor / bank window would never open. Pre-empting them here
        // keeps service NPCs functional until SPEECH bodies execute end
        // to end. Other speech (greetings, custom keywords) still flows
        // through FireSpeechTrigger below.
        if (TryDispatchServiceKeyword(speaker, npc, text))
            return;

        // Script-driven SPEECH triggers (from CHARDEF SPEECH/TSPEECH)
        var speechResult = _triggerDispatcher?.FireSpeechTrigger(npc, speaker, text);
        if (speechResult == TriggerResult.True)
        {
            _log.LogDebug("[npc_hear] {Npc} SPEECH trigger consumed text='{Text}'", npc.Name, text);
            return;
        }

        // Built-in keyword responses. Legacy Sphere saves commonly set
        // NPC=NPC_HUMAN on bankers/vendors/healers/stablemasters and
        // defer the real behaviour to a TSPEECH script block. When that
        // block isn't present on the shard, the service NPC becomes
        // mute. We widen the brain match so a Human-brain NPC whose
        // name contains the role keyword ("banker", "vendor"...) still
        // responds. InferredRole below collapses brain + name into a
        // single dispatch key.
        string? response = null;
        string lowerName = (npc.Name ?? "").ToLowerInvariant();
        NpcBrainType inferredBrain = npc.NpcBrain;
        if (inferredBrain is NpcBrainType.Human or NpcBrainType.None)
        {
            if (lowerName.Contains("banker")) inferredBrain = NpcBrainType.Banker;
            else if (lowerName.Contains("healer")) inferredBrain = NpcBrainType.Healer;
            else if (lowerName.Contains("stable") || lowerName.Contains("stablemaster"))
                inferredBrain = NpcBrainType.Stable;
            else if (lowerName.Contains("guard")) inferredBrain = NpcBrainType.Guard;
            else if (lowerName.Contains("vendor") || lowerName.Contains("shopkeep") ||
                     lowerName.Contains("merchant")) inferredBrain = NpcBrainType.Vendor;
        }

        switch (inferredBrain)
        {
            case NpcBrainType.Vendor:
                if (lower.Contains("buy") || lower.Contains("vendor buy") || lower.Contains("purchase"))
                {
                    // Source-X CClient::Event_TalkBroadcast → Cmd_VendorBuy:
                    // open the vendor buy window on the speaker's client.
                    var gc = FindGameClient(speaker);
                    _log.LogDebug(
                        "[vendor_speech] BUY speaker={Speaker} npc={Npc} brain={Brain} client={HasClient}",
                        speaker.Name, npc.Name, npc.NpcBrain, gc != null);
                    if (gc != null)
                        gc.OpenVendorBuy(npc);
                    response = SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorBuyfast);
                    if (string.IsNullOrEmpty(response))
                        response = "Take a look at my goods.";
                }
                else if (lower.Contains("sell") || lower.Contains("vendor sell"))
                {
                    var gc = FindGameClient(speaker);
                    _log.LogDebug(
                        "[vendor_speech] SELL speaker={Speaker} npc={Npc} brain={Brain} client={HasClient}",
                        speaker.Name, npc.Name, npc.NpcBrain, gc != null);
                    if (gc != null)
                        gc.OpenVendorSell(npc);
                    response = SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorSellfast);
                    if (string.IsNullOrEmpty(response))
                        response = "Show me what you have to sell.";
                }
                break;

            case NpcBrainType.Banker:
                {
                    // Source-X CCharNPC::OnTriggerSpeech banker brain handles
                    // a small set of keywords:
                    //   bank / deposit  -> open the bank box
                    //   balance         -> report the gold currently banked
                    //   withdraw N      -> move N gold from the bank into the
                    //                      speaker's backpack
                    //   check N         -> issue a bank check into the
                    //                      speaker's backpack
                    var gc = FindGameClient(speaker);
                    int withdrawAmount = TryParseAmountAfter(lower, "withdraw");
                    int checkAmount = TryParseAmountAfter(lower, "check");
                    bool wantBank = lower.Contains("bank") || lower == "deposit" || lower.StartsWith("deposit ");

                    if (lower.Contains("balance"))
                    {
                        long banked = CountBankGold(speaker);
                        response = $"Thou hast {banked} gold piece(s) in our care.";
                    }
                    else if (withdrawAmount > 0)
                    {
                        long banked = CountBankGold(speaker);
                        if (banked < withdrawAmount)
                            response = $"You have only {banked} gold piece(s) in our care.";
                        else
                        {
                            RemoveBankGold(speaker, withdrawAmount);
                            DepositGoldToBackpack(speaker, withdrawAmount);
                            response = $"Here are thy {withdrawAmount} gold piece(s).";
                            // Ensure the backpack-side delta is visible immediately.
                            gc?.OpenBankBox();
                        }
                    }
                    else if (checkAmount > 0)
                    {
                        long banked = CountBankGold(speaker);
                        if (banked < checkAmount)
                            response = $"You have only {banked} gold piece(s) in our care.";
                        else if (!DepositBankCheckToBackpack(speaker, checkAmount))
                            response = "I am unable to issue a check for that amount right now.";
                        else
                        {
                            RemoveBankGold(speaker, checkAmount);
                            response = $"Here is thy check for {checkAmount} gold piece(s).";
                            gc?.OpenBankBox();
                        }
                    }
                    else if (wantBank)
                    {
                        gc?.OpenBankBox();
                        response = "Here is your bank box.";
                    }
                }
                break;

            case NpcBrainType.Healer:
                if (lower.Contains("heal") || lower.Contains("resurrect") || lower.Contains("cure"))
                {
                    // Check if speaker is dead → resurrect
                    if (speaker.IsDead)
                    {
                        response = "Let me help you return to the living.";
                        foreach (var c in _clients.Values)
                        {
                            if (c.Character == speaker)
                            {
                                c.OnResurrect();
                                break;
                            }
                        }
                    }
                    else if (speaker.Hits < speaker.MaxHits)
                    {
                        speaker.Hits = speaker.MaxHits;
                        response = "You look much better now.";
                    }
                    else
                    {
                        response = "You look healthy to me.";
                    }
                }
                break;

            case NpcBrainType.Guard:
                if (lower.Contains("help") || lower.Contains("guards"))
                    response = "I shall protect this area.";
                break;

            case NpcBrainType.Stable:
                if (lower.Contains("stable"))
                {
                    // Find a pet near the player
                    Character? pet = null;
                    foreach (var ch in _world.GetCharsInRange(speaker.Position, 8))
                    {
                        if (!ch.IsPlayer && !ch.IsDead && ch.NpcMaster == speaker.Uid)
                        {
                            pet = ch;
                            break;
                        }
                    }
                    if (pet != null && _stableEngine.StablePet(speaker, pet, _world))
                        response = $"Your pet {pet.Name} has been stabled.";
                    else
                        response = "I don't see any of your pets nearby.";
                }
                else if (lower.Contains("claim"))
                {
                    var claimed = _stableEngine.ClaimPet(speaker, 0, _world, speaker.Position);
                    if (claimed != null)
                        response = $"Here is your pet {claimed.Name}.";
                    else
                        response = "You have no stabled pets.";
                }
                else
                {
                    int count = _stableEngine.GetStabledCount(speaker);
                    response = count > 0
                        ? $"You have {count} pet(s) stabled. Say 'claim' to retrieve one."
                        : "I can stable your pets for you. Just say 'stable'.";
                }
                break;
        }

        // Fallback: fire @NPCHearUnknown if no built-in response
        if (response == null)
        {
            _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCHearUnknown,
                new TriggerArgs { CharSrc = speaker, S1 = text });
            return;
        }

        // Send NPC speech response to nearby clients
        var speechPacket = new PacketSpeechUnicodeOut(
            npc.Uid.Value, npc.BodyId, 0, 0x03B2, 3, "TRK", npc.Name ?? "", response);
        BroadcastNearby(npc.Position, 18, speechPacket, 0);
    }
}
