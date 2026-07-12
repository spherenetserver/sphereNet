using SphereNet.Scripting.Variables;

namespace SphereNet.Tests;

public sealed class SourceXVarMapWave223Tests
{
    [Fact]
    public void SetInt_PreservesNativeIntegerTypeAndValue()
    {
        var vars = new VarMap();
        vars.SetInt("Counter", long.MaxValue);

        Assert.True(vars.IsInteger("counter"));
        Assert.Equal(long.MaxValue, vars.GetInt("COUNTER"));
        VarEntry entry = Assert.Single(vars.GetAllEntries());
        Assert.Equal(VarValueKind.Integer, entry.Kind);
        Assert.Equal(long.MaxValue, entry.IntegerValue);
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Value);
    }

    [Fact]
    public void SetString_PreservesNumericLookingTextButStillSupportsNumericRead()
    {
        var vars = new VarMap();
        vars.Set("Padded", "00042");

        Assert.False(vars.IsInteger("Padded"));
        Assert.Equal("00042", vars.Get("Padded"));
        Assert.Equal(42, vars.GetInt("Padded"));
    }

    [Fact]
    public void Enumeration_IsCaseInsensitiveKeySortedAndCopyPreservesTypes()
    {
        var vars = new VarMap();
        vars.Set("zeta", "last");
        vars.SetInt("Beta", 2);
        vars.Set("alpha", "first");

        Assert.Equal(["alpha", "Beta", "zeta"], vars.GetAll().Select(entry => entry.Key));

        var copy = new VarMap();
        copy.CopyFrom(vars);
        Assert.Equal(["alpha", "Beta", "zeta"], copy.GetAll().Select(entry => entry.Key));
        Assert.True(copy.IsInteger("beta"));
    }
}
