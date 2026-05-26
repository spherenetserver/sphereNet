namespace SphereNet.Game.Diagnostics.Behaviors;

public sealed class VendorBot : IBotBehavior
{
    public string Name => "Vendor";

    private readonly Random _rng;

    public VendorBot(int seed) => _rng = new Random(seed);

    public async Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && bot.State == BotState.Playing)
        {
            var world = bot.World;

            var vendor = world.FindNearest(m => m.IsHumanBody && m.Notoriety == 3, 18);
            if (vendor != null)
            {
                int dist = world.DistanceTo(vendor.X, vendor.Y);
                if (dist > 2)
                    await actions.MoveTo(vendor.X, vendor.Y, 5000, ct);

                await actions.DoubleClick(vendor.Serial, 2000, ct);
                await Task.Delay(_rng.Next(500, 1000), ct);

                if (world.ActiveVendor != null)
                {
                    if (world.ActiveVendor.IsBuyList && world.ActiveVendor.Items.Count > 0)
                    {
                        var item = world.ActiveVendor.Items[_rng.Next(world.ActiveVendor.Items.Count)];
                        await actions.Buy(world.ActiveVendor.VendorSerial, item.Serial, 1, ct);
                    }
                    world.ActiveVendor = null;
                }

                await Task.Delay(_rng.Next(1000, 3000), ct);
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
