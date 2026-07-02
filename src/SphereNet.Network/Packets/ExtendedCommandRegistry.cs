namespace SphereNet.Network.Packets;

/// <summary>
/// Single source of truth for known 0xBF extended subcommands.
/// Game handlers and packet routing should consult this list instead of
/// maintaining parallel switch tables.
/// </summary>
public static class ExtendedCommandRegistry
{
    private static readonly HashSet<ushort> s_knownSubCommands =
    [
        0x0005, // screen size
        0x0006, // party
        0x0007, // quest arrow click
        0x000B, // chat/language button path
        0x0013, // context menu request
        0x0015, // context menu response
        0x001A, // stat lock change
        0x001C, // client view size
        0x001E, // query custom house design details

        0x0024, // known ignored
        0x0028, // guild button
        0x002C, // virtue invoke
        0x0032, // quest button
        0x0033, // wheel-boat move (High Seas steering)
    ];

    public static IReadOnlyCollection<ushort> KnownSubCommands => s_knownSubCommands;

    public static bool IsKnown(ushort subCommand) => s_knownSubCommands.Contains(subCommand);
}
