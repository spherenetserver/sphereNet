namespace SphereNet.Game.Diagnostics.Behaviors;

public interface IBotBehavior
{
    string Name { get; }
    Task RunAsync(BotClient bot, BotActionApi actions, CancellationToken ct);
}
