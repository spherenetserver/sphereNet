using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Collections;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

public class RuntimePerformancePressureTests
{
    [Fact]
    public void TriggerArgs_ArgvCache_InvalidatesWhenArgStringChanges()
    {
        var args = new ExecTriggerArgs(null, argStr: "100,200 alpha");

        Assert.Equal(3, args.GetArgc());
        Assert.Equal("100", args.GetArgv()[0]);

        args.ArgString = "new,value";

        Assert.Equal(2, args.GetArgc());
        Assert.Equal("new", args.GetArgv()[0]);
        Assert.Equal("value", args.GetArgv()[1]);
    }

    [Fact]
    public void ScriptInterpreter_Dargv_UsesCachedArgTokens()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var scope = new ScriptScope();
        var args = new ExecTriggerArgs(null, argStr: "10,20 gate");
        var lines = ParseKeys(
            "TAG.COUNT=<DARGV>",
            "TAG.X=<ARGV[0]>",
            "ARGS=new,values",
            "TAG.NEWCOUNT=<DARGV>",
            "TAG.NEWX=<ARGV[0]>");

        interpreter.Execute(lines, target, null, args, scope);

        Assert.True(target.TryGetProperty("TAG.COUNT", out string count));
        Assert.True(target.TryGetProperty("TAG.X", out string x));
        Assert.True(target.TryGetProperty("TAG.NEWCOUNT", out string newCount));
        Assert.True(target.TryGetProperty("TAG.NEWX", out string newX));
        Assert.Equal("3", count);
        Assert.Equal("10", x);
        Assert.Equal("2", newCount);
        Assert.Equal("new", newX);
    }

    [Fact]
    public void UidTable_Free_ReusesItemAndCharacterIndices()
    {
        var table = new UidTable();
        var item = table.AllocateItem();
        var ch = table.AllocateChar();

        table.Free(item);
        table.Free(ch);

        Assert.Equal(item, table.AllocateItem());
        Assert.Equal(ch, table.AllocateChar());
    }

    [Fact]
    public void UidTable_ReRegister_AdvancesPastSavedSerial()
    {
        var table = new UidTable();
        var temp = table.AllocateItem();
        var saved = Serial.NewItem(500);

        table.ReRegister(temp, saved, new object());

        Assert.True(table.AllocateItem().Index > saved.Index);
    }

    [Fact]
    public void WorldSaver_ShardedPartition_RoundtripsCounts()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_perf_save_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var saver = new WorldSaver(loggerFactory)
            {
                Format = SaveFormat.Text,
                ShardCount = 4
            };
            var loader = new WorldLoader(loggerFactory);
            var world = CreateWorld(loggerFactory);
            for (int i = 0; i < 20; i++)
            {
                var item = world.CreateItem();
                item.BaseId = (ushort)(0x0EED + i);
                world.PlaceItem(item, new Point3D((short)(100 + i), 100, 0, 0));
            }
            for (int i = 0; i < 8; i++)
            {
                var ch = world.CreateCharacter();
                ch.Name = $"Npc{i}";
                ch.BodyId = 0x0190;
                world.PlaceCharacter(ch, new Point3D((short)(200 + i), 200, 0, 0));
            }

            Assert.True(saver.Save(world, tmp));

            var dst = CreateWorld(loggerFactory);
            var (items, chars) = loader.Load(dst, tmp);
            Assert.Equal(20, items);
            Assert.Equal(8, chars);
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.manifest")));
            Assert.True(File.Exists(Path.Combine(tmp, "spherechars.manifest")));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GameWorld_VisitInRange_ReturnsCharsAndItemsInSinglePass()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld(loggerFactory);
        var center = new Point3D(100, 100, 0, 0);
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, center);
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));
        var far = world.CreateItem();
        world.PlaceItem(far, new Point3D(300, 300, 0, 0));

        var chars = new List<Character>();
        var items = new List<Item>();
        world.VisitInRange(center, 2, chars.Add, items.Add);

        Assert.Contains(ch, chars);
        Assert.Contains(item, items);
        Assert.DoesNotContain(far, items);
    }

    private static GameWorld CreateWorld(ILoggerFactory loggerFactory)
    {
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        return world;
    }

    private static List<ScriptKey> ParseKeys(params string[] lines)
    {
        var list = new List<ScriptKey>();
        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq >= 0)
                list.Add(new ScriptKey(line[..eq], line[(eq + 1)..]));
            else
                list.Add(new ScriptKey(line, ""));
        }
        return list;
    }
}
