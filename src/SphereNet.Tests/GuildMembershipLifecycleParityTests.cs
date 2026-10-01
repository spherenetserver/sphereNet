using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Guild;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Stone membership records through the real script interpreter.
///
/// Source-X hands MEMBER.n / MEMBERFROMUID.uid out as the CStoneMember record of THAT
/// stone (CItemStone::r_GetRef, CItemStone.cpp:214); its LOYALTO validates the vote and
/// holds the election (CStoneMember::SetLoyalTo, CStoneMember.cpp:453). Deleting a record
/// - RESIGN, the gump's Leave, the stone going away - recounts the votes and makes the
/// character forget the stone (CStoneMember destructor, CStoneMember.cpp:400-427). The
/// war/alliance questions ask f_stonesys_internal_isatwarwith / _isalliedwith first
/// (CItemStone.cpp:1338/1374).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GuildMembershipLifecycleParityTests
{
    private sealed class TestConsole(Character? sourceChar = null) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "TEST";
        public void SysMessage(string text) { }
        public IScriptObj? GetSourceChar() => sourceChar;
    }

    private sealed class Harness : IDisposable
    {
        public GameWorld World { get; }
        public GuildManager Guilds { get; } = new();
        public TriggerRunner Runner { get; }
        private readonly string _path;

        public Harness(string scriptText = "[EOF]")
        {
            var lf = LoggerFactory.Create(_ => { });
            World = new GameWorld(lf);
            World.InitMap(0, 256, 256);
            ObjBase.ResolveWorld = () => World;
            Item.ResolveWorld = () => World;
            Item.ResolveGuild = uid => Guilds.GetGuild(uid);
            Item.ResolveGuildManager = () => Guilds;
            Character.ResolveGuildManager = _ => Guilds;

            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
            _path = Path.Combine(Path.GetTempPath(), $"sphnet_guild_{Guid.NewGuid():N}.scp");
            File.WriteAllText(_path, scriptText);
            resources.LoadResourceFile(_path);
            var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
            Runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
            interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
                Runner.TryRunFunction(name, target, source, args, scope, out var r) ? r : TriggerResult.Default;
            interpreter.CallFunction = (name, target, source, args) =>
                Runner.TryRunFunction(name, target, source, args, out var r) ? r : TriggerResult.Default;
            interpreter.FunctionLookup = Runner.HasFunction;
            interpreter.ResolveObjectRef = (obj, head) => obj is ObjBase o ? o.ResolveScriptRefHead(head) : null;
            GuildDef.RelationScriptQuery = GuildManager.CreateRelationScriptQuery(() => World, Runner);
        }

        public (Item Stone, GuildDef Guild) Stone(bool town = false, string name = "The Order")
        {
            var stone = World.CreateItem();
            stone.ItemType = town ? ItemType.StoneTown : ItemType.StoneGuild;
            stone.BaseId = 0x0ED4;
            World.PlaceItem(stone, new Point3D(100, 100, 0, 0));
            var guild = Guilds.CreateGuild(stone.Uid, name, Serial.Invalid, isTownStone: town);
            guild.RemoveMember(Serial.Invalid);   // the first member to join becomes master
            return (stone, guild);
        }

        public Character Player(string name)
        {
            var ch = World.CreateCharacter();
            ch.IsPlayer = true;
            ch.Name = name;
            World.PlaceCharacter(ch, new Point3D(101, 100, 0, 0));
            return ch;
        }

        /// <summary>Full members, stamped with the stone memory the way joining does.</summary>
        public Character Join(Item stone, GuildDef guild, string name)
        {
            var ch = Player(name);
            guild.JoinAsMember(ch.Uid);
            ch.Memory_AddObjTypes(stone.Uid, guild.IsTownStone ? MemoryType.Town : MemoryType.Guild);
            return ch;
        }

        public void Run(IScriptObj target, string body, Character? src = null)
        {
            string fn = $"f_t_{Guid.NewGuid():N}";
            var lf = LoggerFactory.Create(_ => { });
            var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
            string path = Path.Combine(Path.GetTempPath(), $"sphnet_guild_run_{Guid.NewGuid():N}.scp");
            try
            {
                File.WriteAllText(path, File.ReadAllText(_path).Replace("[EOF]", "") +
                    $"\n[FUNCTION {fn}]\n{body}\n[EOF]\n");
                res.LoadResourceFile(path);
                var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
                var runner = new TriggerRunner(interpreter, res, lf.CreateLogger<TriggerRunner>());
                interpreter.FunctionLookup = runner.HasFunction;
                interpreter.ResolveObjectRef = (obj, head) => obj is ObjBase o ? o.ResolveScriptRefHead(head) : null;
                Assert.True(runner.TryRunFunction(fn, target, new TestConsole(src),
                    new TriggerArgs { Source = src }, out _));
            }
            finally
            {
                File.Delete(path);
            }
        }

        public void Dispose() => File.Delete(_path);
    }

    private static string Hex(Character ch) => $"0{ch.Uid.Value:X}";

    // ---- G01: MEMBER.n / MEMBERFROMUID.uid records -----------------------

    [Fact]
    public void AVoteThroughTheMemberReferenceIsStoredAndHoldsTheElection()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var a = h.Join(stone, guild, "a");   // master
        var b = h.Join(stone, guild, "b");
        var c = h.Join(stone, guild, "c");
        Assert.Equal(a.Uid, guild.GetMaster()!.CharUid);

        h.Run(stone, $"MEMBER.1.LOYALTO={Hex(c)}\nMEMBER.2.LOYALTO={Hex(c)}\nTAG.read=<MEMBER.1.LOYALTO>");

        Assert.Equal(c.Uid, guild.FindMember(b.Uid)!.LoyalTo);
        Assert.Equal(c.Uid, guild.GetMaster()!.CharUid);
        Assert.Equal(Hex(c), stone.Tags.Get("READ"));
    }

    [Fact]
    public void MemberFromUidVerbFormVotesToo()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var a = h.Join(stone, guild, "a");
        var b = h.Join(stone, guild, "b");
        var c = h.Join(stone, guild, "c");

        h.Run(stone, $"MEMBERFROMUID.{Hex(a)}.LOYALTO {Hex(b)}\nMEMBERFROMUID.{Hex(c)}.LOYALTO {Hex(b)}");

        Assert.Equal(b.Uid, guild.GetMaster()!.CharUid);
    }

    [Fact]
    public void ACandidateCannotBeVotedForNorVote_AndZeroIsAVoteForOneself()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var a = h.Join(stone, guild, "a");
        var b = h.Join(stone, guild, "b");
        var cand = h.Player("cand");
        guild.AddRecruit(cand.Uid);
        var outsider = h.Player("outsider");

        h.Run(stone,
            $"MEMBERFROMUID.{Hex(b)}.LOYALTO={Hex(cand)}\n" +
            $"MEMBERFROMUID.{Hex(a)}.LOYALTO={Hex(outsider)}\n" +
            $"MEMBERFROMUID.{Hex(cand)}.LOYALTO={Hex(a)}\n" +
            "TAG.zero=<MEMBER.1.LOYALTO>");

        // Refused votes are reset to the voter itself, never stored as asked.
        Assert.Equal(b.Uid, guild.FindMember(b.Uid)!.LoyalTo);
        Assert.Equal(a.Uid, guild.FindMember(a.Uid)!.LoyalTo);
        Assert.Equal(cand.Uid, guild.FindMember(cand.Uid)!.LoyalTo);
        Assert.Equal(GuildPriv.Candidate, guild.FindMember(cand.Uid)!.Priv);

        h.Run(stone, $"MEMBERFROMUID.{Hex(b)}.LOYALTO={Hex(a)}\nMEMBERFROMUID.{Hex(b)}.LOYALTO=0\nTAG.self=<MEMBERFROMUID.{Hex(b)}.LOYALTO>");
        Assert.Equal(Hex(b), stone.Tags.Get("SELF"));
    }

    [Fact]
    public void TheRecordBelongsToTheStoneItWasReachedThrough()
    {
        using var h = new Harness();
        var (gStone, guild) = h.Stone();
        var (tStone, town) = h.Stone(town: true, name: "Britain");
        var gMaster = h.Join(gStone, guild, "gm");
        var tMaster = h.Join(tStone, town, "tm");
        var both = h.Player("both");
        guild.JoinAsMember(both.Uid);
        town.JoinAsMember(both.Uid);

        h.Run(tStone,
            $"MEMBERFROMUID.{Hex(both)}.GUILDTITLE=Mayor\n" +
            $"MEMBERFROMUID.{Hex(both)}.PRIV=4\n" +
            $"MEMBERFROMUID.{Hex(tMaster)}.LOYALTO={Hex(both)}\n" +
            $"TAG.name=<MEMBERFROMUID.{Hex(both)}.NAME>");

        Assert.Equal("Mayor", town.FindMember(both.Uid)!.Title);
        Assert.Equal("", guild.FindMember(both.Uid)!.Title);
        Assert.Equal(GuildPriv.Accepted, town.FindMember(both.Uid)!.Priv);
        Assert.Equal(GuildPriv.Member, guild.FindMember(both.Uid)!.Priv);
        Assert.Equal(gMaster.Uid, guild.GetMaster()!.CharUid);
        Assert.Equal("both", tStone.Tags.Get("NAME"));
    }

    [Fact]
    public void ABareMemberReferenceReadsAsOne_TheUidComesFromItsUidKey()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var a = h.Join(stone, guild, "a");

        h.Run(stone,
            "TAG.bare=<MEMBER.0>\n" +
            $"TAG.byuid=<MEMBERFROMUID.{Hex(a)}>\n" +
            "TAG.none=<MEMBER.9>\n" +
            "TAG.uid=<MEMBER.0.UID>");

        Assert.Equal("1", stone.Tags.Get("BARE"));
        Assert.Equal("1", stone.Tags.Get("BYUID"));
        Assert.Equal("0", stone.Tags.Get("NONE"));
        Assert.Equal(a.Uid.Value, ObjBase.ParseHexOrDecUInt(stone.Tags.Get("UID")!));
    }

    // ---- G02: script RESIGN = client Leave ---------------------------------

    [Fact]
    public void WhenTheMasterLeavesAndTheVoteTiesTheGuildHasNoMaster()
    {
        // CStoneMember destructor -> ElectMaster: nobody is promoted ahead of the
        // election, and a tie "leaves the current master as is" - there is none.
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var m1 = h.Join(stone, guild, "one");
        var m2 = h.Join(stone, guild, "two");
        var m3 = h.Join(stone, guild, "three");
        Assert.Equal(m1.Uid, guild.GetMaster()!.CharUid);

        h.Run(stone, $"RESIGN {Hex(m1)}");

        Assert.Null(guild.GetMaster());
        Assert.Equal(GuildPriv.Member, guild.FindMember(m2.Uid)!.Priv);
        Assert.Equal(GuildPriv.Member, guild.FindMember(m3.Uid)!.Priv);
    }

    [Fact]
    public void ResigningClearsEveryMemoryOfTheStonesType_ButNotTheOtherType()
    {
        // CChar::Memory_ClearTypes(MEMORY_GUILD) takes the bit off every memory that
        // carries it (CStoneMember.cpp:425), a stale one for another stone included.
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var (oldStone, _) = h.Stone(name: "Old");
        var (townStone, town) = h.Stone(town: true, name: "Britain");
        h.Join(stone, guild, "master");
        var ch = h.Join(stone, guild, "leaver");
        ch.Memory_AddObjTypes(oldStone.Uid, MemoryType.Guild);
        town.JoinAsMember(ch.Uid);
        ch.Memory_AddObjTypes(townStone.Uid, MemoryType.Town);

        h.Run(stone, $"RESIGN {Hex(ch)}");

        Assert.Null(ch.Memory_FindTypes(MemoryType.Guild));
        Assert.NotNull(ch.Memory_FindObjTypes(townStone.Uid, MemoryType.Town));
    }

    [Fact]
    public void ScriptResignRecountsTheVotesLikeTheGumpLeave()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var m1 = h.Join(stone, guild, "one");    // master
        var m2 = h.Join(stone, guild, "two");
        var m3 = h.Join(stone, guild, "three");
        guild.FindMember(m2.Uid)!.LoyalTo = m3.Uid;
        guild.FindMember(m3.Uid)!.LoyalTo = m3.Uid;

        h.Run(stone, $"RESIGN {Hex(m1)}");

        Assert.Null(guild.FindMember(m1.Uid));
        Assert.Equal(m3.Uid, guild.GetMaster()!.CharUid);
        Assert.Null(m1.Memory_FindObj(stone.Uid));
    }

    [Fact]
    public void ScriptResignWithoutArgumentsResignsSrc_AndTheLastMemberEndsTheWar()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var (enemyStone, enemy) = h.Stone(name: "Chaos");
        var solo = h.Join(stone, guild, "solo");
        h.Join(enemyStone, enemy, "foe");
        h.Guilds.DeclareWar(stone.Uid, enemyStone.Uid);
        h.Guilds.DeclareWar(enemyStone.Uid, stone.Uid);
        Assert.True(guild.IsAtWarWith(enemyStone.Uid));

        h.Run(stone, "RESIGN", src: solo);

        Assert.Null(guild.FindMember(solo.Uid));
        Assert.Null(guild.GetRelation(enemyStone.Uid));
        Assert.Null(enemy.GetRelation(stone.Uid));
        Assert.Null(solo.Memory_FindObj(stone.Uid));
    }

    // ---- G03: stone deletion / disband forget the stone --------------------

    [Fact]
    public void DeletingTheStoneClearsTheGuildMemoryButKeepsTheTownOne()
    {
        using var h = new Harness();
        var (gStone, guild) = h.Stone();
        var (tStone, town) = h.Stone(town: true, name: "Britain");
        var ch = h.Join(gStone, guild, "citizen");
        town.JoinAsMember(ch.Uid);
        ch.Memory_AddObjTypes(tStone.Uid, MemoryType.Town);

        h.Guilds.OnStoneDeleted(gStone.Uid, h.World);

        Assert.Null(h.Guilds.FindGuildFor(ch.Uid));
        Assert.Null(ch.Memory_FindObjTypes(gStone.Uid, MemoryType.Guild));
        Assert.Null(ch.Memory_FindTypes(MemoryType.Guild));
        Assert.NotNull(ch.Memory_FindObjTypes(tStone.Uid, MemoryType.Town));
        Assert.Same(town, h.Guilds.FindTownFor(ch.Uid));
    }

    [Fact]
    public void DisbandClearsEveryMembersMemory()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        var a = h.Join(stone, guild, "a");
        var b = h.Join(stone, guild, "b");

        h.Guilds.RemoveGuild(stone.Uid, h.World);

        Assert.Null(a.Memory_FindObj(stone.Uid));
        Assert.Null(b.Memory_FindObj(stone.Uid));
    }

    // ---- G05: disband clears structure tags --------------------------------

    [Fact]
    public void DisbandClearsTheHouseAndShipTags()
    {
        using var h = new Harness();
        var (stone, guild) = h.Stone();
        h.Join(stone, guild, "a");
        var house = h.World.CreateItem();
        var ship = h.World.CreateItem();
        guild.AddHouse(house.Uid);
        guild.AddShip(ship.Uid);
        h.Guilds.SerializeAllToTags(h.World);
        Assert.NotNull(stone.Tags.Get("GUILD.HOUSES"));
        Assert.NotNull(stone.Tags.Get("GUILD.SHIPS"));

        h.Guilds.RemoveGuild(stone.Uid, h.World);

        Assert.Null(stone.Tags.Get("GUILD.HOUSES"));
        Assert.Null(stone.Tags.Get("GUILD.SHIPS"));
    }

    // ---- G06: the saved name comes back whole ------------------------------

    [Fact]
    public void ALongGuildNameSurvivesSaveAndLoad()
    {
        using var h = new Harness();
        string longName = new string('N', 30) + " of the very long guild name";   // 58 chars
        var (stone, guild) = h.Stone(name: longName);
        h.Join(stone, guild, "a");
        h.Guilds.SerializeAllToTags(h.World);

        var reloaded = new GuildManager();
        reloaded.DeserializeFromWorld(h.World);

        Assert.Equal(longName, reloaded.GetGuild(stone.Uid)!.Name);
    }

    // ---- G07: relation override functions ----------------------------------

    private const string RelationScript = """
        [FUNCTION f_stonesys_internal_isatwarwith]
        TAG.lastargo=<ARGO.UID>
        IF (<TAG0.WARVERDICT> == 9)
            RETURN 2
        ENDIF
        IF (<TAG0.WARVERDICT> == 8)
            TAG.ran=1
        ENDIF
        IF (<TAG0.WARVERDICT> < 8)
            RETURN <TAG0.WARVERDICT>
        ENDIF

        [FUNCTION f_stonesys_internal_isalliedwith]
        RETURN <TAG0.ALLYVERDICT>

        [EOF]
        """;

    [Fact]
    public void TheWarFunctionDecidesWithZeroOrOne_AndTheFlagsDecideOtherwise()
    {
        using var h = new Harness(RelationScript);
        var (stone, guild) = h.Stone();
        var (other, _) = h.Stone(name: "Chaos");
        h.Join(stone, guild, "a");

        // Not declared, the function says war.
        stone.SetTag("WARVERDICT", "1");
        Assert.True(guild.IsAtWarWith(other.Uid));
        Assert.Equal($"0{other.Uid.Value:X}", stone.Tags.Get("LASTARGO"));

        // Declared mutually, the function says peace.
        h.Guilds.DeclareWar(stone.Uid, other.Uid);
        h.Guilds.DeclareWar(other.Uid, stone.Uid);
        stone.SetTag("WARVERDICT", "0");
        Assert.False(guild.IsAtWarWith(other.Uid));

        // RETURN 2 and no RETURN at all fall back to the flags.
        stone.SetTag("WARVERDICT", "9");
        Assert.True(guild.IsAtWarWith(other.Uid));
        stone.SetTag("WARVERDICT", "8");
        Assert.True(guild.IsAtWarWith(other.Uid));
        Assert.Equal("1", stone.Tags.Get("RAN"));
    }

    [Fact]
    public void TheAllianceFunctionDecides_AndWithoutAnyFunctionTheFlagsDo()
    {
        using var h = new Harness(RelationScript);
        var (stone, guild) = h.Stone();
        var (other, _) = h.Stone(name: "Friends");
        h.Join(stone, guild, "a");

        stone.SetTag("ALLYVERDICT", "1");
        Assert.True(guild.IsAlliedWith(other.Uid));
        stone.SetTag("ALLYVERDICT", "2");
        Assert.False(guild.IsAlliedWith(other.Uid));
        h.Guilds.DeclareAlliance(stone.Uid, other.Uid);
        h.Guilds.DeclareAlliance(other.Uid, stone.Uid);
        Assert.True(guild.IsAlliedWith(other.Uid));
        stone.SetTag("ALLYVERDICT", "0");
        Assert.False(guild.IsAlliedWith(other.Uid));

        using var plain = new Harness();
        var (s2, g2) = plain.Stone();
        var (o2, _) = plain.Stone(name: "Other");
        plain.Guilds.DeclareWar(s2.Uid, o2.Uid);
        plain.Guilds.DeclareWar(o2.Uid, s2.Uid);
        Assert.True(g2.IsAtWarWith(o2.Uid));
    }
}
