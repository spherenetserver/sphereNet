using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A line written under a NEWBIE item belongs to that item.
///
/// Upstream applies any line that is not a template command to the item it most
/// recently created (CChar::ReadScriptReduced, CChar.cpp:1444 -> r_LoadVal). This
/// engine read ITEM, ITEMNEWBIE and COLOR and dropped the rest, and COLOR was only
/// ever the one case of that general rule it happened to implement.
///
/// What showed the gap: the reference distribution's necromancer professions write
///
///     ITEM=i_spellbook_necromancy
///     MORE1=08981
///
/// where MORE1 is the book's spell content. Dropped, the character is handed an
/// empty spellbook - which looks like a working starting kit right up until they
/// open it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NewbieItemPropertyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_nb_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    /// <summary>Run a NEWBIE section on a fresh character and hand back what landed
    /// in their pack.</summary>
    private List<Item> EquipFrom(string newbieBody)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "nb.scp");
        File.WriteAllText(file,
            "[ITEMDEF 0efa]" + Nl +
            "DEFNAME=i_probe_spellbook" + Nl +
            "TYPE=t_spellbook" + Nl + Nl +
            "[ITEMDEF 01f03]" + Nl +
            "DEFNAME=i_probe_robe" + Nl +
            "TYPE=t_normal" + Nl +
            "LAYER=22" + Nl + Nl +
            newbieBody);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 4966);
        client.SetEngines(commands: new CommandHandler { Resources = resources });

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        client.ApplyNewbieSection(ch, "PROBE_PROFESSION");

        var found = new List<Item>();
        var pack = ch.Backpack;
        if (pack != null) found.AddRange(pack.Contents);
        for (int i = 0; i <= (int)Layer.Horse; i++)
        {
            var worn = ch.GetEquippedItem((Layer)i);
            if (worn != null && worn != pack) found.Add(worn);
        }
        return found;
    }

    /// <summary>The line after the item reaches the item.</summary>
    [Fact]
    public void APropertyLineUnderAnItemIsAppliedToThatItem()
    {
        var items = EquipFrom(
            "[NEWBIE PROBE_PROFESSION]" + Nl +
            "ITEM=i_probe_spellbook" + Nl +
            "MORE1=08981" + Nl);

        var book = items.FirstOrDefault(i => i.BaseId == 0x0EFA);
        Assert.NotNull(book);
        Assert.True(book!.TryGetProperty("MORE1", out string more1));
        // Read back in the Sphere hex form a script writes and expects: 08981.
        Assert.Equal("08981", more1);
    }

    /// <summary>Each item takes its own lines, not the previous item's.</summary>
    [Fact]
    public void EachItemTakesOnlyTheLinesWrittenUnderIt()
    {
        var items = EquipFrom(
            "[NEWBIE PROBE_PROFESSION]" + Nl +
            "ITEM=i_probe_spellbook" + Nl +
            "MORE1=08981" + Nl +
            "ITEM=i_probe_robe" + Nl +
            "MORE1=01" + Nl);

        var book = items.First(i => i.BaseId == 0x0EFA);
        var robe = items.First(i => i.BaseId == 0x1F03);

        Assert.True(book.TryGetProperty("MORE1", out string bookMore));
        Assert.True(robe.TryGetProperty("MORE1", out string robeMore));
        Assert.Equal("08981", bookMore);
        Assert.Equal("01", robeMore);
    }

    /// <summary>COLOR keeps working - it is the same rule, and the hue resolution
    /// behind it is not a plain property write.</summary>
    [Fact]
    public void ColourStillResolvesThroughItsOwnPath()
    {
        var items = EquipFrom(
            "[NEWBIE PROBE_PROFESSION]" + Nl +
            "ITEM=i_probe_robe" + Nl +
            "COLOR=0455" + Nl);

        var robe = items.First(i => i.BaseId == 0x1F03);
        Assert.Equal(0x455, robe.Hue.Value);
    }
}
