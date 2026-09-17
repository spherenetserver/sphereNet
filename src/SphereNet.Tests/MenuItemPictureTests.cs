using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// A MENU row names its picture with an ITEMDEF, not just a number.
///
/// Upstream reads the id as a RESOURCE - ResourceGetIndexType(RES_ITEMDEF, ...) -
/// and shows that definition's DISPID (CMenuItem::ParseLine, CClientDialog.cpp:305),
/// so "i_gold" and "0eed" are equally valid and "0" alone means no picture. It then
/// evaluates the row's TEXT with that definition as the object (:323), which is why
/// the packs write "ON=i_gold &lt;NAME&gt;" and get the item's name.
///
/// Only the numeric spelling was read here and the text was never expanded at all,
/// so the reference distribution's legacy add-menu - 723 rows naming their picture
/// by defname, 699 of them writing &lt;NAME&gt; for the label - opened with no
/// pictures and the literal text "&lt;NAME&gt;" on every row.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MenuItemPictureTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_menupic_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private void LoadDefs()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "menu.scp");
        File.WriteAllText(file,
            "[ITEMDEF 0eed]" + Nl +
            "DEFNAME=i_gold_menu" + Nl +
            "NAME=Gold Menu Coin" + Nl +
            "TYPE=t_gold" + Nl + Nl +
            // A def whose DISPID differs from its own key: the picture must come from
            // DISPID, which is why upstream reads that rather than the resource index.
            "[ITEMDEF i_ingot_menu]" + Nl +
            "DEFNAME=i_ingot_menu" + Nl +
            "ID=01bf2" + Nl);
        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    /// <summary>The resolution the parser performs, against the same definition table
    /// it reads: a defname resolves to a definition whose DISPID is the picture, a
    /// borrowed graphic comes from DISPID and not the def's own key, and an unknown
    /// name resolves to nothing.</summary>
    [Fact]
    public void ADefnameResolvesToTheDefinitionsDisplayId()
    {
        LoadDefs();

        // A plain [ITEMDEF 0eed] carries no DISPID: its own key IS the picture, and
        // reading DISPID alone would call it a bad id and drop the row.
        int goldIndex = DefinitionLoader.ResolveItemDefIndexByName("i_gold_menu");
        Assert.Equal(0x0EED, goldIndex);
        Assert.Equal(0, DefinitionLoader.GetItemDef(goldIndex)!.DispIndex);

        // A named def that borrows a graphic DOES carry one, and that is the picture.
        int ingotIndex = DefinitionLoader.ResolveItemDefIndexByName("i_ingot_menu");
        Assert.NotEqual(0, ingotIndex);
        Assert.Equal(0x1BF2, DefinitionLoader.GetItemDef(ingotIndex)!.DispIndex);

        Assert.Equal(0, DefinitionLoader.ResolveItemDefIndexByName("i_no_such_def_at_all"));
    }

    /// <summary>End to end: a MENU written the way the reference legacy add-menu
    /// writes it opens, and its rows carry the picture and the item's name.</summary>
    [Fact]
    public void AMenuRowNamingItsPictureByDefnameCarriesThePictureAndTheName()
    {
        LoadDefs();
        string file = Path.Combine(_dir, "menus.scp");
        File.WriteAllText(file,
            "[MENU MENU_PIC_TEST]" + Nl +
            "A Test Menu" + Nl +
            "ON=0 No picture at all" + Nl +
            "SYSMESSAGE plain" + Nl +
            "ON=i_gold_menu <NAME>" + Nl +
            "SYSMESSAGE gold" + Nl +
            "ON=01bf2 By number" + Nl +
            "SYSMESSAGE numeric" + Nl);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 4988);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        client.SetEngines(commands: new SphereNet.Game.Speech.CommandHandler { Resources = resources });

        Assert.True(client.TryExecuteScriptCommand(ch, "MENU", "MENU_PIC_TEST", null));

        var options = ((SphereNet.Game.Clients.IClientContext)client).PendingMenuOptions;
        Assert.NotNull(options);
        Assert.Equal(3, options!.Count);

        Assert.Equal(0, options[0].ModelId);                 // ON=0 - no picture
        Assert.Equal("No picture at all", options[0].Text);

        Assert.Equal(0x0EED, options[1].ModelId);            // the defname's DISPID
        Assert.Equal("Gold Menu Coin", options[1].Text);     // <NAME> became the def's

        Assert.Equal(0x1BF2, options[2].ModelId);            // the numeric form still works
        Assert.Equal("By number", options[2].Text);
    }
}
