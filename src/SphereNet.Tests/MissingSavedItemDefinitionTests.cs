using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class MissingSavedItemDefinitionTests
{
    [Theory]
    [InlineData(SaveFormat.Text, "i_missing", false, false, 0)]
    [InlineData(SaveFormat.Binary, "i_missing", false, false, 0)]
    [InlineData(SaveFormat.Text, "01234", false, false, 0xEED)]
    [InlineData(SaveFormat.Binary, "01234", false, false, 0xEED)]
    [InlineData(SaveFormat.Text, "01234", true, false, 0xF7A)]
    [InlineData(SaveFormat.Binary, "01234", true, false, 0xF7A)]
    [InlineData(SaveFormat.Text, "", false, false, 0xEED)]
    [InlineData(SaveFormat.Binary, "", false, false, 0xEED)]
    [InlineData(SaveFormat.Text, "01234", false, true, 0)]
    [InlineData(SaveFormat.Binary, "01234", false, true, 0)]
    [InlineData(SaveFormat.Text, "i_valid", false, false, 0xF7A)]
    [InlineData(SaveFormat.Binary, "i_valid", false, false, 0xF7A)]
    public void MissingDefinitionsFollowSourceXRules(SaveFormat format, string header, bool customDefault, bool missingDefault, int expected)
    {
        using var logs = TestHarness.CreateLoggerFactory();
        string dir = Path.Combine(Path.GetTempPath(), $"missing-item-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using (var w = SaveIO.OpenWriter(Path.Combine(dir, "sphereworld" + SaveIO.ExtensionFor(format)), format))
            {
                w.BeginRecord("WORLDITEM" + (header.Length == 0 ? "" : " " + header));
                w.WriteProperty("SERIAL", "040000001");
                // An ID cannot rescue an undefined symbolic header.
                if (header is "" or "i_missing") w.WriteProperty("ID", "01234");
                w.WriteProperty("P", "100,100,0,0");
                w.WriteProperty("AMOUNT", "7");
                w.EndRecord();
            }
            var loader = new WorldLoader(logs)
            {
                ResolveItemDef = name => name == "i_valid" ? (ushort)0xF7A : (ushort)0,
                ResolveItemDefFullIndex = name => name == "i_valid" ? 0xF7A : name == "DEFAULTITEM" && customDefault ? 0x10001 : 0,
                ResolveItemBase = index => missingDefault ? null : index == 0x10001 ? (ushort)0xF7A : index is 0xEED or 0xF7A ? (ushort)index : null
            };
            var world = TestHarness.CreateWorld(); loader.Load(world, dir);
            var item = world.FindItem(new Serial(0x40000001));
            if (expected == 0) Assert.Null(item);
            else
            {
                Assert.NotNull(item); Assert.Equal((ushort)expected, item.BaseId); Assert.Equal((ushort)7, item.Amount);
                if (customDefault) Assert.Equal("65537", item.Tags.Get("SCRIPTDEF"));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
