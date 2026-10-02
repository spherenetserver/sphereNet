using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// The attribute and race lines of an item's AOS tooltip
/// (CClient::AOSTooltip_addDefaultItemData, CClientMsg_AOSTooltip.cpp:365-389), and
/// the Source-X ATTR_* bit numbers they read (CItem.h:113-153).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemAttributeTooltipTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "spn_iat_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (uint Cliloc, string Args)[] Build(Action<Item> setup,
        ItemType type = ItemType.Normal, params string[] defLines)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "t.scp");
        var lines = new List<string>
        {
            "[ITEMDEF 0eed]",
            "DEFNAME=i_probe_attr",
            "NAME=probe",
        };
        lines.AddRange(defLines);
        File.WriteAllLines(file, lines);

        using var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = _dir };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 5211);
        client.SetEngines(triggerDispatcher: dispatcher);
        client.NetState.ClientVersionNumber = 70_020_000;   // AOS tooltips on

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        item.ItemType = type;
        setup(item);
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));

        client.SkillUse.SendAosTooltip(item, requested: true);
        return item.TooltipCache?.Properties ?? [];
    }

    /// <summary>Every attribute line, after the name and in upstream's order, however
    /// the bits were set.</summary>
    [Fact]
    public void AttributeLinesFollowTheNameInUpstreamOrder()
    {
        var props = Build(i =>
        {
            i.SetAttr(ObjAttributes.NoTrade | ObjAttributes.NoDrop | ObjAttributes.Newbie |
                ObjAttributes.Magic | ObjAttributes.QuestItem | ObjAttributes.Insured |
                ObjAttributes.Cursed | ObjAttributes.Blessed | ObjAttributes.Secure |
                ObjAttributes.LockedDown);
        });

        Assert.Equal(1050045u, props[0].Cliloc);
        Assert.Equal(new (uint, string)[]
        {
            (501643, ""),       // Locked Down
            (501644, ""),       // Locked Down & Secured
            (1038021, ""),      // Blessed
            (1049643, ""),      // Cursed
            (1061682, ""),      // Insured
            (1072351, ""),      // Quest Item
            (3010064, ""),      // Magic
            (1070722, "Newbie"),
            (1076253, ""),      // NO-DROP
            (1076255, ""),      // NO-TRADE
        }, props.Skip(1).ToArray());
    }

    /// <summary>An item with none of those bits gets only its name - the default data
    /// for a plain item is unchanged.</summary>
    [Fact]
    public void AnItemWithoutAttributesIsUnchanged()
    {
        var props = Build(i => i.SetAttr(ObjAttributes.Identified | ObjAttributes.Decay |
            ObjAttributes.Move_Never | ObjAttributes.Blessed2 | ObjAttributes.Cursed2));

        Assert.Single(props);
        Assert.Equal(1050045u, props[0].Cliloc);
    }

    /// <summary>The script numbers a pack uses (attr_lockeddown 01000000, attr_insured
    /// 0100000, attr_secure 02000000) land on the right lines.</summary>
    [Fact]
    public void ScriptAttrNumbersUseSourceXBits()
    {
        var props = Build(i => Assert.True(i.TrySetProperty("ATTR", "01100000")));

        Assert.Equal(new uint[] { 1050045, 501643, 1061682 }, props.Select(p => p.Cliloc).ToArray());
        Assert.Equal(0x1000000UL, (ulong)ObjAttributes.LockedDown);
        Assert.Equal(0x2000000UL, (ulong)ObjAttributes.Secure);
        Assert.Equal(0x100000UL, (ulong)ObjAttributes.Insured);
        Assert.Equal(0x80000UL, (ulong)ObjAttributes.QuestItem);
        Assert.Equal(0x40000UL, (ulong)ObjAttributes.Imbued);
    }

    /// <summary>The attr_* names resolve too, including the Source-X spellings.</summary>
    [Fact]
    public void AttrNamesResolve()
    {
        var item = new Item();
        Assert.True(item.TrySetProperty("ATTR", "attr_nodrop|attr_notrade|attr_lockeddown"));
        Assert.True(item.IsAttr(ObjAttributes.NoDrop));
        Assert.True(item.IsAttr(ObjAttributes.NoTrade));
        Assert.True(item.IsAttr(ObjAttributes.LockedDown));
        Assert.False(item.IsAttr(ObjAttributes.Insured));
    }

    /// <summary>The newbie line's argument is DEFMSG tooltip_tag_newbie. A pack that
    /// wrote it as overhead speech ("@,,1,1 [Newbified]") gets the text without the
    /// speech prefix, which a tooltip cannot use.</summary>
    [Fact]
    public void NewbieLineCarriesTheDefMessageWithoutASpeechPrefix()
    {
        try
        {
            ServerMessages.SetOverride(Msg.TooltipTagNewbie, "@,,1,1 [Newbified]");
            var props = Build(i => i.SetAttr(ObjAttributes.Newbie));
            Assert.Contains((1070722u, "[Newbified]"), props);

            ServerMessages.SetOverride(Msg.TooltipTagNewbie, "Insured-ish");
            props = Build(i => i.SetAttr(ObjAttributes.Newbie));
            Assert.Contains((1070722u, "Insured-ish"), props);
        }
        finally
        {
            ServerMessages.SetOverride(Msg.TooltipTagNewbie, "Newbie");
        }
    }

    /// <summary>Elves Only / Gargoyles Only when CANUSE allows that race alone; no line
    /// when another race is allowed too.</summary>
    [Theory]
    [InlineData("08", 1154650u)]          // elf
    [InlineData("010", 1111709u)]         // gargoyle
    [InlineData("0c", 0u)]                // human + elf
    [InlineData("018", 0u)]               // elf + gargoyle
    [InlineData("0", 0u)]
    public void RaceOnlyLineFollowsCanUse(string canUse, uint expected)
    {
        var props = Build(_ => { }, ItemType.Normal, "CANUSE=" + canUse);
        bool elf = props.Any(p => p.Cliloc == 1154650);
        bool garg = props.Any(p => p.Cliloc == 1111709);
        Assert.Equal(expected == 1154650u, elf);
        Assert.Equal(expected == 1111709u, garg);
    }

    /// <summary>Changing an attribute flags the tooltip for a rebuild (upstream's item
    /// property writes end in UpdatePropertyFlag); setting a bit already set does not.</summary>
    [Fact]
    public void ChangingAnAttributeFlagsTheTooltip()
    {
        var item = new Item();
        item.ConsumeDirty();

        item.SetAttr(ObjAttributes.Blessed);
        Assert.True((item.ConsumeDirty() & DirtyFlag.Properties) != 0);

        item.SetAttr(ObjAttributes.Blessed);
        Assert.Equal(DirtyFlag.None, item.ConsumeDirty() & DirtyFlag.Properties);

        item.ClearAttr(ObjAttributes.Blessed);
        Assert.True((item.ConsumeDirty() & DirtyFlag.Properties) != 0);

        Assert.True(item.TrySetProperty("ATTR", "0400"));
        Assert.True((item.ConsumeDirty() & DirtyFlag.Properties) != 0);
    }
}
