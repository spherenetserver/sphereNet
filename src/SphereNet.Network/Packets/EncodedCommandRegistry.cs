namespace SphereNet.Network.Packets;

/// <summary>
/// Known 0xD7 "encoded command" subcommands. In the UO protocol these are
/// exclusively the custom-house (foundation) design-editor actions — a client
/// only sends them while in house-customization mode, which the server must
/// initiate. The 0xD7 subcommand space is SEPARATE from the 0xBF extended
/// command space (see <see cref="ExtendedCommandRegistry"/>), even though some
/// numeric IDs overlap (e.g. 0x06 is "Build" here but "party" under 0xBF), so
/// the two must never share a dispatch table.
///
/// Custom housing is not yet supported, so these are currently rejected/ignored
/// rather than acted upon. The list documents the protocol for the eventual
/// design-editor implementation.
/// </summary>
public static class EncodedCommandRegistry
{
    public const ushort Backup = 0x02;
    public const ushort Restore = 0x03;
    public const ushort Commit = 0x04;
    public const ushort Delete = 0x05;
    public const ushort Build = 0x06;
    public const ushort Action = 0x0A;
    public const ushort Close = 0x0C;
    public const ushort Stairs = 0x0D;
    public const ushort Sync = 0x0E;
    public const ushort Action2 = 0x0F;
    public const ushort Clear = 0x10;
    public const ushort Level = 0x12;
    public const ushort Roof = 0x13;
    public const ushort RoofDelete = 0x14;
    public const ushort Revert = 0x1A;

    private static readonly HashSet<ushort> s_customHouseDesign =
    [
        Backup, Restore, Commit, Delete, Build, Action, Close, Stairs,
        Sync, Action2, Clear, Level, Roof, RoofDelete, Revert,
    ];

    /// <summary>True if the subcommand is a custom-house design-editor action.</summary>
    public static bool IsCustomHouseDesign(ushort subCommand) => s_customHouseDesign.Contains(subCommand);
}
