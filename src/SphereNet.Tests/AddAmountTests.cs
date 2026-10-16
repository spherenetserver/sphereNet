using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// ".ADD &lt;def&gt;[,amount]" (CV_ADD, CClient.cpp:937): the spoken command handed the
/// whole argument to the cursor, so "i_reag_garlic,10" was looked up as a definition of
/// that name and nothing was made.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class AddAmountTests
{
    [Fact]
    public void TheSpokenAddTakesAnAmountAfterTheComma()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_addamt_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, """
            [ITEMDEF 0f84]
            DEFNAME=i_reag_garlic
            """);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 9711);
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, gm);
        var commands = new SphereNet.Game.Speech.CommandHandler { Resources = resources };
        commands.RegisterDefaults(world);
        commands.OnAddTargetRequested += (_, token) => client.BeginAddTarget(token);
        client.SetEngines(commands: commands);

        client.HandleSpeech(0, 0x03B2, 3, ".add i_reag_garlic,10");
        client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, 101, 100, 0, 0);

        var garlic = world.GetItemsInRange(new Point3D(101, 100, 0, 0), 0).Single(i => i.BaseId == 0x0F84);
        Assert.Equal(10, garlic.Amount);
    }
}
