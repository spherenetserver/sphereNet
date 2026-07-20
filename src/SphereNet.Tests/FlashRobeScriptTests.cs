using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report: the live pack's i_flash_robe (staff robe that cycles hues
/// on a 1 s TIMER, recoloring the wearer's whole gear and mount) does
/// nothing in game. The script exercises the classic Sphere item-timer
/// surface: TIMER verb on a WORN item, @Timer with DORAND + FOR +
/// TOPOBJ.FINDLAYER(n).COLOR chains, NAME &lt;COLOR&gt;, and TIMER -1 as
/// the off switch. The trigger bodies below are the live script verbatim
/// (minus the SYSMESSAGELOC cosmetics that need pack-wide defs).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class FlashRobeScriptTests
{
    private static (SphereNet.Game.Scripting.TriggerDispatcher dispatcher, GameWorld world) Setup(out int robeDefIndex)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_flashrobe_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, """
            [ITEMDEF 01f03]
            DEFNAME=i_robe

            [ITEMDEF I_FLASH_ROBE]
            DEFNAME=I_FLASH_ROBE
            ID=i_robe
            NAME=Flash Robe
            TYPE=T_CLOTHING

            ON=@CREATE
            COLOR=0

            ON=@EQUIP
            TIMER 1

            ON=@TIMER
            DORAND 14
            	COLOR <R1059,1101>
            	COLOR <R1150,1200>
            	COLOR <R1255,1306>
            	COLOR <R1355,1400>
            	COLOR <R1455,1500>
            	COLOR <R1555,1600>
            	COLOR <R1655,1701>
            	COLOR <R1755,1800>
            	COLOR <R1910,1999>
            	COLOR <R2020,2099>
            	COLOR <R2123,2199>
            	COLOR <R2226,2299>
            	COLOR <R2320,2398>
            	COLOR <R2432,3000>
            ENDDO
            NAME <COLOR>
            IF (<CONT>)
            TOPOBJ.FINDLAYER(25).COLOR <COLOR>
            	FOR 1 20
            		IF (<CONT.FINDLAYER(<DLOCAL._FOR>)>)
            		TOPOBJ.FINDLAYER(<DLOCAL._FOR>).COLOR <COLOR>
            		ENDIF
            	ENDFOR
            ELSE
            SAYU <COLOR>
            ENDIF
            topobj.sound 041,0,2
            TIMER 1
            RETURN 1
            """);

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(tmp);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);
        robeDefIndex = stack.Resources.ResolveDefName("I_FLASH_ROBE").Index;

        var world = TestHarness.CreateWorld();
        Item.CreateTriggerHook = it => stack.Dispatcher.FireItemTrigger(
            it, ItemTrigger.Create, new TriggerArgs { ItemSrc = it });
        Item.OnTimerExpired = it => stack.Dispatcher.FireItemTrigger(
            it, ItemTrigger.Timer, new TriggerArgs { ItemSrc = it });
        return (stack.Dispatcher, world);
    }

    [Fact]
    public void WornFlashRobe_TimerTick_RecolorsRobeGearAndMount()
    {
        var (dispatcher, world) = Setup(out int robeIdx);

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var tunic = world.CreateItem();
        tunic.BaseId = 0x1FA1;
        ch.Equip(tunic, (Layer)5);
        var mount = world.CreateItem();
        mount.BaseId = 0x3E9F;
        ch.Equip(mount, Layer.Horse);

        var robe = world.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(robe, robeIdx));
        ch.Equip(robe, Layer.Robe);
        dispatcher.FireItemTrigger(robe, ItemTrigger.Equip,
            new TriggerArgs { CharSrc = ch, ItemSrc = robe });

        // @EQUIP armed the 1 s timer on the WORN robe.
        Assert.True(robe.Timeout > 0, "@EQUIP did not arm the item TIMER");

        // Force the timer due and run the item tick — the worn robe must
        // tick (this is the live failure: nothing pumps off-ground timers).
        robe.SetTimeout(Environment.TickCount64 - 1);
        Assert.True(robe.OnTick());

        Assert.InRange((int)robe.Hue.Value, 1059, 3000); // DORAND hue landed
        Assert.Equal(robe.Hue.Value, tunic.Hue.Value);   // FOR 1 20 recolor
        Assert.Equal(robe.Hue.Value, mount.Hue.Value);   // FINDLAYER(25) mount
        Assert.NotEqual("Flash Robe", robe.Name);        // NAME <COLOR>
        Assert.True(robe.Timeout > Environment.TickCount64 - 50,
            "@Timer did not re-arm the loop");
        Assert.False(robe.IsDeleted);
    }

    [Fact]
    public void TimerVerb_MinusOne_DisablesTheTimer()
    {
        var (_, world) = Setup(out int robeIdx);
        var robe = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(robe, robeIdx);

        Assert.True(robe.TryExecuteCommand("TIMER", "5", null!));
        Assert.True(robe.Timeout > 0);

        // Sphere: TIMER -1 DISABLES the timer (the robe's dclick off switch)
        // — it must not fire "now".
        Assert.True(robe.TryExecuteCommand("TIMER", "-1", null!));
        Assert.Equal(0, robe.Timeout);
    }

    [Fact]
    public void OffGroundTimedItem_IsPumpedByTheWorldTick()
    {
        var (_, world) = Setup(out int robeIdx);

        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var robe = world.CreateItem();
        ItemDefHelper.ApplyInstanceMetadata(robe, robeIdx);
        ch.Equip(robe, Layer.Robe);

        // Arm and expire: the WORLD tick must reach a worn item's timer —
        // sector ticks only cover ground items, so without a dedicated pump
        // the flash robe never cycles (the live report).
        robe.SetTimeout(Environment.TickCount64 - 1);
        world.OnTick();

        Assert.InRange((int)robe.Hue.Value, 1059, 3000);
    }
}
