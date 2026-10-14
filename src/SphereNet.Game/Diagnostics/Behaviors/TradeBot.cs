namespace SphereNet.Game.Diagnostics.Behaviors;

/// <summary>
/// Trades with other bots: walks up to a bot, takes one item from its own backpack and
/// drops it on that bot, which opens a secure trade window on both. Every bot accepts a
/// trade window shortly after it opens (BotClient), so the trade completes and the item
/// changes hands - exercising the trade containers, their cleanup and the item moves a
/// save has to get right.
/// </summary>
public sealed class TradeBot : IBotBehavior
{
    public string Name => "Trade";

    private readonly Random _rng;

    public TradeBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        BotEngine.RegisterTrader(bot);
        try { await TradeLoopAsync(bot, actions, ct); }
        finally { BotEngine.ForgetTrader(bot); }
    }

    private async Task TradeLoopAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            var world = bot.World;
            // Another trader, located where the server last put it: traders head for
            // each other rather than waiting to meet by chance.
            var other = BotEngine.NearestTrader(bot);
            if (other == null || world.BackpackSerial == 0)
            {
                BotEngine.CountTradeSkip(other == null ? "partner" : "backpack");
                await actions.MoveDirection((byte)_rng.Next(0, 8), ct);
                await Task.Delay(_rng.Next(300, 600), ct);
                continue;
            }

            if (world.DistanceTo(other.World.X, other.World.Y) > 1)
                await actions.MoveTo(other.World.X, other.World.Y, 8000, ct);

            var partner = world.FindNearest(m => m.Serial == other.World.CharUid, maxRange: 2);
            if (partner == null)
            {
                BotEngine.CountTradeSkip("far");
                await Task.Delay(500, ct);
                continue;
            }

            // Either side already in a trade: the server refuses a second one.
            if (world.TradeContainer != 0 || other.World.TradeContainer != 0)
            {
                BotEngine.CountTradeSkip("busy");
                await Task.Delay(1000, ct);
                continue;
            }

            await actions.OpenContainer(world.BackpackSerial, 2000, ct);
            await Task.Delay(300, ct);

            BotKnownItem? offer = null;
            if (world.OpenContainers.TryGetValue(world.BackpackSerial, out var pack))
                offer = pack.Items.FirstOrDefault(i => i.Amount > 0);

            if (offer == null)
                BotEngine.CountTradeSkip("item");
            else if (world.DistanceTo(partner.X, partner.Y) > 2)
                BotEngine.CountTradeSkip("far");
            else
            {
                long offeredMs = Environment.TickCount64;
                var picked = await actions.PickUp(offer.Serial, 1, 2000, ct);
                await Task.Delay(200, ct);
                await actions.DropToContainer(offer.Serial, partner.Serial, 2000, ct);
                BotEngine.CountTradeOffered();

                await Task.Delay(2000, ct);
                string? refused = null;
                lock (world.SyncRoot)
                {
                    if (world.TradeOpenedMs < offeredMs)
                        refused = world.LastSystemMessage(offeredMs)
                            ?? (picked == BotActionResult.Rejected
                                ? $"pickup rejected ({world.LastMoveItemRejectReason}) 0x{offer.ItemId:X4}"
                                : "no message");
                }
                if (refused != null)
                    BotEngine.CountTradeOutcome("no window: " + refused);
            }

            await Task.Delay(_rng.Next(5000, 10000), ct);
        }
    }
}
