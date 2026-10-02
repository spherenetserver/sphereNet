using System.Reflection;
using Xunit;

namespace SphereNet.Tests;

/// <summary>DB.QUERY / DB.EXECUTE send their line raw (Source-X CDataBase.cpp:496-501,
/// GetArgRaw). Trimming every quote off both ends broke a statement that ends with a
/// quoted value: "... type = "Online"" lost its closing quote.</summary>
public sealed class DbStatementUnwrapTests
{
    private static string Unwrap(string arg)
    {
        var program = typeof(SphereNet.Server.Program);
        var method = program.GetMethod("UnwrapDbStatement", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [arg])!;
    }

    [Fact]
    public void AStatementEndingWithAQuotedValueIsSentUntouched()
    {
        const string sql = "select * from player_points WHERE p_uid = \"0123\" and type = \"Online\"";
        Assert.Equal(sql, Unwrap(sql));
    }

    [Fact]
    public void AStatementWrappedWholeInQuotesLosesOnlyThatPair()
    {
        Assert.Equal("SELECT 1", Unwrap("\"SELECT 1\""));
        Assert.Equal("x = \"a\"", Unwrap("\"x = \"a\"\""));
    }

    [Fact]
    public void APlainStatementIsUnchanged()
    {
        Assert.Equal("DELETE FROM t", Unwrap("DELETE FROM t"));
    }
}
