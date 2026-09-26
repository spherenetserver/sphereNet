using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Server;

/// <summary>
/// The NPC action verbs that need an engine: HIRE, TRAIN, SHRINK, PETSTABLE and
/// PETRETRIEVE (CCharNPC::sm_szVerbKeys, CCharNPCAct.cpp:44).
///
/// The object layer owns the verb NAME - whether the NPC recognises it - and this
/// owns what happens, because hiring, training, shrinking and stabling each live in
/// an engine the object model cannot see. Every one of these already had a working
/// path reached by SPEECH; none could be reached by a script writing the verb, which
/// is how the reference distribution drives them.
/// </summary>
public static partial class Program
{
    private static void WireNpcActionVerbs()
    {
        // HIRE: quote the day wage (NPC_OnHireHear, CCharNPCPet.cpp:776). The hire
        // itself completes when gold is handed over - that path already exists - so
        // this is the step that was missing: telling the player the price and
        // refusing the cases upstream refuses.
        Character.NpcHireQuote = (npc, src) =>
        {
            if (src == null)
                return false;

            uint dayWage = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.HireDayWage ?? 0;
            if (dayWage == 0 && npc.TryGetTag("HIRE_WAGE", out string? wageTag) &&
                uint.TryParse(wageTag, out uint taggedWage))
            {
                dayWage = taggedWage;
            }
            if (dayWage == 0)
            {
                NpcSpeak(npc, SafeMsg(Msg.NpcPetNotForHire));
                return false;
            }

            var mem = npc.Memory_FindObj(src.Uid);
            if (mem != null)
            {
                // The master asking again: the next gold goes toward the hire, and
                // the NPC says how long it is paid for (CCharNPCPet.cpp:790-796).
                if ((mem.GetMemoryTypes() & (SphereNet.Core.Enums.MemoryType.IPet |
                                             SphereNet.Core.Enums.MemoryType.Friend)) != 0)
                {
                    mem.More1 = (mem.More1 & 0xFFFF0000) | Character.NpcMemActSpeakHire;
                    long balance = npc.TryGetTag("HIRE_BALANCE", out string? bs) &&
                        long.TryParse(bs, out long b) ? b : 0;
                    NpcSpeak(npc, ServerMessages.GetFormatted(Msg.NpcPetHireTime,
                        (balance / Math.Max(1, dayWage)).ToString()));
                    return true;
                }
                // Nobody it fought, was harmed or irritated by (:797-801).
                if ((mem.GetMemoryTypes() & (SphereNet.Core.Enums.MemoryType.Fight |
                                             SphereNet.Core.Enums.MemoryType.HarmedBy |
                                             SphereNet.Core.Enums.MemoryType.IrritatedBy)) != 0)
                {
                    NpcSpeak(npc, SafeMsg(Msg.NpcPetNotWork));
                    return false;
                }
            }

            // Already working for somebody else (CCharNPCPet.cpp:805).
            if (npc.ResolveOwnerCharacter() != null)
            {
                NpcSpeak(npc, SafeMsg(Msg.NpcPetEmployed));
                return false;
            }

            NpcSpeak(npc, ServerMessages.GetFormatted(
                Random.Shared.Next(2) == 0 ? Msg.NpcPetHireAmnt : Msg.NpcPetHireRate,
                dayWage.ToString()));
            // The next gold this speaker hands over is the hire (:816-818).
            var hireMem = npc.Memory_AddObjTypes(src.Uid, SphereNet.Core.Enums.MemoryType.Speak);
            hireMem.More1 = (hireMem.More1 & 0xFFFF0000) | Character.NpcMemActSpeakHire;
            return true;
        };

        // TRAIN <skill>: the same handler the "train <skill>" utterance reaches
        // (NPC_OnTrainHear). Reconstructing the utterance keeps one implementation
        // of what a trainer will teach and for how much - two would drift.
        Character.NpcTrainOffer = (npc, src, skillArg) =>
            src != null && TryHandleTrainKeyword(src, npc, "train " + skillArg);

        // SHRINK: the NPC becomes a figurine. Upstream refuses unless the speaker
        // owns it, and an ARGUMENT means "put it in my pack" rather than leaving it
        // where the creature stood (CCharNPCAct.cpp:211-221).
        Character.NpcShrink = (npc, src, toPack) =>
        {
            if (src == null || _world == null)
                return false;

            var spot = npc.Position;
            var figurine = _world.CreateItem();
            figurine.BaseId = 0x2106;   // statuette graphic, as the shrink spell uses
            if (!SphereNet.Game.NPCs.PetFigurine.Shrink(src, npc, figurine, _world))
            {
                _world.RemoveItem(figurine);
                return false;
            }

            // Upstream points the speaker's ACT at the figurine so the script that
            // asked for the shrink can keep working with it.
            src.Act = figurine.Uid;

            var pack = toPack ? src.Backpack : null;
            if (pack != null)
                pack.AddItem(figurine);
            else
                _world.PlaceItemWithDecay(figurine, spot);
            return true;
        };

        // PETSTABLE / PETRETRIEVE: the stablemaster's two services, reached by the
        // "stable" and "claim" utterances already. Only a stablemaster retrieves
        // (upstream checks the brain first, CCharNPCAct_Vendor.cpp:183).
        Character.NpcStablePetSelect = (npc, src) =>
        {
            if (src == null || _world == null)
                return false;

            // NPC_StablePetSelect (CCharNPCAct_Vendor.cpp:100-170): only for a
            // connected player; the stable must have room, then the target cursor.
            var client = FindGameClient(src);
            if (client == null)
                return false;
            int stabled = _stableEngine.GetStabledCount(src);
            if (stabled >= _world.MaxContainerItems ||
                stabled >= SphereNet.Game.NPCs.StableEngine.GetMaxStabledPets(src, npc))
            {
                NpcSpeak(npc, SafeMsg(Msg.NpcStablemasterToomany));
                return false;
            }

            var master = npc;
            client.SetPendingTarget((serial, _, _, _, _) =>
            {
                // OnTarg_Pet_Stable (CClientTarg.cpp:1563-1627): every refusal has
                // its own line, and a stabled pet is answered with the CLAIM line.
                var pet = _world.FindChar(new Serial(serial));
                string? refusal = pet == null
                    ? Msg.NpcStablemasterTargFail
                    : _stableEngine.StablePetReason(src, pet, _world, master);
                NpcSpeak(master, SafeMsg(refusal ?? Msg.NpcStablemasterClaim));
            });
            client.SysMessage(SafeMsg(Msg.NpcStablemasterTarg));
            return true;
        };

        Character.NpcStablePetRetrieve = (npc, src) =>
        {
            if (src == null || _world == null)
                return false;
            if (npc.NpcBrain != SphereNet.Core.Enums.NpcBrainType.Stable)
                return false;

            // Upstream hands back EVERY stabled pet (CCharNPCAct_Vendor.cpp:190-207):
            // no cap and no range. When one cannot follow (the follower cap), it says
            // so by name and stops there.
            int claimed = 0;
            while (_stableEngine.GetStabledCount(src) > 0)
            {
                string name = _stableEngine.GetStabledPetNames(src)[0];
                if (_stableEngine.ClaimPet(src, 0, _world, src.Position) == null)
                {
                    NpcSpeak(npc, ServerMessages.GetFormatted(Msg.NpcStablemasterClaimFollower, name));
                    return true;
                }
                claimed++;
            }

            NpcSpeak(npc, SafeMsg(claimed > 0 ? Msg.NpcStablemasterClaim : Msg.NpcStablemasterClaimNopets));
            return true;
        };

        // Bare BUY / SELL: open the shop list for the speaker (NV_BUY / NV_SELL,
        // CCharNPCAct.cpp:147-211). Without a client there is nothing to open, and
        // upstream answers false.
        Character.NpcOpenShop = (npc, src, buy) =>
        {
            var client = src != null ? FindGameClient(src) : null;
            if (client == null)
                return false;
            if (buy)
                client.OpenVendorBuy(npc);
            else
                client.OpenVendorSell(npc);
            return true;
        };
    }
}
