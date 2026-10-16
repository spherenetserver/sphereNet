using Microsoft.Extensions.Logging;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A player eats what its body's FOODTYPE allows (Food_CanEat, CCharStatus.cpp:888).
/// Two things stood in the way: a player carries no BaseId, so its definition was
/// looked up as index 0 and it had no FOODTYPE at all (upstream's Char_GetDef reads the
/// ID, which for a player is the body); and the packs list t_food as a plain number
/// ([DEFNAME] t_food 8), which the diet matcher never read as the item type. A hungry
/// player was told "You can't really eat this." about a loaf of bread.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerFoodTypeTests
{
    [Fact]
    public void APlayerEatsWhatItsBodysFoodTypeAllows()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_foodtype_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, """
            [DEFNAME types]
            t_food 8
            t_food_raw 9
            t_fruit 129

            [CHARDEF 0190]
            DEFNAME=c_man
            FOODTYPE=15 t_food, t_fruit

            [ITEMDEF 0103b]
            DEFNAME=i_bread_loaf
            TYPE=t_food

            [ITEMDEF 09f1]
            DEFNAME=i_ribs_raw
            TYPE=t_food_raw
            """);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x0190;
        var bread = world.CreateItem();
        bread.BaseId = 0x103B;
        ItemDefHelper.ApplyInstanceMetadata(bread, 0x103B);
        var ribs = world.CreateItem();
        ribs.BaseId = 0x09F1;
        ItemDefHelper.ApplyInstanceMetadata(ribs, 0x09F1);
        var ai = new SphereNet.Game.AI.NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());

        Assert.Equal("c_man", player.GetRuleCharDef()?.DefName);
        Assert.Equal(0, player.CharDefIndex); // what is saved stays unset
        Assert.Equal(15, ai.NpcFoodQty(player, bread));
        Assert.Equal(0, ai.NpcFoodQty(player, ribs));
    }

    /// <summary>Use_Eat (CCharUse.cpp:953-990): a full eater is "simply too full" and
    /// keeps the food; a meal answers how full it left the eater, in sixths. A full
    /// player was told "You are stuffed!", and every meal "You eat the food."</summary>
    [Fact]
    public void EatingSaysHowFullItLeavesTheEater()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new SphereNet.Game.Accounts.AccountManager(lf), 9811);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new SphereNet.Core.Types.Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = SphereNet.Core.Enums.ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, SphereNet.Core.Enums.Layer.Pack);
        TestHarness.AttachCharacter(client, player);
        var bread = world.CreateItem();
        bread.BaseId = 0x103B;
        bread.ItemType = SphereNet.Core.Enums.ItemType.Food;
        bread.Amount = 3;
        pack.AddItem(bread);

        player.Food = player.MaxFood;
        string said = Said(client, () => client.HandleDoubleClick(bread.Uid.Value));
        Assert.Contains(SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.FoodCanteatf), said);
        Assert.Equal(3, bread.Amount);

        player.Food = 0;
        said = Said(client, () => client.HandleDoubleClick(bread.Uid.Value));
        Assert.Equal(2, bread.Amount);
        Assert.Contains(SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.FoodFull1), said);
    }

    private static string Said(SphereNet.Game.Clients.GameClient client, Action act)
    {
        TestHarness.ClearQueuedPackets(client.NetState);
        act();
        var sb = new System.Text.StringBuilder();
        foreach (var pkt in TestHarness.GetQueuedPackets(client.NetState))
        {
            var span = pkt.Span;
            if (span.Length > 48 && span[0] == 0xAE)
                sb.Append(System.Text.Encoding.BigEndianUnicode.GetString(span[48..])).Append('|');
            else if (span.Length > 44 && span[0] == 0x1C)
                sb.Append(System.Text.Encoding.ASCII.GetString(span[44..])).Append('|');
        }
        return sb.ToString().Replace("\0", "");
    }
}
