using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.State;

namespace SphereNet.Tests;

internal static class TestHarness
{
    public static ILoggerFactory CreateLoggerFactory() => LoggerFactory.Create(_ => { });

    /// <summary>Make the uids of deleted objects available again.
    ///
    /// A delete no longer hands its uid straight back: the allocator holds it until a
    /// maintenance sweep completes, which is this engine's garbage collection and the
    /// point upstream rebuilds its own free list at (CWorld.cpp:655). That is what
    /// stops a stale script reference from resolving to whatever was created next.
    ///
    /// A test that needs the RECYCLED state - to prove a stale reference cannot seize
    /// the object that inherited its number - has to reach it the way a running server
    /// does, by letting the sweep run.</summary>
    public static void RecycleDeletedUids(GameWorld world)
    {
        const long farFuture = 10_000_000;   // past the sweep interval, so it arms at once
        world.TickSleepingMaintenance(farFuture);
        while (world.MaintenanceSweepActive)
            world.TickSleepingMaintenance(farFuture);
    }

    public static GameWorld CreateWorld()
    {
        var world = new GameWorld(CreateLoggerFactory());
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>Register a bare chardef (default: the human 0x190). A corpse is
    /// typed by its creature's chardef the way Source-X types it by _iPrev_id, and
    /// an untyped corpse cannot be carved at all.</summary>
    public static void SeedCharDef(int index = 0x0190) =>
        SphereNet.Game.Definitions.DefinitionLoader.SetCharDef(index,
            new SphereNet.Scripting.Definitions.CharDef(ResourceId.Invalid));

    /// <summary>Teach a vendor to buy an item: a sample in its BUYS box
    /// (LAYER_VENDOR_BUYS), where Source-X keeps what a vendor purchases. A vendor
    /// with no buy list buys nothing.</summary>
    public static void GiveVendorBuySample(GameWorld world, Character vendor, ushort baseId)
    {
        var buys = vendor.GetEquippedItem(SphereNet.Core.Enums.Layer.VendorBuy);
        if (buys == null)
        {
            buys = world.CreateItem();
            buys.ItemType = SphereNet.Core.Enums.ItemType.Container;
            vendor.Equip(buys, SphereNet.Core.Enums.Layer.VendorBuy);
        }
        var sample = world.CreateItem();
        sample.BaseId = baseId;
        buys.AddItem(sample);
    }

    /// <summary>Seed every skill with the classic sphere_skills.scp ADV_RATE
    /// curve (2.5,50.0,200.0). Skill gain strictly follows the curve — no
    /// curve means no gain (Source-X GetChancePercent) — so any test that
    /// expects gains must seed defs first.</summary>
    public static void SeedSkillAdvRates()
    {
        for (int i = 0; i < (int)SphereNet.Core.Enums.SkillType.Qty; i++)
        {
            var def = new SphereNet.Scripting.Definitions.SkillDef(ResourceId.Invalid);
            def.LoadFromKey("ADV_RATE", "2.5,50.0,200.0");
            SphereNet.Game.Definitions.DefinitionLoader.SetSkillDef(i, def);
        }
    }

    public static NetState CreateActiveNetState(ILoggerFactory loggerFactory, int id)
    {
        var state = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = id };
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        return state;
    }

    public static GameClient CreateClient(ILoggerFactory loggerFactory, GameWorld world, AccountManager accounts, int id)
    {
        var state = CreateActiveNetState(loggerFactory, id);
        return new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
    }

    public static Queue<PacketBuffer> GetQueuedPackets(NetState state)
    {
        // Combined outbound snapshot in flush order: priority queues drain
        // Highest → Idle. _queues is indexed by (int)PacketPriority.
        var queues = (Queue<PacketBuffer>[])typeof(NetState)
            .GetField("_queues", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!;

        var combined = new Queue<PacketBuffer>();
        for (int p = queues.Length - 1; p >= 0; p--)
            foreach (var pkt in queues[p])
                combined.Enqueue(pkt);
        return combined;
    }

    /// <summary>Drop everything queued so far, so what follows is measured on its own.
    ///
    /// The snapshot above is a COPY assembled from the priority queues, so calling
    /// Clear() on it cleared the copy and left every earlier packet in place - a test
    /// that cleared and then asserted "the client was sent X" was also seeing whatever
    /// the setup had sent, and passed with the behaviour it was pinning removed. This
    /// clears the real queues.</summary>
    public static void ClearQueuedPackets(NetState state)
    {
        var queues = (Queue<PacketBuffer>[])typeof(NetState)
            .GetField("_queues", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!;
        foreach (var q in queues)
            q.Clear();
    }

    public static void AttachCharacter(GameClient client, Character ch, Account? account = null)
    {
        typeof(GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        if (account != null)
        {
            typeof(GameClient)
                .GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, account);
        }
    }

    /// <summary>Run the world's TIMERF sweep (private: it is driven by the main loop).</summary>
    public static void PumpTimerF(GameWorld world, long nowMs) =>
        typeof(GameWorld)
            .GetMethod("TickTimerF", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(world, [nowMs]);

    public static void SetPrivateField<T>(GameClient client, string fieldName, T value)
    {
        typeof(GameClient)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, value);
    }

    /// <summary>Give map 0 an area covering the whole map that links every loaded
    /// REGIONTYPE which has resources - what an [AREADEF] with RESOURCES=/EVENTS=
    /// does in a real pack. Gathering reads only the region at the tile (Source-X
    /// CWorldMap::CheckNaturalResource), so a bare test world has to say where its
    /// resource tables apply.</summary>
    public static void AttachLoadedRegionTypes(GameWorld world, byte map = 0)
    {
        var field = typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_regionTypeDefs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var defs = (System.Collections.IDictionary)field.GetValue(null)!;
        var region = new SphereNet.Game.World.Regions.Region { Name = "test_resource_area", MapIndex = map };
        region.AddRect(0, 0, 6143, 4095);
        foreach (System.Collections.DictionaryEntry kv in defs)
        {
            if (kv.Value is SphereNet.Scripting.Definitions.RegionTypeDef { Resources.Count: > 0 })
                region.AddRegionType(new ResourceId(SphereNet.Core.Enums.ResType.RegionType, (int)kv.Key));
        }
        world.AddRegion(region);
    }
}
