namespace SphereNet.Network.State;

/// <summary>
/// Whether one packet belongs in the debug log.
///
/// This was decided in two places - the receive side in NetworkManager, with the
/// DebugPacketOpcodes whitelist, and the send side in NetState without it. So the
/// setting did half of what it says: a shard narrowing the log to a few opcodes still
/// paid for every outgoing packet, and its log still carried them.
///
/// That costs more than tidiness. Formatting a packet allocates a string per packet on
/// the thread that is about to send a movement acknowledgement, and the acknowledgements
/// are the packets a stutter is measured in - so a debug session could produce the very
/// delay it was opened to explain.
/// </summary>
internal static class PacketDebugFilter
{
    /// <summary>Ping (0x73) is skipped whatever the filter says: it is pure volume and
    /// carries nothing a reader wants.</summary>
    internal const byte PingOpcode = 0x73;

    internal static bool ShouldLog(bool debugPackets, HashSet<byte>? opcodeFilter, byte opcode)
    {
        if (!debugPackets)
            return false;
        if (opcode == PingOpcode)
            return false;
        // No whitelist means everything.
        return opcodeFilter == null || opcodeFilter.Count == 0 || opcodeFilter.Contains(opcode);
    }

    /// <summary>The second stage: whether this packet's CATEGORY is wanted.
    ///
    /// A shard with a busy street sends far more about its creatures and their gear
    /// than about the player watching them, and on a debug run that traffic is most of
    /// the log and most of its cost. Naming the categories to keep - player and packet,
    /// say - leaves the client's own conversation with the server readable and drops the
    /// rest before anything is classified further or formatted.
    ///
    /// Empty means all of them, so a shard that says nothing gets what it had.
    /// Categories are those the classifier produces: player, npc, item, packet, and
    /// mobile where no world is attached to tell npc from player.</summary>
    internal static bool ShouldLogCategory(HashSet<string>? categoryFilter, string category) =>
        categoryFilter == null || categoryFilter.Count == 0 || categoryFilter.Contains(category);
}
