using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class CharacterDestroyLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterSelectionVetoPreservesSlotAndReportsFailure(bool playerFunction)
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter(); ch.IsPlayer = true;
        var account = new SphereNet.Game.Accounts.Account { Name = "delete-test" };
        account.SetPassword("pw"); account.SetCharSlot(0, ch.Uid);
        var client = TestHarness.CreateClient(logs, world, new SphereNet.Game.Accounts.AccountManager(logs), 993);
        TestHarness.AttachCharacter(client, null!, account);
        if (playerFunction) world.PlayerDeleteAllowed = _ => false;
        else world.CharacterDeleteAllowed = _ => false;
        client.HandleCharDelete(0, "pw");
        Assert.Equal(ch.Uid, account.GetCharSlot(0)); Assert.Same(ch, world.FindChar(ch.Uid));
        var packets = TestHarness.GetQueuedPackets(client.NetState);
        Assert.Contains(packets, packet => packet.Span[0] == 0x85 && packet.Span[1] == 5);
        Assert.DoesNotContain(packets, packet => packet.Span[0] == 0x85 && packet.Span[1] == 0);
    }

    private sealed class Console(PrivLevel level) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => level;
        public string GetName() => "test";
        public void SysMessage(string message) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectAndWorldDeletionHonorVetoBeforeCleanup(bool direct)
    {
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter(); ch.PrivLevel = PrivLevel.GM;
        var item = world.CreateItem(); ch.Equip(item, Layer.Shirt);
        int calls = 0, removed = 0;
        world.ObjectDeleting += _ => removed++;
        world.CharacterDeleteAllowed = target => { calls++; target.Delete(); return false; };
        if (direct) ch.Delete(); else world.DeleteObject(ch);
        Assert.Equal(1, calls); Assert.Equal(0, removed); Assert.False(ch.IsDeleted);
        Assert.Same(ch, world.FindChar(ch.Uid)); Assert.Same(item, ch.GetEquippedItem(Layer.Shirt));
    }

    [Theory]
    [InlineData("REMOVE", false)]
    [InlineData("DESTROY", true)]
    public void ScriptDestroyIgnoresVetoButRemoveDoesNot(string verb, bool deleted)
    {
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter();
        int calls = 0; world.CharacterDeleteAllowed = _ => { calls++; return false; };
        Assert.True(ch.TryExecuteCommand(verb, "", new Console(PrivLevel.Admin)));
        Assert.Equal(1, calls); Assert.Equal(deleted, ch.IsDeleted);
    }

    [Theory]
    [InlineData(false, false, "destroy")]
    [InlineData(true, false, "destroy,player")]
    [InlineData(false, true, "destroy,player")]
    public void PlayerCallbackFollowsDestroyAndBothRunWhenForced(bool allowDestroy, bool force, string expected)
    {
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter(); ch.IsPlayer = true;
        var order = new List<string>();
        world.CharacterDeleteAllowed = _ => { order.Add("destroy"); return allowDestroy; };
        world.PlayerDeleteAllowed = _ => { order.Add("player"); return false; };
        Assert.Equal(force, world.TryDeleteObject(ch, force));
        Assert.Equal(expected, string.Join(',', order)); Assert.Equal(force, ch.IsDeleted);
    }

    [Theory]
    [InlineData(PrivLevel.GM, "1", false)]
    [InlineData(PrivLevel.Admin, "", false)]
    [InlineData(PrivLevel.Admin, "1", true)]
    public void ScriptPlayerRemovalRequiresAdminAndExplicitArgument(PrivLevel level, string argument, bool deleted)
    {
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter(); ch.IsPlayer = true;
        Assert.Equal(deleted, ch.TryExecuteCommand("DESTROY", argument, new Console(level)));
        Assert.Equal(deleted, ch.IsDeleted);
    }

    [Fact]
    public void EquipmentVetoDropsItemBeforeCharacterDisappears()
    {
        var world = TestHarness.CreateWorld(); var ch = world.CreateCharacter(); ch.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(ch, new Point3D(100, 100));
        var item = world.CreateItem(); ch.Equip(item, Layer.Shirt);
        world.ItemDeleteAllowed = _ => false;
        ch.Delete();
        Assert.True(ch.IsDeleted); Assert.False(item.IsDeleted);
        Assert.False(item.ContainedIn.IsValid); Assert.Equal(new Point3D(100, 100), item.Position);
    }
}
