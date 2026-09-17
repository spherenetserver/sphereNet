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

            // Already working for somebody - upstream answers "employed" whoever
            // asks, including the owner (CCharNPCPet.cpp:805).
            if (npc.ResolveOwnerCharacter() != null)
            {
                NpcSpeak(npc, SafeMsg(Msg.NpcPetEmployed));
                return false;
            }

            NpcSpeak(npc, ServerMessages.GetFormatted(
                Random.Shared.Next(2) == 0 ? Msg.NpcPetHireAmnt : Msg.NpcPetHireRate,
                dayWage.ToString()));
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

            // A client gets the target cursor upstream opens; without one - a
            // delayed call, a console - fall back to the nearest owned pet so the
            // verb still does its job server-side.
            var client = FindGameClient(src);
            if (client != null)
            {
                var master = npc;
                client.SetPendingTarget((serial, _, _, _, _) =>
                {
                    var pet = _world.FindChar(new Serial(serial));
                    NpcSpeak(master, pet != null && _stableEngine.StablePet(src, pet, _world, master)
                        ? $"Your pet {pet.Name} has been stabled."
                        : "You cannot stable that.");
                });
                NpcSpeak(npc, "Which pet wouldst thou stable?");
                return true;
            }

            Character? nearest = null;
            foreach (var ch in _world.GetCharsInRange(src.Position, 8))
            {
                if (!ch.IsPlayer && !ch.IsDead && ch.NpcMaster == src.Uid)
                {
                    nearest = ch;
                    break;
                }
            }
            if (nearest == null || !_stableEngine.StablePet(src, nearest, _world, npc))
            {
                NpcSpeak(npc, "I don't see any of your pets nearby.");
                return false;
            }
            NpcSpeak(npc, $"Your pet {nearest.Name} has been stabled.");
            return true;
        };

        Character.NpcStablePetRetrieve = (npc, src) =>
        {
            if (src == null || _world == null)
                return false;
            if (npc.NpcBrain != SphereNet.Core.Enums.NpcBrainType.Stable)
                return false;

            // Upstream hands back EVERY stabled pet, not one (CCharNPCAct_Vendor.cpp:190).
            // Claiming index 0 repeatedly walks the list as it shrinks; the follower
            // cap can stop it part way, which is what ends the loop.
            int claimed = 0;
            while (_stableEngine.ClaimPet(src, 0, _world, src.Position) != null)
            {
                if (++claimed >= SphereNet.Game.NPCs.StableEngine.MaxStabledPets)
                    break;
            }

            NpcSpeak(npc, claimed > 0
                ? $"Here {(claimed == 1 ? "is thy pet" : $"are thy {claimed} pets")}."
                : "You have no stabled pets.");
            return true;
        };
    }
}
