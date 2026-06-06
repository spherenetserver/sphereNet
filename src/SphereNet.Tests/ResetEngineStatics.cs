using System.Reflection;
using Xunit.Sdk;

[assembly: SphereNet.Tests.ResetEngineStatics]

namespace SphereNet.Tests;

// The engine exposes process-wide mutable static hooks that tests wire in their
// setup (CreateWorld / AttachCharacter):
//   ObjBase.ResolveWorld, Item.ResolveWorld   — resolve the ambient world
//   Character.ResolveAccountForChar           — drives Character.PrivLevel
//   VendorEngine.World                         — vendor trade world
// If one test leaves a hook set, a later test (whose object UIDs can collide
// across its own fresh world) resolves against the stale hook — e.g. a player
// reading another test's GM account and gaining GM privileges, or a move
// resolving against the wrong world. xUnit does not guarantee test order, so
// this surfaced as intermittent, order-dependent CI failures.
//
// Reset every hook to null before AND after each test so each test starts from
// a clean slate and re-establishes only what its own setup wires. Combined with
// assembly-wide serialization (see TestParallelization.cs) this makes the suite
// deterministic regardless of execution order.
public sealed class ResetEngineStaticsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => Reset();
    public override void After(MethodInfo methodUnderTest) => Reset();

    private static void Reset()
    {
        SphereNet.Game.Objects.ObjBase.ResolveWorld = null;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = null;
        SphereNet.Game.Objects.Characters.Character.ResolveAccountForChar = null;
        SphereNet.Game.Trade.VendorEngine.World = null;
        SphereNet.Scripting.Definitions.CharDef.DefNameResolver = null;
        SphereNet.Game.Objects.Characters.Character.OnFameChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnKarmaChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnMurderMark = null;
        SphereNet.Game.Objects.Characters.Character.OnCombatAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnCombatDelete = null;
        SphereNet.Game.Objects.Characters.Character.OnCombatEnd = null;
        SphereNet.Game.Objects.Characters.Character.OnMurderDecay = null;
        SphereNet.Game.Objects.Characters.Character.OnNotoSend = null;
        SphereNet.Game.Objects.Characters.Character.OnPersonalSpace = null;
        SphereNet.Game.Objects.Characters.Character.OnEffectAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnPetDesert = null;
        SphereNet.Game.Objects.Characters.Character.OnJailed = null;
        SphereNet.Game.Objects.Characters.Character.OnMemoryEquip = null;
        SphereNet.Game.Objects.Characters.Character.OnEnvironChange = null;
        SphereNet.Game.Objects.Characters.Character.OnSkillUseQuick = null;
        SphereNet.Game.Housing.House.OnRedeed = null;
    }
}
