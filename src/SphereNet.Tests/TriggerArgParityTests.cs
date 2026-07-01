using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using Xunit;

namespace SphereNet.Tests;

// Faz 2 — trigger argument parity. Verifies the ARG contract a script sees when a
// trigger fires through the dispatcher (TriggerDispatcher.RunWrapped / WrapArgs):
// ARGN1/2/3 are all seeded from the caller, and ARGN mutations copy back.
public class TriggerArgParityTests
{
    [Fact]
    public void TriggerArgs_AllThreeArgn_AreSeededIntoScript()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_argn_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_argn_probe]
            ON=@Attack
            TAG.GOTN1=<ARGN1>
            TAG.GOTN2=<ARGN2>
            TAG.GOTN3=<ARGN3>
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Events.Add(stack.Resources.ResolveDefName("e_argn_probe"));
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            // Fire with all three ARGN set. ARGN3 (e.g. @DropOn_* drop-Z) previously
            // read 0 in scripts because WrapArgs never seeded Number3.
            var args = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, N1 = 5, N2 = 6, N3 = 7 };
            stack.Dispatcher.FireCharTrigger(ch, CharTrigger.Attack, args);

            Assert.True(ch.TryGetTag("GOTN1", out var n1) && n1 == "5");
            Assert.True(ch.TryGetTag("GOTN2", out var n2) && n2 == "6");
            Assert.True(ch.TryGetTag("GOTN3", out var n3) && n3 == "7"); // was "0" before the fix
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void TriggerArgs_Argn3_MutationCopiesBackToCaller()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_argn3wb_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_argn3_wb_probe]
            ON=@Attack
            ARGN3=99
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Events.Add(stack.Resources.ResolveDefName("e_argn3_wb_probe"));
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            var args = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, N3 = 1 };
            stack.Dispatcher.FireCharTrigger(ch, CharTrigger.Attack, args);

            // The script's ARGN3 write is read back into the caller's args (Source-X
            // reads pScriptArgs->m_iN3 after OnTrigger).
            Assert.Equal(99, args.N3);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void TriggerArgs_ArgnMutation_PropagatesFromGlobalEventTriggers()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_argn_glob_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_global_argn_probe]
            ON=@Attack
            ARGN1=42
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            // Attach as a GLOBAL player event. This path (RunResourceEventHandlers) —
            // like item ITEMDEF/TEVENTS/TYPEDEF and region/room events — previously
            // bypassed RunWrapped, so ARGN mutations from these trigger sources were
            // silently dropped even though object-EVENTS and char-def triggers kept them.
            stack.Dispatcher.GlobalPlayerEvents.Add(stack.Resources.ResolveDefName("e_global_argn_probe"));

            var args = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, N1 = 1 };
            stack.Dispatcher.FireCharTrigger(ch, CharTrigger.Attack, args);

            Assert.Equal(42, args.N1); // was 1 before the fix
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void TriggerArgs_Args_SeededAndMutationCopiesBack()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_args_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_args_probe]
            ON=@Attack
            TAG.GOTARGS=<ARGS>
            ARGS=rewritten by script
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = TestHarness.CreateWorld();
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Events.Add(stack.Resources.ResolveDefName("e_args_probe"));
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            var args = new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch, S1 = "original text" };
            stack.Dispatcher.FireCharTrigger(ch, CharTrigger.Attack, args);

            // <ARGS> was seeded from the caller's S1...
            Assert.True(ch.TryGetTag("GOTARGS", out var gotArgs) && gotArgs == "original text");
            // ...and the script's ARGS rewrite copies back (Source-X @Speech-style text rewrite).
            Assert.Equal("rewritten by script", args.S1);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
