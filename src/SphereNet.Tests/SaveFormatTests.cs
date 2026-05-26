using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

public class SaveFormatTests
{
    private static (WorldSaver saver, WorldLoader loader) MakeIO()
    {
        var lf = LoggerFactory.Create(b => { });
        return (new WorldSaver(lf), new WorldLoader(lf));
    }

    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(b => { });
        var w = new GameWorld(lf);
        w.InitMap(0, 6144, 4096);
        return w;
    }

    private static (List<Character> chars, List<Item> items) Seed(GameWorld world, int charCount, int itemCount)
    {
        var chars = new List<Character>(charCount);
        var items = new List<Item>(itemCount);
        for (int i = 0; i < charCount; i++)
        {
            var c = world.CreateCharacter();
            c.Name = $"Ch{i}";
            c.BodyId = 0x0190;
            c.Str = (short)(50 + i);
            c.Dex = 50; c.Int = 50;
            c.MaxHits = 100; c.Hits = 100;
            c.IsPlayer = i == 0;
            world.PlaceCharacter(c, new Point3D((short)(1000 + i), 1000, 0, 0));
            chars.Add(c);
        }
        for (int i = 0; i < itemCount; i++)
        {
            var it = world.CreateItem();
            it.BaseId = (ushort)(0x0EED + (i % 3));
            it.Amount = (ushort)(1 + i);
            it.SetTag("TESTTAG", $"val{i}");
            world.PlaceItem(it, new Point3D((short)(2000 + i), 2000, 0, 0));
            items.Add(it);
        }
        return (chars, items);
    }

    [Theory]
    [InlineData(SaveFormat.Text, 0)]
    [InlineData(SaveFormat.TextGz, 0)]
    [InlineData(SaveFormat.Binary, 0)]
    [InlineData(SaveFormat.BinaryGz, 0)]
    [InlineData(SaveFormat.Text, 4)]
    [InlineData(SaveFormat.Binary, 4)]
    [InlineData(SaveFormat.BinaryGz, 8)]
    public void Roundtrip_PreservesEntityCountsAndKeyFields(SaveFormat fmt, int shards)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_save_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = shards;

            var src = MakeWorld();
            var (srcChars, srcItems) = Seed(src, charCount: 7, itemCount: 11);
            Assert.True(saver.Save(src, tmp));

            // Load into a fresh world and compare.
            var dst = MakeWorld();
            var (itemCount, charCount) = loader.Load(dst, tmp);
            Assert.Equal(srcItems.Count, itemCount);
            Assert.Equal(srcChars.Count, charCount);

            // Roundtrip key fields on one sample entity.
            var sampleItem = srcItems[0];
            var reloadedItem = dst.FindItem(sampleItem.Uid);
            Assert.NotNull(reloadedItem);
            Assert.Equal(sampleItem.BaseId, reloadedItem!.BaseId);
            Assert.Equal(sampleItem.Amount, reloadedItem.Amount);
            Assert.True(reloadedItem.TryGetTag("TESTTAG", out var tagVal));
            Assert.Equal("val0", tagVal);

            var sampleChar = srcChars[^1];
            var reloadedChar = dst.FindChar(sampleChar.Uid);
            Assert.NotNull(reloadedChar);
            Assert.Equal(sampleChar.Name, reloadedChar!.Name);
            Assert.Equal(sampleChar.Str, reloadedChar.Str);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FormatDetection_RoundtripsViaExtension()
    {
        Assert.Equal(SaveFormat.Text, SaveIO.FormatFromPath("foo.scp"));
        Assert.Equal(SaveFormat.TextGz, SaveIO.FormatFromPath("foo.scp.gz"));
        Assert.Equal(SaveFormat.Binary, SaveIO.FormatFromPath("foo.sbin"));
        Assert.Equal(SaveFormat.BinaryGz, SaveIO.FormatFromPath("foo.sbin.gz"));
        Assert.Equal(SaveFormat.Text, SaveIO.FormatFromPath("foo.unknown"));

        Assert.Equal("foo.scp",     SaveIO.WithExtension("foo.scp.gz", SaveFormat.Text));
        Assert.Equal("foo.sbin",    SaveIO.WithExtension("foo.scp",    SaveFormat.Binary));
        Assert.Equal("foo.sbin.gz", SaveIO.WithExtension("foo.sbin",   SaveFormat.BinaryGz));
    }

    [Fact]
    public void WorldSaver_BackupLevels_RotatesCommittedFiles()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, _) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 0;
            saver.BackupLevels = 2;

            var world = MakeWorld();
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D(100, 100, 0, 0));

            Assert.True(saver.Save(world, tmp));
            item.Amount = 2;
            Assert.True(saver.Save(world, tmp));
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak1")));
            item.Amount = 3;
            Assert.True(saver.Save(world, tmp));

            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak1")));
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak2")));
            Assert.False(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak3")));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_NormalizesStatLockAndRuneRuntimeState()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_runtime_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "Runtime";
            ch.BodyId = 0x0190;
            ch.SetStatLock(0, 2);
            ch.SetTag("STATLOCK.1", "1");
            src.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            var rune = src.CreateItem();
            rune.BaseId = 0x1F14;
            rune.SetTag("RUNE_X", "150");
            rune.SetTag("RUNE_Y", "160");
            rune.SetTag("RUNE_Z", "3");
            rune.SetTag("RUNE_MAP", "1");
            src.PlaceItem(rune, new Point3D(101, 100, 0, 0));

            Assert.True(saver.Save(src, tmp));

            string charSave = File.ReadAllText(Path.Combine(tmp, "spherechars.scp"));
            string itemSave = File.ReadAllText(Path.Combine(tmp, "sphereworld.scp"));
            Assert.Contains("StatLock[0]=2", charSave);
            Assert.Contains("StatLock[1]=1", charSave);
            Assert.DoesNotContain("TAG.STATLOCK.", charSave);
            Assert.Contains("MOREP=150,160,3,1", itemSave);
            Assert.DoesNotContain("RUNE_X", itemSave);

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loadedChar = dst.FindChar(ch.Uid);
            Assert.NotNull(loadedChar);
            Assert.Equal(2, loadedChar!.GetStatLock(0));
            Assert.Equal(1, loadedChar.GetStatLock(1));

            var loadedRune = dst.FindItem(rune.Uid);
            Assert.NotNull(loadedRune);
            Assert.True(loadedRune!.TryGetRuneMark(out Point3D mark));
            Assert.Equal(new Point3D(150, 160, 3, 1), mark);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ShardHash_IsDeterministicAndDistributes()
    {
        var histogram = new int[8];
        for (uint uid = 1; uid < 10_000; uid++)
            histogram[ShardManifest.ShardIndexForUid(uid, 8)]++;

        // No shard should be completely empty or take more than 40% of entries.
        foreach (int b in histogram)
        {
            Assert.InRange(b, 500, 5_000);
        }
        // Deterministic: calling twice gives the same shard.
        Assert.Equal(
            ShardManifest.ShardIndexForUid(0xABCD1234, 8),
            ShardManifest.ShardIndexForUid(0xABCD1234, 8));
    }

    [Fact]
    public void Rolling_SplitsIntoMultipleFilesAndReloads()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_roll_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;   // uncompressed so size threshold is predictable
            saver.ShardCount = 1;             // rolling mode
            saver.ShardSizeBytes = 4 * 1024;  // 4 KB per shard — forces multiple files

            var src = MakeWorld();
            // 50 items is enough to cross a 4KB threshold for text format
            // (each WORLDITEM record is ~150-300 bytes).
            var (_, srcItems) = Seed(src, charCount: 0, itemCount: 50);
            Assert.True(saver.Save(src, tmp));

            // Expect multiple rolling segments + a manifest.
            string manifestPath = Path.Combine(tmp, "sphereworld.manifest");
            Assert.True(File.Exists(manifestPath), "rolling save should produce a manifest");
            var manifest = ShardManifest.TryLoad(manifestPath);
            Assert.NotNull(manifest);
            Assert.True(manifest!.Files.Count > 1,
                $"expected >1 rolling file, got {manifest.Files.Count}");

            // Round-trip: every item should come back.
            var dst = MakeWorld();
            var (itemCount, _) = loader.Load(dst, tmp);
            Assert.Equal(srcItems.Count, itemCount);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Rolling_SmallWorld_StaysSingleFile()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_rollsingle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, _) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 1;                      // rolling mode
            saver.ShardSizeBytes = 1 * 1024 * 1024;    // 1 MB — way above our data

            var src = MakeWorld();
            Seed(src, charCount: 2, itemCount: 3);
            Assert.True(saver.Save(src, tmp));

            // Single file should exist, no manifest.
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp")),
                "small world should land in classic single-file layout");
            Assert.False(File.Exists(Path.Combine(tmp, "sphereworld.manifest")),
                "single-file save should not write a manifest");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void AccountPersistence_Roundtrip(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_acc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            var src = new AccountManager(lf);
            src.CreateAccount("alice", "secret1");
            src.CreateAccount("bob", "secret2");
            src.CreateAccount("with space", "secret3"); // section name boundary test

            var alice = src.FindAccount("alice")!;
            alice.SetTag("RANK", "gold");
            alice.LastIp = "127.0.0.1";
            alice.SetCharSlot(0, new Serial(0x1234));
            alice.SetCharSlot(3, new Serial(0xABCD));

            int saved = AccountPersistence.Save(src, tmp, fmt);
            Assert.Equal(3, saved);

            // File extension matches the chosen format.
            string expectedPath = Path.Combine(tmp, "sphereaccu" + SaveIO.ExtensionFor(fmt));
            Assert.True(File.Exists(expectedPath), $"expected {expectedPath}");

            var dst = new AccountManager(lf);
            int loaded = AccountPersistence.Load(dst, tmp);
            Assert.Equal(3, loaded);

            var aliceBack = dst.FindAccount("alice");
            Assert.NotNull(aliceBack);
            Assert.Equal("127.0.0.1", aliceBack!.LastIp);
            Assert.True(aliceBack.TryGetTag("RANK", out var rank));
            Assert.Equal("gold", rank);
            Assert.Equal(new Serial(0x1234), aliceBack.GetCharSlot(0));
            Assert.Equal(new Serial(0xABCD), aliceBack.GetCharSlot(3));

            Assert.NotNull(dst.FindAccount("bob"));
            Assert.NotNull(dst.FindAccount("with space"));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AccountPersistence_PlainPassword_WhenMd5Disabled()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_accplain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            File.WriteAllText(Path.Combine(tmp, "sphereaccu.scp"),
                """
                [mortal]
                PLEVEL=7
                PASSWORD=1
                CHARUID0=0204d7
                [EOF]

                """);

            var mgr = new AccountManager(lf) { Md5Passwords = false };
            int loaded = AccountPersistence.Load(mgr, tmp);
            Assert.Equal(1, loaded);

            var acc = mgr.FindAccount("mortal");
            Assert.NotNull(acc);
            Assert.True(acc!.CheckPassword("1"));
            Assert.False(acc.CheckPassword("2"));
            Assert.Equal(32, acc.PasswordHash.Length);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AccountPersistence_FormatSwitch_RemovesOldFile()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_accsw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            var m = new AccountManager(lf);
            m.CreateAccount("alice", "s");

            AccountPersistence.Save(m, tmp, SaveFormat.Text);
            Assert.True(File.Exists(Path.Combine(tmp, "sphereaccu.scp")));

            AccountPersistence.Save(m, tmp, SaveFormat.BinaryGz);
            Assert.True(File.Exists(Path.Combine(tmp, "sphereaccu.sbin.gz")));
            // Stale text file should be cleaned up on format switch.
            Assert.False(File.Exists(Path.Combine(tmp, "sphereaccu.scp")),
                "old format file should be removed after format change");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Migration_TextToBinary_PreservesData()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // Save as text.
            var (saverText, loader) = MakeIO();
            saverText.Format = SaveFormat.Text;
            saverText.ShardCount = 1;
            var src = MakeWorld();
            Seed(src, charCount: 4, itemCount: 6);
            Assert.True(saverText.Save(src, tmp));

            // Load back, save as binary + sharded — the migration flow.
            var (saverBinary, _) = MakeIO();
            saverBinary.Format = SaveFormat.BinaryGz;
            saverBinary.ShardCount = 4;
            var round1 = MakeWorld();
            loader.Load(round1, tmp);
            Assert.True(saverBinary.Save(round1, tmp));

            // Verify the manifest appeared and text files are gone.
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.manifest")));
            Assert.False(File.Exists(Path.Combine(tmp, "sphereworld.scp")));

            // Third load via binary should still produce the same population.
            var round2 = MakeWorld();
            var (items2, chars2) = loader.Load(round2, tmp);
            Assert.Equal(6, items2);
            Assert.Equal(4, chars2);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void Roundtrip_RestoresEquipmentFromCharEquipLinks(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_equip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            var worldPath = Path.Combine(tmp, "sphereworld" + SaveIO.ExtensionFor(fmt));
            using (var writer = SaveIO.OpenWriter(worldPath, fmt))
            {
                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("ID", "0EED");
                writer.WriteProperty("NAME", "equipped test sword");
                writer.WriteProperty("P", "1000,1000,0,0");
                writer.EndRecord();

                writer.BeginRecord("WORLDCHAR");
                writer.WriteProperty("SERIAL", "01");
                writer.WriteProperty("NAME", "EquipTester");
                writer.WriteProperty("P", "1000,1001,0,0");
                writer.WriteProperty("BODY", "0190");
                writer.WriteProperty("EQUIP[1]", "040001000");
                writer.EndRecord();
            }

            var loader = new WorldLoader(lf);
            var dst = MakeWorld();
            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(1, items);
            Assert.Equal(1, chars);

            var ch = dst.FindChar(new Serial(0x00000001));
            Assert.NotNull(ch);
            var equipped = ch!.GetEquippedItem(Layer.OneHanded);
            Assert.NotNull(equipped);
            Assert.Equal(new Serial(0x40001000), equipped!.Uid);
            Assert.True(equipped.IsEquipped);
            Assert.Equal(Layer.OneHanded, equipped.EquipLayer);
            Assert.Equal(ch.Uid, equipped.ContainedIn);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldLoader_DuplicateSerial_SkipsDuplicateAndContinues()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_dup_serial_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using (var writer = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();

                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();
            }

            var loader = new WorldLoader(LoggerFactory.Create(b => { }));
            var dst = MakeWorld();

            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(1, items);
            Assert.Equal(0, chars);
            Assert.NotNull(dst.FindItem(new Serial(0x40001000)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldLoader_DuplicateUuid_SkipsDuplicateAndContinues()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_dup_uuid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            string duplicateUuid = Guid.NewGuid().ToString("D");
            using (var writer = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("UUID", duplicateUuid);
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();

                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001001");
                writer.WriteProperty("UUID", duplicateUuid);
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();
            }

            var loader = new WorldLoader(LoggerFactory.Create(b => { }));
            var dst = MakeWorld();

            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(1, items);
            Assert.Equal(0, chars);
            Assert.NotNull(dst.FindItem(new Serial(0x40001000)));
            Assert.Null(dst.FindItem(new Serial(0x40001001)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldLoader_SourceStyleFixture_PreservesCompatKeysAcrossRoundtrip()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_source_fixture_{Guid.NewGuid():N}");
        string outDir = Path.Combine(tmp, "out");
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "sphereworld.scp"),
                """
                [WORLDITEM i_gold]
                SERIAL=040001000
                ID=0EED
                P=1500,1600,5,0
                AMOUNT=42
                MORE1=01
                MORE2=02
                TIMER=30
                ATTR=010
                TAG.CUSTOM=kept
                SOURCEONLY=value

                [WORLDITEM i_sword_viking]
                SERIAL=040001001
                ID=013B9
                CONT=01
                LAYER=1
                MOREP=10,11,12,0

                [EOF]

                """);

            File.WriteAllText(Path.Combine(tmp, "spherechars.scp"),
                """
                [WORLDCHAR c_man]
                SERIAL=01
                NAME=FixtureChar
                ACCOUNT=mortal
                P=1501,1600,5,0
                BODY=0190
                STR=50
                DEX=60
                INT=70
                EQUIP[1]=040001001
                MEMORY=040001000,8
                REGION=britain
                EVENTS=e_player
                UNKNOWNCHARKEY=char-value

                [EOF]

                """);

            File.WriteAllText(Path.Combine(tmp, "spheredata.scp"),
                """
                [GLOBALS]
                VAR.TEST=1

                [LIST shardlist]
                ELEM=alpha

                [EOF]

                """);

            var lf = LoggerFactory.Create(b => { });
            var loader = new WorldLoader(lf);
            var world = MakeWorld();
            var accounts = new AccountManager(lf);
            accounts.CreateAccount("mortal", "pw");

            var (items, chars) = loader.Load(world, tmp, accounts);

            Assert.Equal(2, items);
            Assert.Equal(1, chars);

            var gold = world.FindItem(new Serial(0x40001000));
            Assert.NotNull(gold);
            Assert.Equal((ushort)42, gold!.Amount);
            Assert.True(gold.TryGetTag("CUSTOM", out var custom));
            Assert.Equal("kept", custom);
            Assert.True(gold.TryGetTag("SAVE.SOURCEONLY", out var sourceOnly));
            Assert.Equal("value", sourceOnly);

            var ch = world.FindChar(new Serial(0x00000001));
            Assert.NotNull(ch);
            Assert.True(ch!.IsPlayer);
            Assert.True(ch.TryGetTag("ACCOUNT", out var accountTag));
            Assert.Equal("mortal", accountTag);
            Assert.True(ch.TryGetTag("SAVE.REGION", out var region));
            Assert.Equal("britain", region);
            Assert.True(ch.TryGetTag("SAVE.UNKNOWNCHARKEY", out var unknownChar));
            Assert.Equal("char-value", unknownChar);
            Assert.Single(ch.Memories);
            Assert.Equal(new Serial(0x40001000), ch.Memories[0].Link);

            var equipped = ch.GetEquippedItem(Layer.OneHanded);
            Assert.NotNull(equipped);
            Assert.Equal(new Serial(0x40001001), equipped!.Uid);

            var acc = accounts.FindAccount("mortal");
            Assert.NotNull(acc);
            Assert.Equal(ch.Uid, acc!.GetCharSlot(0));

            var saver = new WorldSaver(lf) { Format = SaveFormat.Text, ShardCount = 0 };
            Assert.True(saver.Save(world, outDir));

            var reloaded = MakeWorld();
            var (items2, chars2) = loader.Load(reloaded, outDir);
            Assert.Equal(items, items2);
            Assert.Equal(chars, chars2);
            var gold2 = reloaded.FindItem(new Serial(0x40001000));
            Assert.NotNull(gold2);
            Assert.True(gold2!.TryGetTag("SAVE.SOURCEONLY", out var sourceOnly2));
            Assert.Equal("value", sourceOnly2);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void Roundtrip_PreservesNestedContainersAndBankBox(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_nested_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "Banker";
            ch.IsPlayer = true;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            var bank = src.CreateItem();
            bank.ItemType = ItemType.Container;
            bank.Name = "bank box";
            ch.Equip(bank, Layer.BankBox);

            var pouch = src.CreateItem();
            pouch.ItemType = ItemType.Container;
            pouch.Name = "nested pouch";
            bank.AddItem(pouch);

            var gold = src.CreateItem();
            gold.BaseId = 0x0EED;
            gold.ItemType = ItemType.Gold;
            gold.Amount = 42;
            pouch.AddItem(gold);

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(3, items);
            Assert.Equal(1, chars);

            var loadedChar = dst.FindChar(ch.Uid);
            Assert.NotNull(loadedChar);
            var loadedBank = loadedChar!.GetEquippedItem(Layer.BankBox);
            Assert.NotNull(loadedBank);
            Assert.Equal(loadedChar.Uid, loadedBank!.ContainedIn);

            var loadedPouch = dst.FindItem(pouch.Uid);
            Assert.NotNull(loadedPouch);
            Assert.Equal(loadedBank.Uid, loadedPouch!.ContainedIn);
            Assert.Contains(loadedPouch, loadedBank.Contents);

            var loadedGold = dst.FindItem(gold.Uid);
            Assert.NotNull(loadedGold);
            Assert.Equal(loadedPouch.Uid, loadedGold!.ContainedIn);
            Assert.Equal(42, loadedGold.Amount);
            Assert.Contains(loadedGold, loadedPouch.Contents);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Save_UsesCapturedSnapshotForShardedDiskWrite()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_snapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 4;

            var src = MakeWorld();
            var backpack = src.CreateItem();
            backpack.BaseId = 0x0E75;
            src.PlaceItem(backpack, new Point3D(1000, 1000, 0, 0));

            var houseChest = src.CreateItem();
            houseChest.BaseId = 0x0E43;
            src.PlaceItem(houseChest, new Point3D(2000, 2000, 0, 0));

            var gold = src.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Amount = 100;
            backpack.AddItem(gold);

            Assert.True(saver.Save(src, tmp));

            // Mutate the live world after the snapshot was captured. The files
            // already committed from the snapshot must not observe this move.
            houseChest.AddItem(gold);

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loadedGold = dst.FindItem(gold.Uid);
            Assert.NotNull(loadedGold);
            Assert.Equal(backpack.Uid, loadedGold!.ContainedIn);
            Assert.NotEqual(houseChest.Uid, loadedGold.ContainedIn);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.Binary)]
    public void Roundtrip_PreservesOSkinDistinctFromOBody(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_oskin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "SkinTest";
            ch.IsPlayer = true;
            ch.OBody = 0x0190;
            ch.OSkin = 0x03EA;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loaded = dst.FindChar(ch.Uid);
            Assert.NotNull(loaded);
            Assert.Equal((ushort)0x0190, loaded!.OBody);
            Assert.Equal((ushort)0x03EA, loaded.OSkin);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.Binary)]
    public void Roundtrip_PreservesNpcActionState(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_npc_action_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var target = src.CreateCharacter();
            target.Name = "Target";
            src.PlaceCharacter(target, new Point3D(1001, 1000, 0, 0));

            var npc = src.CreateCharacter();
            npc.Name = "Hunter";
            npc.NpcBrain = NpcBrainType.Monster;
            npc.Action = SkillType.Mining;
            npc.Act = target.Uid;
            npc.ActArg1 = 11;
            npc.ActArg2 = 22;
            npc.ActArg3 = 33;
            npc.ActP = new Point3D(1200, 1300, 5, 0);
            npc.ActPrv = target.Uid;
            npc.ActDiff = 44;
            npc.FightTarget = target.Uid;
            npc.PetAIMode = PetAIMode.Attack;
            npc.FleeStepsCurrent = 3;
            npc.FleeStepsMax = 20;
            src.PlaceCharacter(npc, new Point3D(1000, 1000, 0, 0));

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loaded = dst.FindChar(npc.Uid);
            Assert.NotNull(loaded);
            Assert.Equal(SkillType.Mining, loaded!.Action);
            Assert.Equal(target.Uid, loaded.Act);
            Assert.Equal(11, loaded.ActArg1);
            Assert.Equal(22, loaded.ActArg2);
            Assert.Equal(33, loaded.ActArg3);
            Assert.Equal(new Point3D(1200, 1300, 5, 0), loaded.ActP);
            Assert.Equal(target.Uid, loaded.ActPrv);
            Assert.Equal(44, loaded.ActDiff);
            Assert.Equal(target.Uid, loaded.FightTarget);
            Assert.Equal(PetAIMode.Attack, loaded.PetAIMode);
            Assert.Equal(3, loaded.FleeStepsCurrent);
            Assert.Equal(20, loaded.FleeStepsMax);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
