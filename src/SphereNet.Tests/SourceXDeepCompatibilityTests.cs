using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXDeepCompatibilityTests
{
    [Theory]
    [InlineData("TAG.COUNT*=2", "TAG.COUNT", "<EVAL <TAG.COUNT>*2>")]
    [InlineData("TAG.COUNT /= 4", "TAG.COUNT", "<EVAL <TAG.COUNT>/4>")]
    public void ScriptKey_ParsesSourceXCompoundAssignments(string line, string expectedKey, string expectedArg)
    {
        var key = new ScriptKey();

        key.Parse(line);

        Assert.Equal(expectedKey, key.Key);
        Assert.Equal(expectedArg, key.Arg);
    }

    [Fact]
    public void Character_DecimalSkillRangesUseTenthsScale()
    {
        var ch = new Character();

        Assert.True(ch.TrySetProperty("WRESTLING", "{15.0 38.0}"));

        Assert.InRange(ch.GetSkill(SkillType.Wrestling), (ushort)150, (ushort)380);
    }

    [Fact]
    public void Definitions_ResolveSymbolicFlagsLayersSoundsAliasesAndForwardItemRefs()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [DEFNAME sourcex_deep_constants]
            can_i_deep_probe=0100
            can_u_deep_probe=04
            layer_deep_probe=25
            snd_deep_probe=048d

            [ITEMDEF i_deep_forward]
            ID=i_deep_mid1
            CAN=can_i_deep_probe
            CANUSE=can_u_deep_probe
            LAYER=layer_deep_probe
            TDATA1=i_deep_mid1
            TDATA3=-1

            [ITEMDEF i_deep_mid1]
            ID=i_deep_mid2
            [ITEMDEF i_deep_mid2]
            ID=i_deep_mid3
            [ITEMDEF i_deep_mid3]
            ID=i_deep_mid4
            [ITEMDEF i_deep_mid4]
            ID=i_deep_mid5
            [ITEMDEF i_deep_mid5]
            ID=i_deep_target

            [ITEMDEF i_deep_target]
            ID=0eed

            [CHARDEF c_deep_metadata]
            DEFNAME2=c_deep_alias
            ID=0190
            SOUND=snd_deep_probe
            FOODTYPE=5 t_meat_raw, 2 t_fruit
            ERALIMITGEAR=3
            RESPHYSICAL=12
            RESPHYSICALMAX=75
            WRESTLING={15.0 38.0}
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        int itemIndex = stack.Resources.ResolveDefName("i_deep_forward").Index;
        var item = Assert.IsType<SphereNet.Scripting.Definitions.ItemDef>(DefinitionLoader.GetItemDef(itemIndex));
        Assert.Equal((ushort)0x0EED, item.DispIndex);
        Assert.Equal(CanFlags.I_Pile, item.Can);
        Assert.Equal(CanEquipFlags.Human, item.CanUse);
        Assert.Equal(Layer.Horse, item.Layer);
        Assert.Equal((uint)0x0EED, item.TData1);
        Assert.Equal(uint.MaxValue, item.TData3);

        var alias = stack.Resources.ResolveDefName("c_deep_alias");
        Assert.True(alias.IsValid);
        var character = Assert.IsType<SphereNet.Scripting.Definitions.CharDef>(DefinitionLoader.GetCharDef(alias.Index));
        Assert.Equal((ushort)0x048D, character.SoundIdle);
        Assert.Equal(ItemType.MeatRaw, character.FoodType);
        Assert.Equal(3, character.EraLimitGear);
        Assert.Equal((short)12, character.ResPhysical);
        Assert.Equal((short)75, character.ResPhysicalMax);
        Assert.Equal((150, 380), character.SkillRanges[SkillType.Wrestling]);
    }

    [Fact]
    public void ScriptFile_ReportsMalformedSectionHeaderWithLocation()
    {
        string path = WriteScript("[FUNCTION f_ok]\nRETURN 0\n[FUNCTION f_broken\nRETURN 1\n");
        using var file = new ScriptFile();
        string? diagnostic = null;
        file.Diagnostic = message => diagnostic = message;

        Assert.True(file.Open(path));
        var sections = file.ReadAllSections();

        Assert.NotNull(diagnostic);
        Assert.Contains(Path.GetFileName(path), diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":3:", diagnostic, StringComparison.Ordinal);
        Assert.Equal(2, sections.Count);
        Assert.Equal("FUNCTION", sections[1].Name);
        Assert.Equal("f_broken", sections[1].Argument);
    }

    [Fact]
    public void Character_SourceXRefreshAndSpellEffectVerbsReachEngineHooks()
    {
        var ch = new Character();
        bool notorietyUpdated = false;
        string? spellArgs = null;
        Character.NotoSaveUpdate = target => notorietyUpdated = target == ch;
        Character.OnScriptSpellEffect = (target, args, _) =>
        {
            if (target == ch) spellArgs = args;
        };

        try
        {
            Assert.True(ch.TryExecuteCommand("NOTOUPDATE", "", null!));
            Assert.True(ch.TryExecuteCommand("SPELLEFFECT", "s_fireball,750", null!));
            Assert.True(notorietyUpdated);
            Assert.Equal("s_fireball,750", spellArgs);
        }
        finally
        {
            Character.NotoSaveUpdate = null;
            Character.OnScriptSpellEffect = null;
        }
    }

    [Fact]
    public void Client_SourceXTooltipContextPromptAndFaceVerbsAreOperational()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [FUNCTION f_deep_prompt]
            TAG.PROMPT_RESULT=<ARGS>
            """);
        stack.Resources.LoadResourceFile(path);

        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(stack.LoggerFactory);
        var client = TestHarness.CreateClient(stack.LoggerFactory, world, accounts, 9921);
        Character player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x0190;
        TestHarness.AttachCharacter(client, player);
        client.SetEngines(triggerDispatcher: stack.Dispatcher);
        var context = (IClientContext)client;

        context.ScriptTooltipProperties = [];
        Assert.True(client.TryExecuteScriptCommand(player, "ADDCLILOC", "1042971,probe", null));
        Assert.Equal((1042971u, "probe"), Assert.Single(context.ScriptTooltipProperties));

        context.ScriptContextEntries = [];
        Assert.True(client.TryExecuteScriptCommand(player, "ADDCONTEXTENTRY", "200,3000001,2", null));
        Assert.Equal(((ushort)200, 3000001u, (ushort)2), Assert.Single(context.ScriptContextEntries));

        Assert.True(client.TryExecuteScriptCommand(player, "PROMPTCONSOLE", "f_deep_prompt,Enter value", null));
        client.HandlePromptResponse(player.Uid.Value, 1, 1, "source-x reply");
        Assert.True(player.TryGetTag("PROMPT_RESULT", out string? prompt));
        Assert.Equal("source-x reply", prompt);

        Assert.True(client.TryExecuteScriptCommand(player, "CHANGEFACE", "", null));
        client.HandleGumpResponse(player.Uid.Value, 0x2B0, 0x3B44, [], []);
        Assert.Equal((ushort)0x3B44, player.GetEquippedItem(Layer.Face)?.BaseId);
    }

    private static string WriteScript(string contents)
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-sourcex-deep-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".scp");
        File.WriteAllText(path, contents);
        return path;
    }
}
