using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Parity-matrix backbone (Faz 0 / Faz 1): backing primitives for the Source-X
/// SERV.* world-ops verbs. The script-side resolver glue (Program.Scripting) is
/// thin delegation; the testable state operations live on GameWorld. Here we lock
/// in SERV.CLEARLISTS — the previously-missing list counterpart of SERV.CLEARVARS.
/// </summary>
public class ParityMatrixServTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        return world;
    }

    [Fact]
    public void ClearGlobalLists_NoPrefix_DropsEveryListAndReportsCount()
    {
        var world = CreateWorld();
        world.GetOrCreateList("party_log").Add("a");
        world.GetOrCreateList("quest_state").Add("b");
        world.GetOrCreateList("quest_done").Add("c");
        Assert.Equal(3, world.GlobalListCount);

        int cleared = world.ClearGlobalLists();

        Assert.Equal(3, cleared);
        Assert.Equal(0, world.GlobalListCount);
        Assert.Empty(world.GetAllGlobalLists());
    }

    [Fact]
    public void ClearGlobalLists_WithPrefix_ClearsOnlyMatchingNames()
    {
        var world = CreateWorld();
        world.GetOrCreateList("quest_state").Add("a");
        world.GetOrCreateList("quest_done").Add("b");
        world.GetOrCreateList("party_log").Add("c");

        int cleared = world.ClearGlobalLists("quest_"); // case-insensitive prefix

        Assert.Equal(2, cleared);
        Assert.Equal(1, world.GlobalListCount);
        // The non-matching list survives untouched.
        Assert.Single(world.GetOrCreateList("party_log"));
    }

    [Fact]
    public void ClearGlobalLists_MirrorsClearGlobalVars_Independently()
    {
        var world = CreateWorld();
        world.SetGlobalVar("score", "10");
        world.GetOrCreateList("log").Add("x");

        // Clearing lists leaves vars intact, and vice versa — the two stores are
        // independent (the bug was that only the var store had a clear primitive).
        Assert.Equal(1, world.ClearGlobalLists());
        Assert.Equal("10", world.GetGlobalVar("score"));

        Assert.Equal(1, world.ClearGlobalVars());
        Assert.Null(world.GetGlobalVar("score"));
    }

    [Fact]
    public void AccountManager_GetByIndex_GivesStableNameOrderedAccess()
    {
        var accounts = new AccountManager(LoggerFactory.Create(_ => { }));
        // Created out of alphabetical order — SERV.ACCOUNT.n must still index a stable
        // (name-ordered) sequence so admin dialogs can iterate 0..Count-1.
        accounts.CreateAccount("charlie", "p");
        accounts.CreateAccount("alice", "p");
        accounts.CreateAccount("bob", "p");

        Assert.Equal(3, accounts.Count);
        Assert.Equal("alice", accounts.GetByIndex(0)!.Name);
        Assert.Equal("bob", accounts.GetByIndex(1)!.Name);
        Assert.Equal("charlie", accounts.GetByIndex(2)!.Name);

        // Out of range → null (the SERV.ACCOUNT.n resolver maps that to "0").
        Assert.Null(accounts.GetByIndex(3));
        Assert.Null(accounts.GetByIndex(-1));
    }
}
