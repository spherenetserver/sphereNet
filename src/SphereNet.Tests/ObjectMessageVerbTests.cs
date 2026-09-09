using System;
using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// MESSAGE and MSG put a line over the object (port plan İŞ-15).
///
/// Upstream has them on CObjBase, so every object answers them (CObjBase.cpp:2402), and
/// the audience depends on who asked: with a source character the line is private to
/// that character, without one it goes over the object for everyone nearby.
///
/// Here the verb existed only as an admin console command and not on objects at all, so
/// a script line like `MESSAGE You feel a chill` did nothing. The packs on hand issue it
/// 330 times - by a wide margin the most-used verb this engine did not implement.
/// </summary>
public sealed class ObjectMessageVerbTests : IDisposable
{
    private readonly List<(ObjBase Obj, string Text, Character? To)> _sent = [];

    public void Dispose() => ObjBase.OnObjectMessage = null;

    private sealed class Console(Character? actor) : ITextConsole
    {
        public void SysMessage(string text) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Player;
        public string GetName() => actor?.Name ?? "console";
        public IScriptObj? GetSourceChar() => actor;
    }

    private (GameWorld World, Character Asker, Item Thing) Bench()
    {
        var world = TestHarness.CreateWorld();
        var asker = world.CreateCharacter();
        asker.Name = "Asker";
        world.PlaceCharacter(asker, new Point3D(100, 100, 0, 0));
        var thing = world.CreateItem();
        thing.BaseId = 0x0F51;
        thing.Name = "a strange lever";
        world.PlaceItem(thing, new Point3D(101, 100, 0, 0));

        // Installed here, not in the constructor: the shared-statics reset runs between
        // the two.
        ObjBase.OnObjectMessage = (obj, text, to) => _sent.Add((obj, text, to));
        return (world, asker, thing);
    }

    [Fact]
    public void AnItemAnswersTheVerb()
    {
        var (_, asker, thing) = Bench();

        Assert.True(thing.TryExecuteCommand("MESSAGE", "You feel a chill", new Console(asker)));

        var (obj, text, to) = Assert.Single(_sent);
        Assert.Same(thing, obj);                 // the line belongs to the OBJECT
        Assert.Equal("You feel a chill", text);
        Assert.Same(asker, to);                  // and is private to whoever asked
    }

    [Fact]
    public void MsgIsTheSameVerb()
    {
        var (_, asker, thing) = Bench();

        Assert.True(thing.TryExecuteCommand("MSG", "again", new Console(asker)));

        Assert.Single(_sent);
        Assert.Equal("again", _sent[0].Text);
    }

    [Fact]
    public void ACharacterAnswersItToo()
    {
        var (world, asker, _) = Bench();
        var other = world.CreateCharacter();
        other.Name = "Other";
        world.PlaceCharacter(other, new Point3D(102, 100, 0, 0));

        Assert.True(other.TryExecuteCommand("MESSAGE", "over the other one", new Console(asker)));

        Assert.Same(other, _sent[0].Obj);
        Assert.Same(asker, _sent[0].To);
    }

    [Fact]
    public void WithNoSourceCharacterEveryoneNearbySeesIt()
    {
        var (_, _, thing) = Bench();

        // A delayed call or a console with nobody behind it: upstream broadcasts over
        // the object instead of picking a recipient.
        Assert.True(thing.TryExecuteCommand("MESSAGE", "the lever creaks", new Console(null)));

        Assert.Null(_sent[0].To);
        Assert.Equal("the lever creaks", _sent[0].Text);
    }

    [Fact]
    public void ItIsNotTheSystemMessageVerb()
    {
        // SYSMESSAGE writes in the corner and goes to the console that asked; MESSAGE
        // belongs to the object. Keeping them apart is the whole point.
        var (_, asker, thing) = Bench();

        Assert.True(thing.TryExecuteCommand("SYSMESSAGE", "corner text", new Console(asker)));

        Assert.Empty(_sent);
    }
}
