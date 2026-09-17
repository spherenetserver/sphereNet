using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// What a script can read back off a definition through SERV.CHARDEF / SERV.ITEMDEF.
///
/// The read that showed the gap is the era-display hue. 141 lines across the shipped
/// packs write, inside an @Create body,
///
///     COLOR=&lt;SERV.CHARDEF.&lt;BASEID&gt;.RESDISPDNHUE&gt;
///
/// and the resolver had no case for RESDISPDNHUE, so the read produced an empty
/// string and the assignment set the creature's hue to nothing. Every one of those
/// creatures spawned at the default colour. The key is parsed into a typed field on
/// the definition, which is also why the ITEMDEF resolver's tag fallback did not
/// cover it - an unparsed key lands in the tags, a parsed one does not.
///
/// The CHARDEF resolver had no tag fallback at all, so any key the chardef parser
/// does not name read back as "" there as well.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DefinitionReadbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_dr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Nl = "\r\n";

    private static string Resolve(string property)
    {
        var method = typeof(SphereNet.Server.Program)
            .GetMethod("ResolveServerProperty", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [property]) ?? "";
    }

    /// <summary>Load a script and point the server's resolver at it.</summary>
    private void Load(string script)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "d.scp");
        File.WriteAllText(file, script);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        typeof(SphereNet.Server.Program)
            .GetField("_resources", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, resources);
    }

    /// <summary>The read the packs actually make, on a chardef.</summary>
    [Fact]
    public void AChardefAnswersForItsEraDisplayHue()
    {
        Load(
            "[CHARDEF c_probe_beast]" + Nl +
            "ID=0190" + Nl +
            "NAME=probe beast" + Nl +
            "RESLEVEL=3" + Nl +
            "RESDISPDNHUE=0482" + Nl +
            "RESDISPDNID=c_probe_beast" + Nl);

        Assert.Equal("0482", Resolve("CHARDEF.c_probe_beast.RESDISPDNHUE"));
        Assert.Equal("3", Resolve("CHARDEF.c_probe_beast.RESLEVEL"));
        // Written as a defname and read back as one, the way upstream answers it
        // (ResourceGetName over the stored index, CCharBase.cpp:277).
        Assert.Equal("c_probe_beast", Resolve("CHARDEF.c_probe_beast.RESDISPDNID"));
    }

    /// <summary>And on an itemdef, where the same three keys are typed and so were
    /// also missed by the tag fallback.</summary>
    [Fact]
    public void AnItemdefAnswersForItsEraDisplayHue()
    {
        Load(
            "[ITEMDEF 013bb]" + Nl +
            "DEFNAME=i_probe_blade" + Nl +
            "TYPE=t_weapon_sword" + Nl +
            "RESLEVEL=5" + Nl +
            "RESDISPDNHUE=0410" + Nl);

        Assert.Equal("0410", Resolve("ITEMDEF.i_probe_blade.RESDISPDNHUE"));
        Assert.Equal("5", Resolve("ITEMDEF.i_probe_blade.RESLEVEL"));
    }

    /// <summary>The scalars a chardef carries that upstream answers for and this
    /// resolver did not. MOVERATE is the one with a live consumer - the NPC step
    /// cadence reads it - so a script could change the pacing it could not read.</summary>
    [Fact]
    public void AChardefAnswersForItsBaseScalars()
    {
        Load(
            "[CHARDEF c_probe_beast]" + Nl +
            "ID=0190" + Nl +
            "MOVERATE=40" + Nl +
            "HIREDAYWAGE=70" + Nl +
            "BLOODCOLOR=0" + Nl +
            "FOLLOWERSLOTS=2" + Nl +
            "DAM=6,10" + Nl +
            "ARMOR=4" + Nl +
            "CATEGORY=Evil NPC" + Nl +
            "SUBSECTION=Japanese" + Nl +
            "DESCRIPTION=Ninja (male)" + Nl);

        Assert.Equal("40", Resolve("CHARDEF.c_probe_beast.MOVERATE"));
        Assert.Equal("70", Resolve("CHARDEF.c_probe_beast.HIREDAYWAGE"));
        Assert.Equal("2", Resolve("CHARDEF.c_probe_beast.FOLLOWERSLOTS"));
        Assert.Equal("6,10", Resolve("CHARDEF.c_probe_beast.DAM"));
        Assert.Equal("4", Resolve("CHARDEF.c_probe_beast.ARMOR"));
        Assert.Equal("Evil NPC", Resolve("CHARDEF.c_probe_beast.CATEGORY"));
        Assert.Equal("Japanese", Resolve("CHARDEF.c_probe_beast.SUBSECTION"));
        Assert.Equal("Ninja (male)", Resolve("CHARDEF.c_probe_beast.DESCRIPTION"));
    }

    /// <summary>A key the chardef parser does not name is kept in the definition's
    /// tags, and reading it back now gets what the pack wrote - the same fallback
    /// the ITEMDEF resolver already had.</summary>
    [Fact]
    public void AChardefAnswersForAnUnparsedKeyFromItsTags()
    {
        Load(
            "[CHARDEF c_probe_beast]" + Nl +
            "ID=0190" + Nl +
            "TAG.BARDING.DIFF=92.2" + Nl);

        Assert.Equal("92.2", Resolve("CHARDEF.c_probe_beast.TAG.BARDING.DIFF"));
    }

    /// <summary>A name the resolver has no case for and no tag behind still reads
    /// back empty, rather than picking up something else.</summary>
    [Fact]
    public void AnUnknownKeyStillReadsBackEmpty()
    {
        Load(
            "[CHARDEF c_probe_beast]" + Nl +
            "ID=0190" + Nl);

        Assert.Equal("", Resolve("CHARDEF.c_probe_beast.NOTHING_WRITES_THIS"));
    }
}
