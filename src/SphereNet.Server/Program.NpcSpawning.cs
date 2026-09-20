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
        SpawnComponent.OnNpcScriptInit = npc =>
        {
            dispatcher.FireCharTrigger(npc, CharTrigger.Create, new TriggerArgs { CharSrc = npc });
            if (npc.NpcBrain == NpcBrainType.None)
                npc.NpcBrain = NpcBrainType.Animal;
            dispatcher.FireCharTrigger(npc, CharTrigger.NPCRestock, new TriggerArgs { CharSrc = npc });
        };
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
