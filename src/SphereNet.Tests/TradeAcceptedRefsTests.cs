using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;
using GameTriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// @TradeAccepted names the goods, not just how many (port plan İŞ-18 / PLAN-401).
///
/// Source-X fills the trigger args' object list with the items the receiving side is
/// about to get (CItemContainer::Trade_Status, CItemContainer.cpp:196 -
/// <c>m_VarObjs.Insert(i, pItem)</c>) and sets ARGN1 to their number. Scripts read
/// that list as REF1..REFn (CScriptTriggerArgs::r_GetRef, :189). SphereNet passed the
/// COUNT and nothing else, which makes the trigger useless to any script that has to
/// know WHAT changed hands - the reference pack's house transfer walks exactly this
/// list to find the deed:
///
///     ON=@TradeAccepted
///     for &lt;ARGN1&gt;
///         if (&lt;REF&lt;dLOCAL._FOR&gt;.TYPE&gt;==t_trade_house_deed)
///             UID.&lt;REF&lt;dLOCAL._FOR&gt;&gt;.TRIGGER @HouseTraded
///
/// (Scripts-X-main/housing/house_typedefs.scp:516). With no refs the loop reads zero
/// every time and a traded house never changes hands.
/// </summary>
public sealed class TradeAcceptedRefsTests : IDisposable
{
    private readonly string _scriptPath;

    public TradeAcceptedRefsTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_tradref_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, """
            [EVENTS e_trade_probe]
            ON=@TradeAccepted
            TAG.SEEN_COUNT=<ARGN1>
            TAG.SEEN_GIVEN=<ARGN2>
            TAG.SEEN_R1=<REF1>
            TAG.SEEN_R2=<REF2>
            TAG.SEEN_R3=<REF3>
            RETURN 0
            """);
    }

    public void Dispose() => File.Delete(_scriptPath);

    private static (GameWorld World, GameClient Client, Character Me, Character Partner, TradeManager Trades)
        Setup(TriggerDispatcher dispatcher, int id)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        typeof(GameClient)
            .GetField("_triggerDispatcher", System.Reflection.BindingFlags.Instance |
                                            System.Reflection.BindingFlags.NonPublic)!
            .SetValue(client, dispatcher);

        var me = MakePlayer(world, 100);
        var partner = MakePlayer(world, 101);
        partner.IsOnline = true;

        TestHarness.AttachCharacter(client, me);
        var trades = new TradeManager();
        TestHarness.SetPrivateField(client, "_tradeManager", trades);
        return (world, client, me, partner, trades);
    }

    private static Character MakePlayer(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.Player;
        ch.Str = 100; ch.MaxHits = ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        ch.Backpack = pack; ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private (TriggerDispatcher Dispatcher, ScriptInterpreter Interpreter) BuildDispatcher()
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(_scriptPath);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        return (new TriggerDispatcher { Resources = resources, Runner = runner }, interpreter);
    }

    [Fact]
    public void AcceptingATradeNamesEveryIncomingItemAsARef()
    {
        var (dispatcher, _) = BuildDispatcher();
        var (world, client, me, partner, trades) = Setup(dispatcher, 8301);
        me.Events.Add(ResourceId.FromEventName("e_trade_probe"));

        Assert.True(client.InitiateTrade(partner));
        var trade = trades.FindTradeFor(me)!;

        // Two items coming my way, one going the other.
        var incoming1 = world.CreateItem(); incoming1.BaseId = 0x0F51; incoming1.Name = "Deed";
        var incoming2 = world.CreateItem(); incoming2.BaseId = 0x0F51; incoming2.Name = "Key";
        Assert.True(trade.PartnerContainer.TryAddItem(incoming1));
        Assert.True(trade.PartnerContainer.TryAddItem(incoming2));

        var outgoing = world.CreateItem(); outgoing.BaseId = 0x0F51; outgoing.Name = "Gold bar";
        Assert.True(trade.InitiatorContainer.TryAddItem(outgoing));

        trade.SetAccept(partner, true);
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 1);

        Assert.True(me.TryGetTag("SEEN_COUNT", out string? count));
        Assert.Equal("2", count);
        Assert.True(me.TryGetTag("SEEN_GIVEN", out string? given));
        Assert.Equal("1", given);

        // REF1..REFn are the incoming items in container order; the slot past the
        // end stays empty, the way an unset REF reads.
        Assert.True(me.TryGetTag("SEEN_R1", out string? r1));
        Assert.True(me.TryGetTag("SEEN_R2", out string? r2));
        Assert.True(me.TryGetTag("SEEN_R3", out string? r3));
        Assert.Equal(incoming1.Uid.Value, ParseUid(r1!));
        Assert.Equal(incoming2.Uid.Value, ParseUid(r2!));
        Assert.Equal("0", r3);
    }

    [Fact]
    public void AnEmptyOfferNamesNoRefs()
    {
        var (dispatcher, _) = BuildDispatcher();
        var (_, client, me, partner, trades) = Setup(dispatcher, 8302);
        me.Events.Add(ResourceId.FromEventName("e_trade_probe"));

        Assert.True(client.InitiateTrade(partner));
        var trade = trades.FindTradeFor(me)!;

        trade.SetAccept(partner, true);
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 1);

        Assert.True(me.TryGetTag("SEEN_COUNT", out string? count));
        Assert.Equal("0", count);
        Assert.True(me.TryGetTag("SEEN_R1", out string? r1));
        Assert.Equal("0", r1);
    }

    [Fact]
    public void TheRefsBelongToTheSideThatRECEIVESThem()
    {
        // Each side is told what IT gets - Source-X gives pChar1 the list from
        // pPartner's window, not from its own (CItemContainer.cpp:193/201).
        var (dispatcher, _) = BuildDispatcher();
        var (world, client, me, partner, trades) = Setup(dispatcher, 8303);
        partner.Events.Add(ResourceId.FromEventName("e_trade_probe"));

        Assert.True(client.InitiateTrade(partner));
        var trade = trades.FindTradeFor(me)!;

        var mine = world.CreateItem(); mine.BaseId = 0x0F51; mine.Name = "My offer";
        Assert.True(trade.InitiatorContainer.TryAddItem(mine));

        trade.SetAccept(partner, true);
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 1);

        Assert.True(partner.TryGetTag("SEEN_COUNT", out string? count));
        Assert.Equal("1", count);
        Assert.True(partner.TryGetTag("SEEN_R1", out string? r1));
        Assert.Equal(mine.Uid.Value, ParseUid(r1!));
    }

    [Fact]
    public void ARefTheScriptSetsItselfSurvivesTheRestOfTheChain()
    {
        // The map is shared, not copied per block (Source-X keeps m_VarObjs on the
        // args), so a script that parks an object in a free slot can read it back.
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_refchain_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, """
            [EVENTS e_ref_writer]
            ON=@TradeAccepted
            REF9=<REF1>
            RETURN 0
            """);
        try
        {
            resources.LoadResourceFile(tmp);
            var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
            var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
            var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

            var ch = new Character();
            ch.Events.Add(ResourceId.FromEventName("e_ref_writer"));

            var refs = new System.Collections.Generic.Dictionary<int, string> { [1] = "04001234" };
            dispatcher.FireCharTrigger(ch, CharTrigger.TradeAccepted,
                new GameTriggerArgs { CharSrc = ch, N1 = 1, Refs = refs });

            Assert.True(refs.ContainsKey(9));
            Assert.Equal(0x4001234u, ParseUid(refs[9]));
        }
        finally { File.Delete(tmp); }
    }

    private static uint ParseUid(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.Length > 1 && s[0] == '0') s = s[1..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : 0;
    }
}
