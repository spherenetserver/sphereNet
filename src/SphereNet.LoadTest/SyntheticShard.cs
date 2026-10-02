using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Save;

namespace SphereNet.LoadTest;

/// <summary>
/// A self-contained shard the load test can boot anywhere, CI included: no UO client
/// files, no script pack and no save are needed from outside.
///
/// - Client data: a flat 1792x1792 map0 (one passable land tile at z=10), empty
///   statics, an all-zero High Seas tiledata and an empty multi table. Enough for the
///   server's mandatory file checks and for walking; nothing else reads them here.
/// - Scripts: one file with the ITEMDEFs the ground items need; NPCs carry their body
///   and brain in the save itself.
/// - Accounts: one ordinary player account and character per bot, placed by the
///   generator, so the login is the normal existing-character path.
/// - World: a deterministic town (NPCs that wander, ground items) around the bots'
///   start tiles, written with the production WorldSaver.
///
/// Everything is a pure function of (npcs, items, bots, spread, seed), so two runs - and
/// two worker counts - start from the same world; <see cref="Layout.ScenarioHash"/>
/// proves it per run.
/// </summary>
public static class SyntheticShard
{
    public const int MapWidth = 1792;
    public const int MapHeight = 1792;
    public const sbyte GroundZ = 10;
    private const ushort LandTile = 0x0003; // grass in the client art; any id works here

    // The town: every sector a bot can start in, in either spread mode, plus a margin.
    public const int TownMinX = 1376, TownMaxX = 1564, TownMinY = 1496, TownMaxY = 1784;

    private static readonly ushort[] NpcBodies = [0x00C9, 0x00D9, 0x00CD, 0x00E1, 0x00DD, 0x00E2, 0x0019, 0x0022];
    private static readonly ushort[] ItemIds = [0x0EED, 0x0F3F, 0x1BFB, 0x0F7D, 0x0E21, 0x1BF5, 0x1776, 0x0F85, 0x097B, 0x0DCA];

    public sealed record Layout(string Root, string MulDir, string ScriptDir, string SaveDir,
        string AccountDir, string LogDir, string IniPath)
    {
        /// <summary>SHA-256 over every generated object (uid, kind, graphic, position,
        /// amount, brain, account): the same value for every run of one scenario, so the
        /// runs being compared provably started from the same world.</summary>
        public string ScenarioHash { get; set; } = "";
    }

    public static Layout Create(string root, int npcs, int items, int bots, string spread, int seed)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var layout = new Layout(
            root,
            Path.Combine(root, "mul"),
            Path.Combine(root, "scripts"),
            Path.Combine(root, "save"),
            Path.Combine(root, "accounts"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "sphere.ini"));
        foreach (var dir in new[] { layout.MulDir, layout.ScriptDir, layout.SaveDir, layout.AccountDir, layout.LogDir })
            Directory.CreateDirectory(dir);

