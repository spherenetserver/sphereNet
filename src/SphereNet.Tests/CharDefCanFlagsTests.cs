using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

public class CharDefCanFlagsTests
{
    [Fact]
    public void LoadFromKey_Can_ParsesSymbolicMovementFlags()
    {
        var def = new CharDef(ResourceId.Invalid);
        def.LoadFromKey("CAN", "MT_RUN|MT_WALK");

        // Regression: symbolic CAN= used to fail numeric parse and leave None,
        // stripping every such creature of its movement capabilities.
        Assert.True((def.Can & CanFlags.C_Run) != 0);
        Assert.True((def.Can & CanFlags.C_Walk) != 0);
        Assert.Equal(CanFlags.None, def.Can & CanFlags.C_Fly);
    }

    [Fact]
    public void LoadFromKey_Can_ParsesNumericAndMixedForms()
    {
        var hex = new CharDef(ResourceId.Invalid);
        hex.LoadFromKey("CAN", "0x2004"); // C_Run | C_Walk
        Assert.True((hex.Can & CanFlags.C_Run) != 0 && (hex.Can & CanFlags.C_Walk) != 0);

        var fly = new CharDef(ResourceId.Invalid);
        fly.LoadFromKey("CAN", "MT_FLY|MT_WALK");
        Assert.True((fly.Can & CanFlags.C_Fly) != 0 && (fly.Can & CanFlags.C_Walk) != 0);
        Assert.Equal(CanFlags.None, fly.Can & CanFlags.C_Run);
    }
}
