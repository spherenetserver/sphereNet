using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 250 — deferred clean cluster: ACCOUNT UNUSED admin command (account aging)
/// and MAXAMOUNT stackable-item cap (Source-X CServerConfig m_iItemsMaxAmount +
/// per-item override, enforced when merging piles).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXWave250Tests
{
    // ---------- Account aging (ACCOUNT UNUSED <days> [DELETE]) ----------

    private static SphereNet.Server.Admin.AdminCommandProcessor MakeProcessor(AccountManager accounts) =>
        new(TestHarness.CreateWorld(), accounts, new SphereConfig(), () => 0, NullLoggerFactory.Instance);

    [Fact]
    public void AccountUnused_ListsIdleButNotActiveOrStaff()
    {
        var accounts = new AccountManager(NullLoggerFactory.Instance);

        var stale = accounts.CreateAccount("olduser", "pw")!;
        stale.LastLogin = default;
        stale.CreateDate = DateTime.UtcNow.AddDays(-100);

        var active = accounts.CreateAccount("newuser", "pw")!;
        active.LastLogin = DateTime.UtcNow;

        var staff = accounts.CreateAccount("staffuser", "pw")!;
        staff.CreateDate = DateTime.UtcNow.AddDays(-200);
        accounts.SetAccountPrivLevel("staffuser", PrivLevel.GM);

        var lines = new List<string>();
        MakeProcessor(accounts).ProcessCommand("ACCOUNT UNUSED 30", lines.Add);

        Assert.Contains(lines, l => l.Contains("olduser"));
        Assert.DoesNotContain(lines, l => l.Contains("newuser"));
        Assert.DoesNotContain(lines, l => l.Contains("staffuser")); // staff never aged
        Assert.Contains(lines, l => l.Contains("1 account(s) idle"));
    }

    [Fact]
    public void AccountUnused_Delete_RemovesOnlyIdleNonStaff()
    {
        var accounts = new AccountManager(NullLoggerFactory.Instance);

        var stale = accounts.CreateAccount("olduser", "pw")!;
        stale.CreateDate = DateTime.UtcNow.AddDays(-100);
        var active = accounts.CreateAccount("newuser", "pw")!;
        active.LastLogin = DateTime.UtcNow;

        MakeProcessor(accounts).ProcessCommand("ACCOUNT UNUSED 30 DELETE", _ => { });

        Assert.Null(accounts.FindAccount("olduser"));
        Assert.NotNull(accounts.FindAccount("newuser"));
    }

    // ---------- MAXAMOUNT stackable cap ----------

    private const string PileScript = """
        [ITEMDEF 0f5e]
        DEFNAME=i_test_pile
        NAME=test pile
        CAN=0x0100

        [ITEMDEF 0f60]
        DEFNAME=i_test_solid
        NAME=test solid
        """;

    private static void LoadDefs(string contents)
    {
        string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spherenet_w250_{Guid.NewGuid():N}.scp");
        System.IO.File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(NullLoggerFactory.Instance.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = System.IO.Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void MaxAmount_StackableUsesGlobalDefault_NonStackableZero()
    {
        LoadDefs(PileScript);
        var pile = new Item { BaseId = 0x0F5E };
        var solid = new Item { BaseId = 0x0F60 };

        Assert.True(pile.IsStackable);
        Assert.Equal(60000, pile.MaxAmount); // Source-X default m_iItemsMaxAmount
        pile.TryGetProperty("MAXAMOUNT", out string pileMax);
        Assert.Equal("60000", pileMax);

        Assert.False(solid.IsStackable);
        Assert.Equal(0, solid.MaxAmount); // non-stackable → 0
    }

    [Fact]
    public void MaxAmount_PerItemOverrideWins()
    {
        LoadDefs(PileScript);
        var pile = new Item { BaseId = 0x0F5E };
        pile.TrySetProperty("MAXAMOUNT", "5000");
        Assert.Equal(5000, pile.MaxAmount);
        Assert.Equal(5000, pile.MaxAmountOverride);

        pile.TrySetProperty("MAXAMOUNT", "-1"); // clears back to global
        Assert.Null(pile.MaxAmountOverride);
        Assert.Equal(60000, pile.MaxAmount);
    }

    [Fact]
    public void MaxAmount_EnforcedWhenMergingPiles()
    {
        LoadDefs(PileScript);
        var container = new Item { ItemType = ItemType.Container, BaseId = 0x0E75 };

        var a = new Item { BaseId = 0x0F5E, Amount = 59000 };
        Assert.Same(a, container.TryAddItemWithStack(a));

        // 59000 + 2000 = 61000 > 60000 → does NOT merge, stays a separate item.
        var b = new Item { BaseId = 0x0F5E, Amount = 2000 };
        var addedB = container.TryAddItemWithStack(b);
        Assert.Same(b, addedB);
        Assert.Equal(59000, a.Amount);
        Assert.Equal(2, container.ContentCount);

        // 59000 + 500 = 59500 <= 60000 → merges into the existing pile.
        var c = new Item { BaseId = 0x0F5E, Amount = 500 };
        container.TryAddItemWithStack(c);
        Assert.Equal(59500, a.Amount);
    }
}
