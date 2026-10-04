using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The character context menu uses the reference's entry tags and clilocs
/// (Event_AOSPopupMenuRequest, CClientEvent.cpp:2608-2724).
///
/// The engine used to add a "Dismount" line of its own and sent it with cliloc
/// 3006112, which the client prints as "Command: Stop", so a rider choosing Stop
/// got off the horse. Upstream has no mount entries; 3006112 belongs to the pet's
/// POPUP_PETSTOP (135), which is the pet hearing "stop".
/// </summary>
public sealed class ContextMenuPetEntryTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient Client, Character Actor) NewClient(GameWorld world, int id)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        var actor = world.CreateCharacter();
        actor.BodyId = 0x0190;
        actor.IsPlayer = true;
        world.PlaceCharacter(actor, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, actor);
        return (client, actor);
    }

    /// <summary>The (tag, cliloc) pairs of the context menu sent for <paramref name="uid"/>.</summary>
    private static List<(ushort Tag, uint Cliloc)> Menu(GameClient client, uint uid)
    {
        TestHarness.ClearQueuedPackets(client.NetState);
        client.WorldFeatures.SendContextMenu(uid);
        var result = new List<(ushort, uint)>();
        foreach (var buf in TestHarness.GetQueuedPackets(client.NetState))
        {
            var span = buf.Span;
            if (span.Length < 13 || span[0] != 0xBF || span[3] != 0x00 || span[4] != 0x14) continue;
            bool newFormat = span[6] == 2;
            int count = span[11];
            for (int i = 0; i < count; i++)
            {
                if (newFormat)
                {
                    int at = 12 + i * 8;
                    uint cliloc = (uint)((span[at] << 24) | (span[at + 1] << 16) | (span[at + 2] << 8) | span[at + 3]);
                    result.Add(((ushort)((span[at + 4] << 8) | span[at + 5]), cliloc));
                }
                else
                {
                    int at = 12 + i * 6;
                    result.Add(((ushort)((span[at] << 8) | span[at + 1]),
                        3000000u + (uint)((span[at + 2] << 8) | span[at + 3])));
                }
            }
        }
        return result;
    }

    [Fact]
    public void TheOwnMenuHasNoStopLine()
    {
        var world = CreateWorld();
        var (client, me) = NewClient(world, 7101);

        var menu = Menu(client, me.Uid.Value);

        Assert.Contains(menu, e => e.Tag == 520 && e.Cliloc == 3006123);  // paperdoll
        Assert.Contains(menu, e => e.Tag == 302 && e.Cliloc == 3006145);  // backpack
        Assert.DoesNotContain(menu, e => e.Cliloc == 3006112);
    }

    [Fact]
    public void AnOwnedPetOffersTheCommandsAndStopStopsIt()
    {
        var world = CreateWorld();
        var (client, owner) = NewClient(world, 7102);
        var pet = world.CreateCharacter();
        pet.Name = "rex";
        pet.BodyId = 0x00C8;
        pet.NpcMaster = owner.Uid;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        pet.PetAIMode = PetAIMode.Follow;

        var menu = Menu(client, pet.Uid.Value);
        Assert.Contains(menu, e => e.Tag == 135 && e.Cliloc == 3006112);  // POPUP_PETSTOP
        Assert.Contains(menu, e => e.Tag == 134 && e.Cliloc == 3006111);  // POPUP_PETKILL

        client.WorldFeatures.HandleContextMenuResponse(pet.Uid.Value, 135);

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }
}
