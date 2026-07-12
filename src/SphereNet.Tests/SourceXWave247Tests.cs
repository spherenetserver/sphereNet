using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 247 — per-char regen rate overrides (Source-X CChar REGENHITS/MANA/STAM/FOOD,
/// resolved by CChar::Stats_GetRegenRate), plus old-style menu (0x7C) correctness:
/// TESTIF= expression gating and the MAX_MENU_ITEMS cap.
/// </summary>
public sealed class SourceXWave247Tests
{
    private static Character MakeWounded(ushort body)
    {
        var ch = new Character { BodyId = body, IsPlayer = true };
        ch.MaxHits = 100;
        ch.Hits = 50;
        ch.Food = 10;
        return ch;
    }

    private static string Prop(Character ch, string name)
    {
        ch.TryGetProperty(name, out string value);
        return value;
    }

    [Fact]
    public void RegenProperty_RoundTripsSecondsAndTenths()
    {
        var ch = new Character { BodyId = 0x0190 };

        // Non-D writes seconds; both variants read back the same stored ms field.
        ch.TrySetProperty("REGENHITS", "25");
        Assert.Equal(25_000, ch.RegenHitsRateMs);
        Assert.Equal("25", Prop(ch, "REGENHITS"));
        Assert.Equal("250", Prop(ch, "REGENHITSD"));

        // The D variant writes tenths into the same field.
        ch.TrySetProperty("REGENMANAD", "155");
        Assert.Equal(15_500, ch.RegenManaRateMs);
        Assert.Equal("15", Prop(ch, "REGENMANA"));   // 15500/1000 truncates
        Assert.Equal("155", Prop(ch, "REGENMANAD"));
    }

    [Fact]
    public void RegenProperty_UnsetReadsGlobalRate()
    {
        var ch = new Character { BodyId = 0x0190 };
        // No per-char override → reports the global rate (ResetEngineStatics defaults).
        Assert.Equal("40", Prop(ch, "REGENHITS"));
        Assert.Equal("20", Prop(ch, "REGENMANA"));
        Assert.Equal("10", Prop(ch, "REGENSTAM"));
    }

    [Fact]
    public void RegenRate_NegativeOverride_DisablesRegen()
    {
        // A fresh char's regen timer starts at 0, so the first OnTick normally regens.
        var control = MakeWounded(0x0190);
        control.OnTick();
        Assert.Equal(53, control.Hits); // base 1 + human racial 2

        var disabled = MakeWounded(0x0190);
        disabled.TrySetProperty("REGENHITS", "-1"); // < 0 = never regen this stat
        disabled.OnTick();
        Assert.Equal(50, disabled.Hits); // unchanged — regen suppressed
    }

    [Fact]
    public void MenuPacket_CapsItemsAtByteCount_NoStreamCorruption()
    {
        // 300 entries: the 1-byte count must advertise 255 AND the body must contain
        // exactly 255 items, or the client's read cursor desyncs on the overflow.
        var items = new List<MenuItemEntry>();
        for (int i = 0; i < 300; i++)
            items.Add(new MenuItemEntry((ushort)i, 0, "x"));

        var span = new PacketMenuDisplay(0x1234, 1, "t", items).Build().Span;

        int qLen = span[9];
        int count = span[10 + qLen];
        Assert.Equal(255, count);

        // id(1)+len(2)+serial(4)+menuId(2)+qLen(1)+title(1)+count(1)+255*6
        Assert.Equal(12 + 255 * 6, span.Length);
    }

    private const string TestIfMenu = """
        [SKILLMENU sm_wave247]
        Test

        ON=01 Alpha
        TESTIF=<STR> > 100

        ON=02 Beta
        TESTIF=<STR> < 100
        """;

    [Fact]
    public void SkillMenu_TestIf_HidesEntriesWithFalseExpression()
    {
        // Wire a real interpreter so TESTIF expressions actually evaluate.
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_w247_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "menu.scp");
            File.WriteAllText(file, TestIfMenu);
            stack.Resources.ScpBaseDir = dir;
            stack.Resources.LoadResourceFile(file);
            new DefinitionLoader(stack.Resources, new SpellRegistry()).LoadAll();

            var lf = LoggerFactory.Create(_ => { });
            var world = new GameWorld(lf);
            world.InitMap(0, 6144, 4096);
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(1000, 1000, 0, 0));
            player.TrySetProperty("STR", "200"); // Alpha (>100) shows, Beta (<100) hidden

            var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 247);
            TestHarness.AttachCharacter(client, player);
            client.SetEngines(triggerDispatcher: stack.Dispatcher);

            Assert.True(client.TryExecuteScriptCommand(player, "SKILLMENU", "sm_wave247", null));

            var packets = TestHarness.GetQueuedPackets(client.NetState);
            var menu = packets.FirstOrDefault(p => p.Span.Length > 0 && p.Span[0] == 0x7C);
            Assert.NotNull(menu);

            var span = menu!.Span;
            int qLen = span[9];
            int count = span[10 + qLen];
            Assert.Equal(1, count); // only the passing TESTIF entry survived
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