        WriteClientFiles(layout.MulDir);
        WriteScripts(layout.ScriptDir);
        layout.ScenarioHash = WriteWorld(layout.SaveDir, layout.AccountDir, npcs, items, bots, spread, seed);
        return layout;
    }

    public static void WriteIni(Layout layout, int port, int workerCount)
    {
        static string Dir(string p) => p.Replace('\\', '/').TrimEnd('/') + "/";
        var sb = new StringBuilder();
        sb.AppendLine("[SPHERE]");
        sb.AppendLine("ServName=LoadTest");
        sb.AppendLine("ServIP=127.0.0.1");
        sb.AppendLine($"ServPort={port}");
        sb.AppendLine($"AdminPanelPort={port + 3}");
        sb.AppendLine("UseCrypt=0");
        sb.AppendLine("UseNoCrypt=1");
        sb.AppendLine("UseHttp=0");
        sb.AppendLine("ClientMax=2000");
        sb.AppendLine("ClientMaxIP=2000");
        sb.AppendLine("ConnectingMax=2000");
        sb.AppendLine("ConnectingMaxIP=2000");
        sb.AppendLine("AccApp=0");
        sb.AppendLine("Md5Passwords=1"); // the generator's AccountManager stores MD5
        sb.AppendLine("ArriveDepartMsg=0");
        sb.AppendLine($"ScpFiles={Dir(layout.ScriptDir)}");
        sb.AppendLine($"WorldSave={Dir(layout.SaveDir)}");
        sb.AppendLine($"AcctFiles={Dir(layout.AccountDir)}");
        sb.AppendLine($"MulFiles={Dir(layout.MulDir)}");
        sb.AppendLine($"Log={Dir(layout.LogDir)}");
        sb.AppendLine($"Map0={MapWidth},{MapHeight},64,0,0");
        // Saves happen only when the runner asks for one, mid-window.
        sb.AppendLine("SavePeriod=0");
        sb.AppendLine("SaveOnShutdown=0");
        sb.AppendLine("SaveBackground=1");
        sb.AppendLine("BackupLevels=0");
        sb.AppendLine($"MulticoreWorkerCount={workerCount}");
        sb.AppendLine("LogFileLevel=Warning");
        File.WriteAllText(layout.IniPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteClientFiles(string mulDir)
    {
        // map0.mul: blocks of 4-byte header + 64 cells (tile:2, z:1), column-major.
        int blocks = (MapWidth / 8) * (MapHeight / 8);
        var block = new byte[196];
        for (int c = 0; c < 64; c++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4 + c * 3), LandTile);
            block[4 + c * 3 + 2] = unchecked((byte)GroundZ);
        }
        using (var map = new FileStream(Path.Combine(mulDir, "map0.mul"), FileMode.Create, FileAccess.Write))
        {
            for (int i = 0; i < blocks; i++)
                map.Write(block);
        }

        // staidx0.mul: every block "no statics" (offset -1). statics0.mul cannot be
        // empty (it is memory mapped), so it carries one unused record.
        var idx = new byte[blocks * 12];
        Array.Fill(idx, (byte)0xFF);
        File.WriteAllBytes(Path.Combine(mulDir, "staidx0.mul"), idx);
        File.WriteAllBytes(Path.Combine(mulDir, "statics0.mul"), new byte[7]);

        // tiledata.mul, High Seas layout: 512 land groups of 4 + 32*30 bytes, then
        // 2048 item groups of 4 + 32*41 bytes. All flags clear: every land tile and
        // item is passable and nothing is a surface or a wall.
        long size = 512L * (4 + 32 * 30) + 2048L * (4 + 32 * 41);
        File.WriteAllBytes(Path.Combine(mulDir, "tiledata.mul"), new byte[size]);

        // multi.idx with one empty entry and a placeholder multi.mul.
        var multiIdx = new byte[12];
        Array.Fill(multiIdx, (byte)0xFF);
        File.WriteAllBytes(Path.Combine(mulDir, "multi.idx"), multiIdx);
        File.WriteAllBytes(Path.Combine(mulDir, "multi.mul"), new byte[12]);
    }

    private static void WriteScripts(string scriptDir)
    {
        // Only what the saved world needs: an ITEMDEF for every ground item graphic
        // (the loader refuses an item whose definition is missing). NPCs carry their
        // body and brain in the save itself.
        var sb = new StringBuilder();
        sb.Append("// Synthetic load-test pack.\r\n\r\n");
        foreach (ushort id in ItemIds)
            sb.Append($"[ITEMDEF 0{id:x}]\r\nNAME=load item {id:x}\r\n\r\n");
        sb.Append("[EOF]\r\n");
        File.WriteAllText(Path.Combine(scriptDir, "loadtest.scp"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string WriteWorld(string saveDir, string accountDir, int npcCount, int itemCount,
        int botCount, string spread, int seed)
    {
        using var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, MapWidth, MapHeight);
        var rng = new Random(seed);

        for (int i = 0; i < npcCount; i++)
        {
            var npc = world.CreateCharacter();
            npc.Name = $"town creature {i}";
            npc.BodyId = NpcBodies[i % NpcBodies.Length];
            npc.IsPlayer = false;
            // Mostly animals (wander), a share of humans (speech listeners).
            npc.NpcBrain = i % 5 == 0 ? NpcBrainType.Human : NpcBrainType.Animal;
            npc.Str = 40; npc.Dex = 40; npc.Int = 20;
            npc.MaxHits = 40; npc.Hits = 40;
            npc.MaxStam = 40; npc.Stam = 40;
            npc.MaxMana = 20; npc.Mana = 20;
            var pos = new Point3D((short)rng.Next(TownMinX, TownMaxX + 1),
                (short)rng.Next(TownMinY, TownMaxY + 1), GroundZ, 0);
            npc.Home = pos;
            npc.HomeDist = 12;
            world.PlaceCharacter(npc, pos);
        }

        for (int i = 0; i < itemCount; i++)
        {
            var item = world.CreateItem();
            item.BaseId = ItemIds[i % ItemIds.Length];
            item.Amount = (ushort)(1 + rng.Next(20));
            world.PlaceItem(item, new Point3D((short)rng.Next(TownMinX, TownMaxX + 1),
                (short)rng.Next(TownMinY, TownMaxY + 1), GroundZ, 0));
        }

        // The bots' own accounts and characters, ordinary players: the login is the
        // normal "existing character in slot 0" path and nothing about them is
        // special-cased by the server.
        var accounts = new AccountManager(lf);
        var botPositions = BotPositions(botCount, spread, seed);
        for (int i = 0; i < botCount; i++)
        {
            int id = i + 1;
            if (accounts.CreateAccount(AccountName(id), Password) == null)
                throw new InvalidOperationException($"could not create account {AccountName(id)}");
            var ch = world.CreateCharacter();
            ch.Name = CharName(id);
            ch.IsPlayer = true;
            ch.BodyId = 0x0190;
            ch.Str = 60; ch.Dex = 60; ch.Int = 40;
            ch.MaxHits = 60; ch.Hits = 60;
            ch.MaxStam = 60; ch.Stam = 60;
            ch.MaxMana = 40; ch.Mana = 40;
            ch.SetTag("ACCOUNT", AccountName(id));
            world.PlaceCharacter(ch, botPositions[i]);
        }

        var content = new StringBuilder();
        foreach (var obj in world.GetAllObjects().OrderBy(o => o.Uid.Value))
        {
            content.Append(obj.Uid.Value).Append(' ').Append(obj.BaseId).Append(' ')
                .Append(obj.X).Append(',').Append(obj.Y).Append(',').Append(obj.Z);
            if (obj is SphereNet.Game.Objects.Items.Item it) content.Append(" i").Append(it.Amount);
            if (obj is SphereNet.Game.Objects.Characters.Character c)
                content.Append(" c").Append(c.BodyId).Append(' ').Append((int)c.NpcBrain)
                    .Append(' ').Append(c.Tags.Get("ACCOUNT"));
            content.Append('\n');
        }
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(content.ToString())));

        var saver = new WorldSaver(lf) { Format = SaveFormat.Text, BackupLevels = 0 };
        if (!saver.Save(world, saveDir))
            throw new InvalidOperationException("Synthetic world save failed.");
        AccountPersistence.Save(accounts, accountDir, SaveFormat.Text, generation: saver.LastGeneration);
        return hash;
    }

    public const string Password = "loadpass";
    public static string AccountName(int id) => $"loadbot{id:D4}";
    public static string CharName(int id) => $"LoadBot{id}";

    /// <summary>Distinct start tiles. "cluster": a square around the town centre
    /// sized for roughly one bot per four tiles - a crowd where most bots see each
    /// other. "town": spread over the whole spawn box.</summary>
    private static Point3D[] BotPositions(int count, string spread, int seed)
    {
        var rng = new Random(seed ^ 0x5EED);
        int minX, maxX, minY, maxY;
        if (spread == "town")
        {
            (minX, maxX, minY, maxY) = (1400, 1540, 1520, 1760);
        }
        else
        {
            int radius = Math.Max(6, (int)Math.Ceiling(Math.Sqrt(4.0 * count) / 2));
            (minX, maxX, minY, maxY) = (CenterX - radius, CenterX + radius, CenterY - radius, CenterY + radius);
        }
        var used = new HashSet<(int, int)>();
        var result = new Point3D[count];
        for (int i = 0; i < count; i++)
        {
            int x, y;
            do
            {
                x = rng.Next(minX, maxX + 1);
                y = rng.Next(minY, maxY + 1);
            } while (!used.Add((x, y)));
            result[i] = new Point3D((short)x, (short)y, GroundZ, 0);
        }
        return result;
    }

    public const int CenterX = 1470, CenterY = 1640;
}
