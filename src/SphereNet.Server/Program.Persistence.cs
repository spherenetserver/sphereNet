using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static void SaveAccountsToDisk()
    {
        try
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string accDir = ResolvePath(basePath, _config.AccountDir);
            SphereNet.Persistence.Accounts.AccountPersistence.Save(
                _accounts, accDir, _saver.Format,
                _loggerFactory.CreateLogger("AccountPersistence"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Account save failed");
        }
    }

    private static void RequestSaveOnMainLoop()
    {
        _mainLoopActions.Enqueue(PerformSave);
    }

    private static void RequestSaveFormatChangeOnMainLoop(string fmtName, int shards)
    {
        _mainLoopActions.Enqueue(() => HandleSaveFormatChange(fmtName, shards));
    }

    private static void PerformSave()
    {
        // Source-X DEFMSG_WORLDSAVE_S behaviour: tell every online player a
        // save is happening so they don't blame momentary lag on the server
        // crashing. We use the world-event hue (0x0040, light red) which
        // matches the colour OSI/Source-X uses for global system events.
        const ushort SaveHue = 0x0040;
        BroadcastToAllPlayers(ServerMessages.Get("worldsave_started"), SaveHue);

        _log.LogInformation("Saving world...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _systemHooks.DispatchServer("save", _serverHookContext);
            _housingEngine?.SerializeAllToTags();
            _shipEngine?.SerializeAllToTags();
            _guildManager?.SerializeAllToTags(_world);
            _spellEngine.RevertAllForSave();
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string sp = ResolvePath(basePath, _config.WorldSaveDir);
            _saver.Save(_world, sp);
            _spellEngine.ReapplyAllAfterSave();
            SaveAccountsToDisk();
            _saveCount++;
            sw.Stop();
            double secs = sw.Elapsed.TotalSeconds;
            _log.LogInformation("Save complete. ({Secs:F2} sec)", secs);
            BroadcastToAllPlayers(
                ServerMessages.GetFormatted("worldsave_complete", _saveCount, $"{secs:F2}"),
                SaveHue);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "World save failed");
            BroadcastToAllPlayers(
                ServerMessages.GetFormatted("worldsave_failed", ex.Message),
                SaveHue);
        }
    }

    /// <summary>Send a sysmessage to every logged-in player. Used for global
    /// events (world save start/complete, shutdown countdown, etc.) where
    /// Source-X uses g_World.Broadcast() / addBarkParse(...,
    /// CCharBase::ALLCHARS, ...).</summary>
    private static void BroadcastToAllPlayers(string text, ushort hue)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var c in _clients.Values)
        {
            if (!c.IsPlaying)
                continue;
            try
            {
                c.SysMessage(text, hue);
            }
            catch
            {
                // Don't let a single dead socket abort the broadcast — a
                // disconnected client during save is normal at server tick
                // boundaries; the connection will be reaped shortly.
            }
        }
    }

    /// <summary>Handle a <c>.SAVEFORMAT</c> request: parse format name, update
    /// the saver, then immediately persist so the user can confirm the new
    /// files land on disk. Invalid format strings are rejected without any
    /// state change so a typo can't nuke the save path.</summary>
    private static void HandleSaveFormatChange(string fmtName, int shards)
    {
        if (!Enum.TryParse<SphereNet.Core.Configuration.SaveFormat>(fmtName, ignoreCase: true, out var fmt))
        {
            _log.LogWarning("SAVEFORMAT: unknown format '{Name}'. Valid: Text, TextGz, Binary, BinaryGz",
                fmtName);
            return;
        }
        _saver.Format = fmt;
        _config.SaveFormat = fmt;
        if (shards >= 1)
        {
            _saver.ShardCount = shards;
            _config.SaveShards = shards;
        }
        _log.LogInformation("SAVEFORMAT: switching to {Format} (shards={Shards}) and saving now",
            fmt, _saver.ShardCount);
        PerformSave();
    }
}
