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
        if (_clientsByCharUid.TryGetValue(ch.Uid, out var indexed) && indexed.Character == ch)
            return indexed;
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
    /// <summary>Whole-word keyword match. The old substring Contains() made
    /// service NPCs react to unrelated chatter — Turkish "buyur"/"buyuk"/"buyu"
    /// all contain "buy", "banka" contains "bank" — so vendors opened the buy
    /// gump and barked on nearly every player-to-player sentence.</summary>
    private static bool HasWord(string lowerText, string word)
    {
        int idx = 0;
        while ((idx = lowerText.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = idx == 0 || !char.IsLetterOrDigit(lowerText[idx - 1]);
            int end = idx + word.Length;
            if (startOk && (end >= lowerText.Length || !char.IsLetterOrDigit(lowerText[end])))
                return true;
            idx = end;
        }
        return false;
    }

    /// <summary>Word starting with the keyword ("bank" also accepts
    /// "banka"/"bankaya"/"banker" — common no-diacritics Turkish usage).</summary>
    private static bool HasWordPrefix(string lowerText, string prefix)
    {
        int idx = 0;
        while ((idx = lowerText.IndexOf(prefix, idx, StringComparison.Ordinal)) >= 0)
        {
            if (idx == 0 || !char.IsLetterOrDigit(lowerText[idx - 1]))
                return true;
            idx += prefix.Length;
        }
        return false;
    }

    private static bool TryDispatchServiceKeyword(Character speaker, Character npc, string text)
    {
        string lower = text.ToLowerInvariant();
        NpcBrainType brain = npc.NpcBrain;

        // Source-X NPC_OnTrainHear: "train [skill]" — a townsfolk NPC teaches
        // the skills it masters. Bare "train" lists them; a named skill quotes
        // the price and arms the gold-payment step (TRAIN_PENDING on the
        // student, consumed by the drop-gold-on-NPC path).
        if (lower.StartsWith("train") && speaker.IsPlayer &&
            brain is NpcBrainType.Human or NpcBrainType.Vendor or NpcBrainType.Banker
                or NpcBrainType.Stable or NpcBrainType.Healer or NpcBrainType.None &&
            TryHandleTrainKeyword(speaker, npc, text))
        {
            return true;
        }

        // The trade keywords belong to every NPC_IsVendor brain - healer, banker,
        // vendor and stable master (CCharNPC::IsVendor, CCharNPC.cpp:251). The shop
        // list opening IS the answer: NV_BUY / NV_SELL say nothing when they succeed
        // (CCharNPCAct.cpp:147-211).
        if (SphereNet.Game.Trade.VendorEngine.IsVendorBrain(brain))
        {
            if (HasWord(lower, "buy") || HasWord(lower, "purchase"))
            {
                var gc = FindGameClient(speaker);
                _log.LogDebug(
                    "[svc_kw] VENDOR_BUY speaker={Speaker} npc={Npc} client={HasClient}",
                    speaker.Name, npc.Name, gc != null);
                gc?.OpenVendorBuy(npc);
                return true;
            }
            if (HasWord(lower, "sell"))
            {
                var gc = FindGameClient(speaker);
                _log.LogDebug(
                    "[svc_kw] VENDOR_SELL speaker={Speaker} npc={Npc} client={HasClient}",
                    speaker.Name, npc.Name, gc != null);
                gc?.OpenVendorSell(npc);
                return true;
            }
        }

        // A banker opens the bank box, as the BANKSELF verb a banker's SPEECH runs
        // (spk_jobBANKER: ON=*Bank* ... SRC.BANKSELF). Balances, withdrawals and
        // cheques are the pack's SPEECH business; the engine has no lines for them.
        if (brain == NpcBrainType.Banker && HasWordPrefix(lower, "bank"))
        {
            int bankDist = Math.Max(Math.Abs(speaker.X - npc.X), Math.Abs(speaker.Y - npc.Y));
            if (bankDist > 3 || speaker.MapIndex != npc.MapIndex) return false;
            _log.LogDebug("[svc_kw] BANK_OPEN speaker={Speaker} npc={Npc}",
                speaker.Name, npc.Name);
            FindGameClient(speaker)?.OpenBankBox();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Service NPC keyword response. We don't yet have a dedicated NPC
    /// overhead-speech broadcast, so the line is delivered as a system
    /// </summary>
    // Source-X sphere.ini TRAINSKILLPERCENT / TRAINSKILLMAX (defaults 30 / 420).
    // Single source of truth: VendorTrainingEngine, wired from config — this
    // keyword path and the paid-training flow must agree.
    private static int TrainSkillPercent => SphereNet.Game.Trade.VendorTrainingEngine.TrainSkillPercent;
    private static int TrainSkillMax => SphereNet.Game.Trade.VendorTrainingEngine.TrainSkillMax;

    /// <summary>Handle the "train [skill]" keyword (Source-X NPC_OnTrainHear).
    /// Returns false when the utterance is not really a train request (lets
    /// the rest of the speech chain run).</summary>
    private static bool TryHandleTrainKeyword(Character speaker, Character npc, string text)
    {
        string rest = text.Trim();
        rest = rest.Length > 5 ? rest[5..].Trim() : "";

        if (rest.Length == 0)
        {
            // List teachable skills: everything the NPC masters (>= 10.0) and
            // can still raise for this student.
            var teachable = new List<string>();
            for (int i = 0; i < (int)SphereNet.Core.Enums.SkillType.Qty; i++)
            {
                var sk = (SphereNet.Core.Enums.SkillType)i;
                int npcVal = npc.GetSkill(sk);
                if (npcVal < 100) continue;
                int cap = Math.Min(npcVal * TrainSkillPercent / 100, TrainSkillMax);
                if (speaker.GetSkill(sk) < cap)
                    teachable.Add(sk.ToString());
            }
            NpcSpeak(npc, teachable.Count == 0
                ? "There is nothing I can teach thee."
                : $"I can teach thee: {string.Join(", ", teachable)}.");
            return true;
        }

        // Named skill: exact enum match first, then prefix match ("anatom").
        string wanted = rest.Replace(" ", "");
        SphereNet.Core.Enums.SkillType? found = null;
        if (Enum.TryParse(wanted, ignoreCase: true, out SphereNet.Core.Enums.SkillType parsed) &&
            (int)parsed >= 0 && (int)parsed < (int)SphereNet.Core.Enums.SkillType.Qty)
        {
            found = parsed;
        }
        else
        {
            for (int i = 0; i < (int)SphereNet.Core.Enums.SkillType.Qty; i++)
            {
                var sk = (SphereNet.Core.Enums.SkillType)i;
                if (sk.ToString().StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    found = sk;
                    break;
                }
            }
        }
        if (found == null)
            return false; // not a skill name — let the speech chain continue

        var skill = found.Value;
        int trainerVal = npc.GetSkill(skill);
        if (trainerVal < 100)
        {
            NpcSpeak(npc, "I know not enough of that myself.");
            return true;
        }
        int maxTrain = Math.Min(trainerVal * TrainSkillPercent / 100, TrainSkillMax);
        int current = speaker.GetSkill(skill);
        if (current >= maxTrain)
        {
            NpcSpeak(npc, "Thou knowest already more than I can teach thee.");
            return true;
        }

        // 1 gold per 0.1 skill point (Source-X m_iTrainSkillCost default).
        int cost = maxTrain - current;
        speaker.SetTag("TRAIN_PENDING", $"{npc.Uid.Value}|{(int)skill}|{maxTrain}");
        NpcSpeak(npc,
            $"I can train thy {skill} up to {maxTrain / 10.0:0.#} for {cost} gold. Hand me the gold to begin.");
        return true;
    }

    private static void NpcSpeak(Character npc, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var speechPacket = new PacketSpeechUnicodeOut(
            npc.Uid.Value,
            npc.BodyId,
            0x06,
            npc.SpeechColor != 0 ? npc.SpeechColor : (ushort)0x03B2,
            3,
            PacketSpeechUnicodeOut.SystemLanguage,
            npc.GetName(),
            line);
        BroadcastNearby(npc.Position, 14, speechPacket, 0);
    }

    /// <summary>
    /// NPC training request (Source-X NPC_OnTrainHear). Recognise a skill name in
    /// the message, quote its price, and record a pending-training memory keyed by
    /// the student so a later gold hand-off (<see cref="TryPayForTraining"/>)
    /// completes it. With no skill named, advertise that the NPC teaches.
    /// </summary>
    private static string HandleTrainRequest(Character speaker, Character npc, string lowerText)
    {
        if (!TryMatchSkill(lowerText, out var skill))
            return "I can train thee — name the skill thou wishest to learn.";

        int amount = SphereNet.Game.Trade.VendorTrainingEngine
            .CalcTrainableAmount(npc, speaker, skill);
        if (amount <= 0)
            return $"I cannot teach thee more {skill}.";

        int cost = SphereNet.Game.Trade.VendorTrainingEngine.TrainCost(npc, amount);
        SphereNet.Game.Trade.VendorTrainingEngine.RememberOffer(npc, speaker, skill);
        return $"To train {(amount / 10.0):0.0} points of {skill} will cost thee {cost} gold. " +
               "Hand me the coin and I shall teach thee.";
    }

    /// <summary>Match the first base-skill whose enum name appears in the message.</summary>
    private static bool TryMatchSkill(string lowerText, out SkillType skill)
    {
        for (int i = 0; i < SphereNet.Game.Skills.SkillEngine.BaseSkillCount; i++)
        {
            var candidate = (SkillType)i;
            if (lowerText.Contains(candidate.ToString().ToLowerInvariant()))
            {
                skill = candidate;
                return true;
            }
        }
        skill = default;
        return false;
    }

    /// <summary>
    /// Source-X CClient::Event_TalkBroadcast region keyword check. Fires exactly
    /// once per player utterance — currently handles the GUARD / GUARDS call
    /// inside REGION_FLAG_GUARDED zones. Future global keywords (e.g. "i resign
    /// from my guild" outside guild stones) hook in here too.
    /// </summary>
    /// <summary>Returns (Cancel, Text): Cancel is true when the speaker's @Speech
    /// self-trigger cancelled the utterance (RETURN 1) — the caller then suppresses
    /// broadcast and NPC hear; Text is the utterance the trigger may have rewritten via
    /// ARGS (Source-X @Speech text rewrite), else the original.</summary>
    private static (bool Cancel, string Text) OnPlayerSpeech(Character speaker, string text, TalkMode mode)
    {
        if (string.IsNullOrEmpty(text)) return (false, text);
        if (_triggerDispatcher != null)
        {
            if (_triggerDispatcher.FireSpeechSelfTrigger(speaker, text, (int)mode,
                    out string rewritten, FindGameClient(speaker)) == TriggerResult.True)
                return (true, text); // @Speech RETURN 1 — cancel the whole utterance
            text = rewritten;        // @Speech may have rewritten <ARGS>
        }

        // Source-X Event_Talk_Common (CClientEvent.cpp:1870): GUARD or GUARDS as a
        // whole word. A substring test let "guardsman" call them, and "help" is not
        // a guard call there at all.
        if (!SpeechWords.IsGuardCall(text)) return (false, text);

        var region = _world.FindRegion(speaker.Position);
        if (region == null || !region.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
        {
            _log.LogDebug("[guards] {Speaker} called guards but region={Region} guarded={Guarded} at {Pos}",
                speaker.Name, region?.Name ?? "(none)",
                region?.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded) ?? false,
                speaker.Position);
            return (false, text);
        }

        // Calling the guards is silent in Source-X (CChar::CallGuards,
        // CCharFight.cpp:178): no line says the area is quiet or that the guards
        // are coming - the guard's own strike line is the answer.
        foreach (var hostile in FindAllGuardTargets(speaker))
            CallGuards(speaker, hostile);

        return (false, text);
    }

    /// <summary>Source-X CChar::CallGuards() (CCharFight.cpp:177): everyone around
    /// the caller that carries the criminal flag - or is evil, when
    /// GUARDSONMURDERERS is on - and that the caller may disturb (staff may not be
    /// reported).</summary>
    private static List<Character> FindAllGuardTargets(Character speaker)
    {
        var results = new List<Character>();
        foreach (var ch in _world.GetCharsInRange(speaker.Position, 14))
        {
            if (ch == speaker || ch.IsDead || ch.IsDeleted) continue;
            if (ch.PrivLevel >= PrivLevel.Counsel) continue;
            bool criminal = ch.IsCriminal || ch.IsStatFlag(StatFlag.Criminal);
            if (criminal || (_config.GuardsOnMurderers && _npcAI.NotoIsEvil(ch)))
                results.Add(ch);
        }
        return results;
    }

    /// <summary>Last CallGuards per caller (the _iTimeLastCallGuards spam check).</summary>
    private static readonly Dictionary<uint, long> _lastCallGuards = [];

    /// <summary>Source-X CChar::CallGuards(pCriminal) (CCharFight.cpp:215): nobody
    /// dead, no statue and no GM, and only against someone standing on guarded
    /// ground; at most once per 2.5 seconds per caller. A guard calling does the
    /// work itself, anyone else gets the first guard in sight that is free or
    /// already on this criminal; @CallGuards may refuse, and when no guard is at
    /// hand a new one is summoned onto the criminal. The guard then looks at the
    /// criminal as its own look-around would (NPC_LookAtCharGuard with
    /// bFromTrigger). Returns whether a guard was called.</summary>
    internal static bool CallGuards(Character caller, Character criminal)
    {
        if (_world == null || _npcAI == null || criminal == caller)
            return false;
        if (caller.IsDead || criminal.IsDead || criminal.IsDeleted ||
            (CharDefHelper.GetCanFlags(criminal) & CanFlags.C_Statue) != 0 ||
            criminal.PrivLevel >= PrivLevel.GM)
            return false;
        var criminalArea = _world.FindRegion(criminal.Position);
        if (criminalArea == null || !criminalArea.IsGuarded)
            return false;

        long now = Environment.TickCount64;
        if (_lastCallGuards.TryGetValue(caller.Uid.Value, out long last) && now - last <= 2500)
            return false;
        _lastCallGuards[caller.Uid.Value] = now;

        Character? guard = null;
        if (!caller.IsPlayer && caller.NpcBrain == NpcBrainType.Guard)
            guard = caller;
        else
        {
            foreach (var ch in _world.GetCharsInRange(caller.Position, 14))
            {
                if (ch.IsPlayer || ch.IsDead || ch.IsDeleted || ch.NpcBrain != NpcBrainType.Guard)
                    continue;
                if (ch.FightTarget == criminal.Uid || !ch.IsStatFlag(StatFlag.War))
                {
                    guard = ch;
                    break;
                }
            }
        }

        // @CallGuards (Source-X) — fired on the caller for the reported criminal
        // (<argo> = the criminal). RETURN 1 cancels the guard response.
        if (_triggerDispatcher?.FireCharTrigger(caller, CharTrigger.CallGuards,
                new TriggerArgs { CharSrc = caller, O1 = criminal }) == TriggerResult.True)
            return false;

        guard ??= SummonCityGuardNear(criminal, criminalArea.Name);
        if (guard == null || guard.IsDeleted)
            return false;

        _npcAI.GuardLookAtChar(guard, criminal, fromTrigger: true);
        return true;
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

        // GUARDLINGER is minutes, as upstream reads it (CServerConfig.cpp:1292).
        long lingerMs = Math.Max(1, _config.GuardLinger) * 60_000L;
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
            ushort dispId = SphereNet.Game.Definitions.ItemDefHelper.CreateGraphic(itemDef, rid.Index);
            if (dispId == 0) continue;

            var item = _world.CreateItem();
            item.BaseId = dispId;
            ItemDefHelper.ApplyInstanceMetadata(item, rid.Index, setDisplayId: false, setName: false);
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

        // Source-X NPC_OnHear: the NPC turns to face whoever addresses it
        // (unless mid-fight). The little head-turn is what makes townsfolk
        // feel alive when spoken to.
        //
        // Assigning Direction marks DirtyFlag.Direction, and the dirty->view pipeline
        // then delivers PacketMobileMoving (the exact packet, to the same nearby clients)
        // on the next view refresh. A synchronous BroadcastFacingUpdate here as well was
        // a redundant per-NPC client scan on every spoken line — the dominant cost of the
        // speech fan-out in NPC-dense areas — so the facing is left to the pipeline.
        if (!npc.FightTarget.IsValid && npc.Position != speaker.Position)
            npc.Direction = npc.Position.GetDirectionTo(speaker.Position);

        // NPC talk state (NPC_OnHear, CCharNPCAct.cpp:279-298): a busy talker says
        // the "interrupt" line to a new speaker.
        _npcAI?.NpcHearBegin(npc, speaker);

        // Source-X global speech function hook — silent when missing. Many imported
        // script packs don't define it, so it is gated by HasFunction: when absent (the
        // common case) every nearby NPC on every spoken line skips building a TriggerArgs
        // just to no-op inside TryRunFunction. Behaviour is unchanged when it IS defined.
        if (_triggerDispatcher?.Runner?.HasFunction("f_onchar_speech") == true)
            _triggerDispatcher.Runner.TryRunFunction(
                "f_onchar_speech",
                npc,
                null,
                new SphereNet.Scripting.Execution.TriggerArgs(speaker, (int)mode, 0, text)
                {
                    Object1 = npc,
                    Object2 = speaker
                },
                out _);

        // @NPCHearGreeting fires only on FIRST contact with this speaker (Source-X
        // NPC_OnHear: greeting when there is no MEMORY_SPEAK, then record it).
        // Firing it on every line would let a RETURN-1 greeting swallow all of the
        // speaker's subsequent speech, and re-greet endlessly. Per-line keyword
        // reactions go through the SPEECH triggers below, which still run each line.
        bool firstContact = npc.Memory_FindObjTypes(speaker.Uid,
            SphereNet.Core.Enums.MemoryType.Speak) == null;
        if (firstContact)
        {
            // The greeting fires BEFORE MEMORY_SPEAK is recorded, and a RETURN 1 leaves
            // it unrecorded, so the NPC greets this speaker again next time
            // (CCharNPCAct.cpp:301-314). Gated on IsTrigUsed like upstream.
            if (_triggerDispatcher != null &&
                _triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCHearGreeting))
            {
                var trigResult = _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCHearGreeting,
                    new TriggerArgs { CharSrc = speaker, S1 = text });
                if (trigResult == TriggerResult.True)
                {
                    _log.LogDebug("[npc_hear] {Npc} @NPCHearGreeting consumed text='{Text}'", npc.Name, text);
                    return;
                }
            }
            npc.Memory_AddObjTypes(speaker.Uid, SphereNet.Core.Enums.MemoryType.Speak);
        }

        // Script-driven SPEECH triggers (from CHARDEF SPEECH/TSPEECH) come FIRST,
        // as upstream runs them (NPC_OnHear, CCharNPCAct.cpp:317-359): a SPEECH
        // block that answers the line owns it, service keywords included - the
        // pack's own "buy"/"bank" blocks (their dead-player refusals and all) run
        // their BUY / SELL / BANKSELF verbs themselves.
        var speechResult = _triggerDispatcher?.FireSpeechTrigger(npc, speaker, text,
            (int)mode, FindGameClient(speaker));
        if (speechResult == TriggerResult.True)
        {
            _log.LogDebug("[npc_hear] {Npc} SPEECH trigger consumed text='{Text}'", npc.Name, text);
            // A speech block answered: the speaker is the new talk partner
            // (NPC_ActStart_SpeakTo, CCharNPCAct.cpp:332-336).
            _npcAI?.NpcStartSpeakTo(npc, speaker);
            return;
        }

        // Upstream's one hard-coded reaction (:362-366): a healer, or anyone with
        // 100.0 Spirit Speak, looks at the speaker - which is how a ghost asking a
        // healer gets resurrected.
        if ((npc.NpcBrain == NpcBrainType.Healer || npc.GetSkill(SkillType.SpiritSpeak) >= 1000) &&
            _npcAI != null && _npcAI.LookAtChar(npc, speaker))
            return;

        // SphereNet's fallback for a service NPC whose pack attaches no SPEECH for
        // these words: the trade, bank, training and stable keywords by BRAIN (the
        // role is never guessed from the name), silent like the verbs they stand
        // in for. Only reached when no SPEECH block took the line.
        if (TryDispatchServiceKeyword(speaker, npc, text))
            return;

        string? response = null;
        switch (npc.NpcBrain)
        {
            case NpcBrainType.Vendor:
                if (HasWord(lower, "teach"))
                {
                    response = HandleTrainRequest(speaker, npc, lower);
                }
                break;

            case NpcBrainType.Stable:
                if (HasWord(lower, "stable"))
                {
                    // Source-X stablemaster: ask the player to TARGET the pet to stable
                    // (instead of auto-picking the nearest). The callback validates
                    // ownership/summoned/distance via StableEngine.StablePet.
                    var gc = FindGameClient(speaker);
                    if (gc != null)
                    {
                        var stableNpc = npc;
                        gc.SetPendingTarget((serial, x, y, z, gfx) =>
                        {
                            var pet = _world.FindChar(new Serial(serial));
                            string reply = pet != null &&
                                    _stableEngine.StablePet(speaker, pet, _world, stableNpc)
                                ? $"Your pet {pet.Name} has been stabled."
                                : "You cannot stable that.";
                            var pkt = new PacketSpeechUnicodeOut(
                                stableNpc.Uid.Value, stableNpc.BodyId, 0, 0x03B2, 3, PacketSpeechUnicodeOut.SystemLanguage,
                                stableNpc.Name ?? "", reply);
                            BroadcastNearby(stableNpc.Position, 18, pkt, 0);
                        });
                        response = "Which pet wouldst thou stable?";
                    }
                    else
                    {
                        // Headless / no client (e.g. a scripted call) — fall back to the
                        // nearest owned pet so the flow still works server-side.
                        Character? pet = null;
                        foreach (var ch in _world.GetCharsInRange(speaker.Position, 8))
                        {
                            if (!ch.IsPlayer && !ch.IsDead && ch.NpcMaster == speaker.Uid)
                            {
                                pet = ch;
                                break;
                            }
                        }
                        response = pet != null && _stableEngine.StablePet(speaker, pet, _world, npc)
                            ? $"Your pet {pet.Name} has been stabled."
                            : "I don't see any of your pets nearby.";
                    }
                }
                else if (HasWord(lower, "claim"))
                {
                    var claimed = _stableEngine.ClaimPet(speaker, 0, _world, speaker.Position);
                    if (claimed != null)
                        response = $"Here is your pet {claimed.Name}.";
                    else
                        response = "You have no stabled pets.";
                }
                break;
        }

        // Fallback: fire @NPCHearUnknown if no built-in response
        if (response == null)
        {
            var unknownResult = _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCHearUnknown,
                new TriggerArgs { CharSrc = speaker, S1 = text });
            // Not understood while talking: count it (NPC_OnHear, CCharNPCAct.cpp:369-385).
            if (unknownResult != TriggerResult.True)
                _npcAI?.NpcHearUnknown(npc, speaker);
            return;
        }

        // Send NPC speech response to nearby clients
        var speechPacket = new PacketSpeechUnicodeOut(
            npc.Uid.Value, npc.BodyId, 0, 0x03B2, 3, PacketSpeechUnicodeOut.SystemLanguage, npc.GetName(), response);
        BroadcastNearby(npc.Position, 18, speechPacket, 0);
    }
}
