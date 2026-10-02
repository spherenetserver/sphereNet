using SphereNet.Core.Enums;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;

namespace SphereNet.Server;

public static partial class Program
{
    private static void ConfigureNpcSpawnScripts(GameWorld world, TriggerDispatcher dispatcher)
    {
        // SpawnComponent calls both hooks in order. Only the first owns script
        // initialization; the completion hook must not generate equipment again.
        // Source-X splits a spawned NPC's script start in two (CCSpawn::GenerateChar):
        // NPC_LoadScript(true) - the CHARDEF's own @Create, then @NPCRestock - before
        // @Spawn and placement (CCSpawn.cpp:415), and NPC_CreateTrigger - the
        // TEVENTS/EVENTSPET @Create chain - once the creature is placed and attached
        // (CCSpawn.cpp:465).
        SpawnComponent.OnNpcScriptInit = npc =>
        {
            // Source-X GetNPCBrainAuto (CCharNPC.cpp:272): the body decides.
            if (npc.NpcBrain == NpcBrainType.None)
                npc.NpcBrain = npc.GetNpcBrainAuto();
            dispatcher.FireNpcLoadScript(npc, restock: true);
        };
        SpawnComponent.OnNpcCreateTrigger = npc => dispatcher.FireNpcCreateEvents(npc);
        // RESPAWN brings a dead NPC back through the same two halves
        // (CSector::RespawnDeadNPCs, CSector.cpp:1141/1146).
        world.NpcRespawnLoadScript = npc => dispatcher.FireNpcLoadScript(npc, restock: true);
        world.NpcRespawnCreateTrigger = npc => dispatcher.FireNpcCreateEvents(npc);
        world.OnNpcSpawned = npc =>
        {
            var def = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            if (def != null)
                foreach (int spell in def.NpcSpells)
                    if (spell >= 0 && spell <= ushort.MaxValue && Enum.IsDefined(typeof(SpellType), (ushort)spell))
                        npc.NpcSpellAdd((SpellType)spell);
            npc.Hits = npc.MaxHits;
            npc.Stam = npc.MaxStam;
            npc.Mana = npc.MaxMana;
        };
    }
}
