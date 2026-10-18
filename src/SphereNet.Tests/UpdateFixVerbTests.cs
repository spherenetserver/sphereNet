using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// OV_UPDATE / OV_FIX (CObjBase.cpp:2972, :3043) end in CChar::Update: the
/// character's own client is resynced (addReSync). Neither is the script RESYNC.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class UpdateFixVerbTests
{
    private static Character MakeChar()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    [Theory]
    [InlineData("UPDATE")]
    [InlineData("UPDATEX")]
    [InlineData("FIX")]
    public void Verb_ResyncsOwnClient(string verb)
    {
        var ch = MakeChar();
        int resyncs = 0;
        Character.OnOwnClientResync = c => { if (c == ch) resyncs++; };
        try
        {
            Assert.True(ch.TryExecuteCommand(verb, "", new NullConsole()));
            Assert.Equal(1, resyncs);
        }
        finally { Character.OnOwnClientResync = null; }
    }

    private sealed class NullConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public SphereNet.Core.Enums.PrivLevel GetPrivLevel() => SphereNet.Core.Enums.PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }
}
