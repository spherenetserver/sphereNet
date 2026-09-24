using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>.edit on a character lists the spell effects it wears (upstream: IT_SPELL
/// items on LAYER_SPECIAL). They are built without a world uid, and Serial counts 0
/// as valid, so the menu took them for world objects and answered "Object not found:
/// 0x00000000" instead of opening them.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class EditSpellEffectMemoryTests
{
    [Fact]
    public void PickingASpellEffectOpensItInsteadOfObjectNotFound()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7992);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.PrivLevel = PrivLevel.Owner; me.Name = "Mortal";
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        me.Memory_CreateSpellEffect(17, 0x2085, 500, me.Uid, "Bless");

        client.ShowInspectDialog(me.Uid.Value);
        TestHarness.ClearQueuedPackets(client.NetState);
        client.HandleEditMenuChoice(1);

        var texts = TestHarness.GetQueuedPackets(client.NetState)
            .Where(p => p.Span[0] == 0xAE)
            .Select(p => System.Text.Encoding.BigEndianUnicode.GetString(p.Span[48..].ToArray()))
            .ToList();
        Assert.DoesNotContain(texts, t => t.Contains("not found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(texts, t => t.Contains("[Spell effect] Bless"));
    }
}
