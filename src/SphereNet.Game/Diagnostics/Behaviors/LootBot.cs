namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class LootBot : IBotBehavior
{
    public string Name => "Loot";

    private readonly Random _rng;

    public LootBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            var world = bot.World;

            BotKnownItem? corpse = null;
            foreach (var item in world.KnownItems.Values)
            {
                if (item.ItemId is >= 0x2006 and <= 0x2007)
                {
                    int d = world.DistanceTo(item.X, item.Y);
                    if (d <= 18) { corpse = item; break; }
                }
            }

            if (corpse != null)
            {
                int dist = world.DistanceTo(corpse.X, corpse.Y);
                if (dist > 2)
                    await actions.MoveTo(corpse.X, corpse.Y, 5000, ct);

                await actions.DoubleClick(corpse.Serial, 2000, ct);
                await Task.Delay(500, ct);

                if (world.OpenContainers.TryGetValue(corpse.Serial, out var container))
                {
                    foreach (var lootItem in container.Items)
                    {
                        if (ct.IsCancellationRequested) break;
                        await actions.PickUp(lootItem.Serial, lootItem.Amount, 2000, ct);
                        await Task.Delay(200, ct);

                        if (world.BackpackSerial != 0)
                            await actions.DropToContainer(lootItem.Serial, world.BackpackSerial, 2000, ct);

                        await Task.Delay(200, ct);
                    }
                }

                await Task.Delay(_rng.Next(500, 1500), ct);
            }
            else
            {
                byte dir = (byte)_rng.Next(0, 8);
                await actions.MoveDirection(dir, ct);
                await Task.Delay(_rng.Next(300, 600), ct);
            }
        }
    }
}
