using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class ItemTimerRegistrationIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameDeadlineRegisteredRepeatedlyFiresOnlyOncePerTick(bool parallel)
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        long due = Environment.TickCount64 - 1;
        for (int i = 0; i < 100; i++) item.SetTimeout(due);
        int calls = 0;
        Item.OnTimerExpired = current =>
        {
            calls++;
            // A TIMER=0-style rearm must wait for the next world tick.
            for (int i = 0; i < 3; i++) current.SetTimeout(due);
            return TriggerResult.True;
        };
        for (int tick = 1; tick <= 3; tick++)
        {
            if (parallel) world.OnTickParallel(); else world.OnTick();
            Assert.Equal(tick, calls);
        }
    }
}
